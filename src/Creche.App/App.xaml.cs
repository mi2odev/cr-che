using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Creche.App.Converters;
using Creche.App.Services;
using Creche.App.ViewModels;
using Creche.App.Views;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Application.Settings;
using Creche.Infrastructure;
using Creche.Infrastructure.Logging;
using Creche.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Creche.App;

public partial class App : System.Windows.Application
{
    // "Global\" makes the mutex visible to every Windows session: one running copy per machine.
    private const string MutexName = @"Global\CrecheManager.SingleInstance.6f1c1a52";
    private const string ActivateEventName = @"Global\CrecheManager.Activate.6f1c1a52";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private IHost? _host;
    private DispatcherTimer? _backupTimer;

    public static IServiceProvider Services { get; private set; } = null!;
    public static ToastService Toasts { get; private set; } = null!;
    public static DialogService Dialogs { get; private set; } = null!;
    public static UiState State { get; private set; } = null!;
    public static ThemeService Themes { get; private set; } = null!;
    public static PrintService Printer { get; private set; } = null!;
    public static ILogger Log { get; private set; } = null!;
    public static ShellViewModel? Shell { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            // Another copy is running: ask it to come to the front, then leave.
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            Shutdown();
            return;
        }
        ListenForActivation();

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = e.Args, ContentRootPath = AppContext.BaseDirectory });
            builder.Services.AddCrecheInfrastructure(builder.Configuration);
            builder.Logging.ClearProviders();
            builder.Logging.AddDebug();
            builder.Logging.Services.AddSingleton<ILoggerProvider>(sp => new FileLoggerProvider(sp.GetRequiredService<IAppPaths>().LogsFolder));
            builder.Services.AddSingleton<UiState>();
            builder.Services.AddSingleton<ThemeService>();
            builder.Services.AddSingleton<ToastService>();
            builder.Services.AddSingleton<DialogService>();
            builder.Services.AddSingleton<PrintService>();
            _host = builder.Build();
            await _host.StartAsync();

            Services = _host.Services;
            Log = Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
            Toasts = Services.GetRequiredService<ToastService>();
            Dialogs = Services.GetRequiredService<DialogService>();
            State = Services.GetRequiredService<UiState>();
            Themes = Services.GetRequiredService<ThemeService>();
            Printer = Services.GetRequiredService<PrintService>();
            var paths = Services.GetRequiredService<IAppPaths>();
            ImageStore.Folder = paths.ImagesFolder;

            DispatcherUnhandledException += (_, args) =>
            {
                Log.LogError(args.Exception, "Unhandled UI exception");
                Toasts.Error(Loc.T("Error.Unexpected", args.Exception.Message));
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                Log.LogError(args.Exception, "Unobserved task exception");
                args.SetObserved();
            };

            // Broken XAML bindings are silent by default: send them to the log file.
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener(Services.GetRequiredService<ILoggerFactory>().CreateLogger("Binding")));

            Log.LogInformation("{App} {Version} starting; data folder: {Folder}", AppInfo.Name, typeof(App).Assembly.GetName().Version, paths.DataFolder);
            Themes.ApplySaved();
            Loc.Instance.SetLanguage(State.Get("language") ?? DefaultLanguage());

            // Applies pending EF Core migrations, switches SQLite to WAL, creates the admin account, seeds demo data if asked.
            await Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            await RunAutomaticBackupAsync();
            _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            _backupTimer.Tick += async (_, _) => await RunAutomaticBackupAsync();
            _backupTimer.Start();

            await RefreshSettingsAsync();
            ShowLogin();
        }
        catch (Exception ex)
        {
            Log?.LogCritical(ex, "Startup failed");
            MessageBox.Show($"{AppInfo.Name} could not start.\n\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static async Task<CrecheSettings> RefreshSettingsAsync()
    {
        var settings = await Services.GetRequiredService<ISettingsService>().GetAsync();
        MoneyConverter.Currency = settings.Currency;
        return settings;
    }

    private static string DefaultLanguage()
    {
        string system = System.Globalization.CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        return system is "fr" or "ar" ? system : "en";
    }

    private static async Task RunAutomaticBackupAsync()
    {
        try
        {
            await Services.GetRequiredService<IBackupService>().RunAutomaticBackupIfDueAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Automatic backup failed");
        }
    }

    public void ShowLogin()
    {
        var login = new LoginWindow();
        MainWindow = login;
        login.Show();
    }

    public void ShowShell()
    {
        var previous = MainWindow;
        Shell = ActivatorUtilities.CreateInstance<ShellViewModel>(Services);
        var window = new MainWindow { DataContext = Shell };
        MainWindow = window;
        window.Show();
        previous?.Close();
        _ = Shell.StartAsync();
    }

    public void SignOut()
    {
        Services.GetRequiredService<IAuthService>().Logout();
        var previous = MainWindow;
        Shell = null;
        ShowLogin();
        previous?.Close();
    }

    /// <summary>Called when a window is closed by the user: quit unless another window took over.</summary>
    public void OnWindowClosed(Window window)
    {
        if (ReferenceEquals(MainWindow, window)) Shutdown();
    }

    /// <summary>Starts a fresh copy (after a restore) once this one has released the single-instance lock.</summary>
    public void Restart()
    {
        ReleaseMutex();
        string? exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Shutdown();
    }

    private void ListenForActivation()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var thread = new Thread(() =>
        {
            while (_activateEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    var window = MainWindow;
                    if (window is null) return;
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                });
            }
        })
        { IsBackground = true, Name = "single-instance" };
        thread.Start();
    }

    private void ReleaseMutex()
    {
        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
            _ownsMutex = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _backupTimer?.Stop();
        if (_host is not null)
        {
            State?.Save();
            Log?.LogInformation("Shutting down");
            _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            _host.Dispose();
        }
        ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

internal sealed class BindingErrorListener(ILogger log) : TraceListener
{
    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) log.LogWarning("{Message}", message);
    }
}

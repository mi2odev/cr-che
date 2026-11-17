using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Creche.App.Services;
using Creche.Application.Common;
using Creche.Application.Services;
using Creche.Domain.Enums;
using Creche.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Creche.App.ViewModels;

public sealed partial class LoginViewModel(IAuthService auth, ISettingsService settings, IDbContextFactory<CrecheDbContext> factory) : ViewModelBase
{
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string CrecheName { get; set; } = "";
    [ObservableProperty] public partial string? LogoFile { get; set; }
    [ObservableProperty] public partial bool ShowFirstRunHint { get; set; }
    [ObservableProperty] public partial Option<string>? Language { get; set; }

    public IReadOnlyList<Option<string>> Languages => Loc.Languages;

    public async Task LoadAsync()
    {
        Language = Loc.Languages.FirstOrDefault(l => l.Value == Loc.Instance.Language);
        Username = App.State.Get("login.username", "");
        await Run(async () =>
        {
            var config = await settings.GetAsync();
            CrecheName = config.Name;
            LogoFile = config.LogoFile;
            await using var db = await factory.CreateDbContextAsync();
            // Until the built-in admin has signed in once, tell the user how to get in.
            ShowFirstRunHint = await db.Users.AnyAsync(u => u.Username == DatabaseInitializer.DefaultAdminUsername && u.MustChangePassword && u.LastLoginAt == null);
            if (ShowFirstRunHint && Username.Length == 0) Username = DatabaseInitializer.DefaultAdminUsername;
        });
    }

    partial void OnLanguageChanged(Option<string>? value)
    {
        if (value is null || value.Value == Loc.Instance.Language) return;
        Loc.Instance.SetLanguage(value.Value);
        App.State.Set("language", value.Value);
    }

    [RelayCommand]
    private async Task Login()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Username) || Password.Length == 0)
        {
            Error = Loc.T("Login.Required");
            return;
        }
        LoginResult? result = null;
        if (!await Run(async () => result = await auth.LoginAsync(Username, Password)) || result is null) return;
        if (!result.Success)
        {
            Error = Loc.T(result.Failure == LoginFailure.Disabled ? "Login.Disabled" : "Login.Invalid");
            Password = "";
            return;
        }

        App.State.Set("login.username", Username.Trim());
        if (result.Session!.MustChangePassword)
        {
            var dialog = App.Dialogs.Create<ChangePasswordViewModel>(true);
            dialog.CurrentPassword = Password;
            if (!App.Dialogs.Show(dialog))
            {
                auth.Logout();
                Password = "";
                return;
            }
        }
        Password = "";
        ((App)WpfApp.Current).ShowShell();
    }
}

public sealed partial class ChangePasswordViewModel : DialogViewModel
{
    private readonly IAuthService _auth;

    public ChangePasswordViewModel(IAuthService auth, bool forced)
    {
        _auth = auth;
        Forced = forced;
        Title = Loc.T("Account.ChangePassword");
    }

    /// <summary>True at first sign-in: the current password is already known and the change is mandatory.</summary>
    public bool Forced { get; }

    [ObservableProperty] public partial string CurrentPassword { get; set; } = "";
    [ObservableProperty] public partial string NewPassword { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPassword { get; set; } = "";
    [ObservableProperty] public partial string Error { get; set; } = "";

    [RelayCommand]
    private async Task Save()
    {
        Error = "";
        if (NewPassword != ConfirmPassword)
        {
            Error = Loc.T("Account.PasswordMismatch");
            return;
        }
        if (await Run(() => _auth.ChangePasswordAsync(CurrentPassword, NewPassword)))
        {
            App.Toasts.Success(Loc.T("Account.PasswordChanged"));
            Close(true);
        }
    }
}

public sealed record NavItem(string Key, string Icon, string Title, Type PageType);

public sealed partial class ShellViewModel : ViewModelBase
{
    private static readonly (string Key, string Icon, Type Page, Permission? Permission)[] Pages =
    [
        ("dashboard", "home", typeof(DashboardViewModel), Permission.Dashboard),
        ("attendance", "check-circle", typeof(AttendanceViewModel), Permission.Attendance),
        ("activity", "book", typeof(ActivityLogViewModel), Permission.ActivityLog),
        ("children", "smile", typeof(ChildrenViewModel), Permission.Children),
        ("families", "users", typeof(FamiliesViewModel), Permission.Families),
        ("groups", "grid", typeof(GroupsViewModel), Permission.GroupsView),
        ("enrolment", "user-plus", typeof(EnrolmentViewModel), Permission.Enrolment),
        ("billing", "file", typeof(BillingViewModel), Permission.Billing),
        ("payments", "card", typeof(PaymentsViewModel), Permission.Payments),
        ("staff", "briefcase", typeof(StaffViewModel), Permission.Staff),
        ("expenses", "trending-down", typeof(ExpensesViewModel), Permission.Expenses),
        ("reports", "chart", typeof(ReportsViewModel), Permission.Reports),
        ("users", "shield", typeof(UsersViewModel), Permission.Users),
        ("settings", "settings", typeof(SettingsViewModel), null),
    ];

    private readonly IServiceProvider _services;
    private readonly ICurrentUser _user;
    private bool _navigating;

    public ShellViewModel(IServiceProvider services, ICurrentUser user)
    {
        _services = services;
        _user = user;
        UserName = user.User?.DisplayName ?? "";
        Role = user.User is { } u ? Loc.Enum(u.Role) : "";
        BuildNavigation();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        App.Themes.Changed += (_, _) => OnPropertyChanged(nameof(ThemeIcon));
    }

    public ObservableCollection<NavItem> Items { get; } = [];
    public ToastService Toasts => App.Toasts;
    public string UserName { get; }
    public string ThemeIcon => App.Themes.IsDark ? "sun" : "moon";

    [ObservableProperty] public partial string Role { get; set; }
    [ObservableProperty] public partial string CrecheName { get; set; } = "";
    [ObservableProperty] public partial string? LogoFile { get; set; }
    [ObservableProperty] public partial NavItem? Selected { get; set; }
    [ObservableProperty] public partial PageViewModel? CurrentPage { get; set; }

    public async Task StartAsync()
    {
        await RefreshBrandingAsync();
        string? last = App.State.Get($"nav.{_user.User?.Username}");
        Selected = Items.FirstOrDefault(i => i.Key == last) ?? Items.FirstOrDefault();
    }

    public async Task RefreshBrandingAsync()
    {
        var settings = await App.RefreshSettingsAsync();
        CrecheName = settings.Name;
        LogoFile = settings.LogoFile;
    }

    private void BuildNavigation()
    {
        Items.ReplaceWith(Pages.Where(p => p.Permission is null || _user.Can(p.Permission.Value))
            .Select(p => new NavItem(p.Key, p.Icon, Loc.T($"Nav.{p.Key}"), p.Page)));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Rebuild translated labels and re-create the current page so every text is in the new language.
        string? key = Selected?.Key;
        _navigating = true;
        BuildNavigation();
        Role = _user.User is { } u ? Loc.Enum(u.Role) : "";
        Selected = Items.FirstOrDefault(i => i.Key == key);
        _navigating = false;
        if (Selected is not null) Show(Selected, null);
    }

    partial void OnSelectedChanged(NavItem? value)
    {
        if (value is null || _navigating) return;
        Show(value, null);
    }

    /// <summary>Opens a page by key, optionally passing an argument (for example the id of a record to open).</summary>
    public void Navigate(string key, object? parameter = null)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (item is null) return;
        _navigating = true;
        Selected = item;
        _navigating = false;
        Show(item, parameter);
    }

    public bool CanNavigate(string key) => Items.Any(i => i.Key == key);

    private void Show(NavItem item, object? parameter)
    {
        var page = (PageViewModel)ActivatorUtilities.CreateInstance(_services, item.PageType);
        page.Parameter = parameter;
        CurrentPage = page;
        App.State.Set($"nav.{_user.User?.Username}", item.Key);
        _ = page.LoadAsync();
    }

    [RelayCommand]
    private void ToggleTheme() => App.Themes.Toggle();

    [RelayCommand]
    private void Search()
    {
        if (!_user.Can(Permission.Search)) return;
        if (!App.Dialogs.Show<SearchViewModel>(out var search) || search.Chosen is not { } hit) return;
        switch (hit.Kind)
        {
            case SearchKind.Child when CanNavigate("children"): Navigate("children", hit.Id); break;
            case SearchKind.Child: Navigate("attendance"); break;
            case SearchKind.Guardian: Navigate("families", hit.RelatedId); break;
            case SearchKind.Staff: Navigate("staff", hit.Id); break;
            case SearchKind.Receipt: Navigate("payments", hit.Id); break;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        ((App)WpfApp.Current).SignOut();
    }
}

public sealed partial class SearchViewModel : DialogViewModel
{
    private readonly ISearchService _search;
    private int _version;

    public SearchViewModel(ISearchService search)
    {
        _search = search;
        Title = Loc.T("Search.Title");
    }

    public ObservableCollection<SearchHit> Results { get; } = [];
    public SearchHit? Chosen { get; private set; }

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial SearchHit? Selected { get; set; }
    [ObservableProperty] public partial bool NoResults { get; set; }

    partial void OnTextChanged(string value) => _ = QueryAsync(value);

    private async Task QueryAsync(string text)
    {
        int version = ++_version;
        await Task.Delay(120);
        if (version != _version) return;
        await Run(async () =>
        {
            var hits = await _search.SearchAsync(text);
            if (version != _version) return;
            Results.ReplaceWith(hits);
            Selected = Results.FirstOrDefault();
            NoResults = hits.Count == 0 && text.Trim().Length >= 2;
        });
    }

    [RelayCommand]
    private void Open(SearchHit? hit)
    {
        hit ??= Selected;
        if (hit is null) return;
        Chosen = hit;
        Close(true);
    }

    [RelayCommand]
    private void Move(string delta)
    {
        if (Results.Count == 0) return;
        int index = Selected is null ? 0 : Results.IndexOf(Selected) + int.Parse(delta);
        Selected = Results[Math.Clamp(index, 0, Results.Count - 1)];
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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

public sealed record NavItem(string Key, string Icon, string Title, string Section, Type PageType)
{
    /// <summary>"Ctrl+3" for the first nine pages, shown as a tooltip.</summary>
    public string? Shortcut { get; init; }
    public string ToolTip => Shortcut is null ? Title : $"{Title}  ({Shortcut})";
}

/// <summary>A one-click task offered on the dashboard and in the command palette.</summary>
public sealed record QuickAction(string Key, string Icon, string NavKey, string? Parameter, string Tone)
{
    public string Title => Loc.T($"Quick.{Key}");
    public string Subtitle => Loc.T($"Quick.{Key}.Hint");

    public static readonly QuickAction[] All =
    [
        new("attendance", "check-circle", "attendance", null, "Success"),
        new("enrol", "user-plus", "enrolment", PageViewModel.NewAction, "Info"),
        new("payment", "card", "payments", PageViewModel.NewAction, "Success"),
        new("activity", "book", "activity", null, "Warning"),
        new("family", "users", "families", PageViewModel.NewAction, "Info"),
        new("expense", "trending-down", "expenses", PageViewModel.NewAction, "Danger"),
        new("invoices", "file", "billing", null, "Warning"),
    ];
}

/// <summary>One line of the command palette: a page, a quick action or a search result.</summary>
public sealed record PaletteItem(string Icon, string Title, string? Subtitle, string Badge, SearchHit? Hit = null, string? NavKey = null, object? Parameter = null);

public sealed record ShortcutItem(string Keys, string Description)
{
    public IReadOnlyList<string> Parts => Keys.Split('+');
}

public sealed partial class ShellViewModel : ViewModelBase
{
    public const double MinZoom = 0.8, MaxZoom = 1.6;

    private static readonly (string Key, string Icon, string Section, Type Page, Permission? Permission)[] Pages =
    [
        ("dashboard", "home", "today", typeof(DashboardViewModel), Permission.Dashboard),
        ("attendance", "check-circle", "today", typeof(AttendanceViewModel), Permission.Attendance),
        ("activity", "book", "today", typeof(ActivityLogViewModel), Permission.ActivityLog),
        ("children", "smile", "people", typeof(ChildrenViewModel), Permission.Children),
        ("families", "users", "people", typeof(FamiliesViewModel), Permission.Families),
        ("groups", "grid", "people", typeof(GroupsViewModel), Permission.GroupsView),
        ("enrolment", "user-plus", "people", typeof(EnrolmentViewModel), Permission.Enrolment),
        ("staff", "briefcase", "people", typeof(StaffViewModel), Permission.Staff),
        ("billing", "file", "money", typeof(BillingViewModel), Permission.Billing),
        ("payments", "card", "money", typeof(PaymentsViewModel), Permission.Payments),
        ("expenses", "trending-down", "money", typeof(ExpensesViewModel), Permission.Expenses),
        ("reports", "chart", "money", typeof(ReportsViewModel), Permission.Reports),
        ("users", "shield", "admin", typeof(UsersViewModel), Permission.Users),
        ("settings", "settings", "admin", typeof(SettingsViewModel), null),
    ];

    private readonly IServiceProvider _services;
    private readonly ICurrentUser _user;
    private readonly Stack<NavItem> _history = new();
    private bool _navigating;

    public ShellViewModel(IServiceProvider services, ICurrentUser user)
    {
        _services = services;
        _user = user;
        UserName = user.User?.DisplayName ?? "";
        Role = user.User is { } u ? Loc.Enum(u.Role) : "";
        IsCompact = App.State.GetBool("ui.compact");
        Zoom = Math.Clamp(App.State.GetDouble("ui.zoom") ?? 1.0, MinZoom, MaxZoom);
        NavView = CollectionViewSource.GetDefaultView(Items);
        NavView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Section)));
        BuildNavigation();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        App.Themes.Changed += (_, _) => OnPropertyChanged(nameof(ThemeIcon));
    }

    public ObservableCollection<NavItem> Items { get; } = [];
    /// <summary>The sidebar list, grouped by section ("Today", "People", "Money", "Admin").</summary>
    public ICollectionView NavView { get; }
    public ToastService Toasts => App.Toasts;
    public string UserName { get; }
    public string ThemeIcon => App.Themes.IsDark ? "sun" : "moon";
    public string ZoomText => $"{Math.Round(Zoom * 100)} %";
    public bool CanGoBack => _history.Count > 0;

    [ObservableProperty] public partial string Role { get; set; }
    [ObservableProperty] public partial string CrecheName { get; set; } = "";
    [ObservableProperty] public partial string? LogoFile { get; set; }
    [ObservableProperty] public partial NavItem? Selected { get; set; }
    [ObservableProperty] public partial PageViewModel? CurrentPage { get; set; }
    [ObservableProperty] public partial bool IsCompact { get; set; }
    [ObservableProperty] public partial double Zoom { get; set; } = 1.0;

    partial void OnIsCompactChanged(bool value) => App.State.Set("ui.compact", value);

    partial void OnZoomChanged(double value)
    {
        App.State.Set("ui.zoom", value);
        OnPropertyChanged(nameof(ZoomText));
    }

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
        var allowed = Pages.Where(p => p.Permission is null || _user.Can(p.Permission.Value)).ToList();
        Items.ReplaceWith(allowed.Select((p, i) => new NavItem(p.Key, p.Icon, Loc.T($"Nav.{p.Key}"), Loc.T($"Nav.Section.{p.Section}"), p.Page)
        {
            Shortcut = i < 9 ? $"Ctrl+{i + 1}" : null,
        }));
    }

    /// <summary>Quick actions the signed-in user is allowed to run.</summary>
    public IEnumerable<QuickAction> AvailableActions => QuickAction.All.Where(a => CanNavigate(a.NavKey));

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Rebuild translated labels and re-create the current page so every text is in the new language.
        string? key = Selected?.Key;
        _navigating = true;
        BuildNavigation();
        _history.Clear();
        OnPropertyChanged(nameof(CanGoBack));
        Role = _user.User is { } u ? Loc.Enum(u.Role) : "";
        Selected = Items.FirstOrDefault(i => i.Key == key);
        _navigating = false;
        if (Selected is not null) Show(Selected, null);
    }

    partial void OnSelectedChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (newValue is null || _navigating) return;
        Remember(oldValue);
        Show(newValue, null);
    }

    private void Remember(NavItem? previous)
    {
        if (previous is null || (_history.Count > 0 && _history.Peek() == previous)) return;
        _history.Push(previous);
        while (_history.Count > 30) _history.TryPop(out _);
        OnPropertyChanged(nameof(CanGoBack));
    }

    /// <summary>Opens a page by key, optionally passing an argument (for example the id of a record to open).</summary>
    public void Navigate(string key, object? parameter = null)
    {
        var item = Items.FirstOrDefault(i => i.Key == key);
        if (item is null) return;
        if (Selected != item) Remember(Selected);
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
    private void GoBack()
    {
        while (_history.TryPop(out var previous))
        {
            if (!Items.Contains(previous)) continue;
            _navigating = true;
            Selected = previous;
            _navigating = false;
            Show(previous, null);
            break;
        }
        OnPropertyChanged(nameof(CanGoBack));
    }

    [RelayCommand]
    private void NavigateIndex(string index)
    {
        if (int.TryParse(index, out int i) && i >= 0 && i < Items.Count) Navigate(Items[i].Key);
    }

    [RelayCommand]
    private void RunAction(QuickAction? action)
    {
        if (action is not null && CanNavigate(action.NavKey)) Navigate(action.NavKey, action.Parameter);
    }

    [RelayCommand]
    private void ToggleSidebar() => IsCompact = !IsCompact;

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(MaxZoom, Math.Round(Zoom + 0.1, 1));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(MinZoom, Math.Round(Zoom - 0.1, 1));

    [RelayCommand]
    private void ZoomReset() => Zoom = 1.0;

    [RelayCommand]
    private void ToggleTheme() => App.Themes.Toggle();

    [RelayCommand]
    private void ShowShortcuts() => App.Dialogs.Show<ShortcutsViewModel>(out _);

    [RelayCommand]
    private void Search()
    {
        // Pages and quick actions are always offered; record search needs the search permission.
        var commands = AvailableActions
            .Select(a => new PaletteItem(a.Icon, a.Title, a.Subtitle, Loc.T("Search.Action"), NavKey: a.NavKey, Parameter: a.Parameter))
            .Concat(Items.Select(i => new PaletteItem(i.Icon, i.Title, i.Section, Loc.T("Search.Page"), NavKey: i.Key)))
            .ToList();
        if (!App.Dialogs.Show<SearchViewModel>(out var search, commands, _user.Can(Permission.Search)) || search.Chosen is not { } chosen) return;
        if (chosen.NavKey is { } key)
        {
            Navigate(key, chosen.Parameter);
            return;
        }
        if (chosen.Hit is not { } hit) return;
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

/// <summary>Ctrl+K: one box to jump to a page, start a task, or find a child, parent, employee or receipt.</summary>
public sealed partial class SearchViewModel : DialogViewModel
{
    private readonly ISearchService _search;
    private readonly IReadOnlyList<PaletteItem> _commands;
    private readonly bool _canSearchRecords;
    private int _version;

    public SearchViewModel(ISearchService search, List<PaletteItem> commands, bool canSearchRecords)
    {
        _search = search;
        _commands = commands;
        _canSearchRecords = canSearchRecords;
        Title = Loc.T("Search.Title");
        Results.ReplaceWith(_commands);
        Selected = Results.FirstOrDefault();
    }

    public ObservableCollection<PaletteItem> Results { get; } = [];
    public PaletteItem? Chosen { get; private set; }
    public string Placeholder => Loc.T(_canSearchRecords ? "Search.PalettePlaceholder" : "Search.CommandsPlaceholder");

    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial PaletteItem? Selected { get; set; }
    [ObservableProperty] public partial bool NoResults { get; set; }

    partial void OnTextChanged(string value) => _ = QueryAsync(value);

    private async Task QueryAsync(string text)
    {
        int version = ++_version;
        string query = text.Trim();
        var commands = query.Length == 0
            ? _commands.ToList()
            : _commands.Where(c => Matches(c.Title, query) || Matches(c.Subtitle, query)).Take(8).ToList();

        if (query.Length < 2 || !_canSearchRecords)
        {
            Results.ReplaceWith(commands);
            Selected = Results.FirstOrDefault();
            NoResults = Results.Count == 0;
            return;
        }

        await Task.Delay(120);
        if (version != _version) return;
        await Run(async () =>
        {
            var hits = await _search.SearchAsync(query);
            if (version != _version) return;
            Results.ReplaceWith(hits.Select(ToItem).Concat(commands));
            Selected = Results.FirstOrDefault();
            NoResults = Results.Count == 0;
        });
    }

    private static bool Matches(string? value, string query)
        => value is not null && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static PaletteItem ToItem(SearchHit hit) => new(
        hit.Kind switch
        {
            SearchKind.Child => "smile",
            SearchKind.Guardian => "users",
            SearchKind.Staff => "briefcase",
            _ => "card",
        },
        hit.Title, hit.Subtitle, Loc.Enum(hit.Kind), Hit: hit);

    [RelayCommand]
    private void Open(PaletteItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        Chosen = item;
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

/// <summary>F1: the list of keyboard shortcuts.</summary>
public sealed class ShortcutsViewModel : DialogViewModel
{
    public ShortcutsViewModel() => Title = Loc.T("Shortcuts.Title");

    public IReadOnlyList<ShortcutItem> Items { get; } =
    [
        new ShortcutItem("Ctrl+K", Loc.T("Shortcuts.Search")),
        new ShortcutItem("Ctrl+1…9", Loc.T("Shortcuts.Pages")),
        new ShortcutItem("Alt+Left", Loc.T("Shortcuts.Back")),
        new ShortcutItem("F5", Loc.T("Shortcuts.Refresh")),
        new ShortcutItem("Ctrl+B", Loc.T("Shortcuts.Sidebar")),
        new ShortcutItem("Ctrl+=", Loc.T("Shortcuts.ZoomIn")),
        new ShortcutItem("Ctrl+-", Loc.T("Shortcuts.ZoomOut")),
        new ShortcutItem("Ctrl+0", Loc.T("Shortcuts.ZoomReset")),
        new ShortcutItem("Ctrl+Shift+L", Loc.T("Shortcuts.Theme")),
        new ShortcutItem("Enter", Loc.T("Shortcuts.Open")),
        new ShortcutItem("Esc", Loc.T("Shortcuts.Close")),
        new ShortcutItem("F1", Loc.T("Shortcuts.Help")),
    ];
}

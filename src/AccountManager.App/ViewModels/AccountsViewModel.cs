using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using AccountManager.App.Services;
using AccountManager.App.Views;
using AccountManager.Core.Models;
using AccountManager.Core.Roblox;
using AccountManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace AccountManager.App.ViewModels;

public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _refresh;

    public AccountsViewModel(AppHost host)
    {
        _host = host;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;

        _host.Accounts.Changed += (_, _) => Ui.RunOnUi(Reload);
        _host.Instances.InstanceChanged += id => Ui.RunOnUi(() => UpdateInstance(id));
        _host.Settings.Changed += (_, _) => Ui.RunOnUi(() =>
        {
            ApplyStreamerMode();
            OnPropertyChanged(nameof(FollowSuggestions));
        });
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FollowSuggestions));

        // Uptime ticks even when nothing else changes.
        _refresh = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshAll(), Dispatcher.CurrentDispatcher);
        _refresh.Start();
        Reload();
    }

    public ObservableCollection<AccountItemViewModel> Items { get; } = [];

    public ICollectionView View { get; }

    public ObservableCollection<AccountItemViewModel> Selected { get; } = [];

    [ObservableProperty]
    private string _search = "";

    /// <summary>Optional place / game link / private server link that overrides each account's default when launching.</summary>
    [ObservableProperty]
    private string _joinInput = "";

    /// <summary>Optional server job id, combined with the place in <see cref="JoinInput"/>.</summary>
    [ObservableProperty]
    private string _jobIdInput = "";

    /// <summary>False: launches join <see cref="JoinInput"/>. True: they follow <see cref="FollowInput"/> into their game.</summary>
    [ObservableProperty]
    private bool _followMode;

    /// <summary>Roblox username to follow; one of the managed accounts or anyone else.</summary>
    [ObservableProperty]
    private string _followInput = "";

    public IReadOnlyList<string> JoinModes { get; } = ["Join a game", "Follow a player"];

    public string SelectedJoinMode
    {
        get => FollowMode ? JoinModes[1] : JoinModes[0];
        set => FollowMode = value == JoinModes[1];
    }

    /// <summary>Usernames offered in the follow box. Empty in streamer mode so names don't show on screen.</summary>
    public IReadOnlyList<string> FollowSuggestions =>
        _host.Settings.Current.StreamerMode ? [] : Items.Select(i => i.Account.Username).Order(StringComparer.OrdinalIgnoreCase).ToList();

    [ObservableProperty]
    private string _joinHint = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private bool _isImporting;

    public bool IsEmpty => Items.Count == 0;

    partial void OnSearchChanged(string value) => View.Refresh();

    partial void OnJoinInputChanged(string value) => UpdateJoinHint();

    partial void OnJobIdInputChanged(string value) => UpdateJoinHint();

    partial void OnFollowModeChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectedJoinMode));
        OnPropertyChanged(nameof(FollowSuggestions));
        UpdateJoinHint();
    }

    partial void OnFollowInputChanged(string value) => UpdateJoinHint();

    private void UpdateJoinHint()
    {
        if (FollowMode)
        {
            string name = FollowInput.Trim();
            JoinHint = name.Length == 0
                ? ""
                : IsValidUsername(name)
                    ? $"Follows {name} into the game they're playing. Their privacy settings must allow joins."
                    : "Roblox usernames are 3–20 letters, digits or underscores.";
            return;
        }

        if (string.IsNullOrWhiteSpace(JoinInput) && string.IsNullOrWhiteSpace(JobIdInput))
        {
            JoinHint = "";
            return;
        }

        JoinTarget? target = JoinTarget.Parse(JoinInput, JobIdInput);
        JoinHint = target is null ? DescribeJoinProblem() : "Joins: " + target;
    }

    /// <summary>Why the join fields can't be used, in words the user can act on.</summary>
    private string DescribeJoinProblem()
    {
        if (!string.IsNullOrWhiteSpace(JobIdInput))
        {
            if (!JoinTarget.TryNormalizeJobId(JobIdInput, out _))
            {
                return "That job ID isn't valid. It looks like 0b7c4f5e-9d2a-4c1b-8e3f-123456789abc.";
            }

            if (JoinTarget.Parse(JoinInput) is not { Kind: JoinKind.Place or JoinKind.Server })
            {
                return "A job ID needs the game too: enter its place ID or game link on the left.";
            }
        }

        return "Not recognised. Paste a place ID, game link or private server link.";
    }

    public void SetSelection(IEnumerable<AccountItemViewModel> items)
    {
        Selected.Clear();
        foreach (AccountItemViewModel item in items)
        {
            Selected.Add(item);
        }

        HasSelection = Selected.Count > 0;
    }

    [RelayCommand]
    private async Task SignInWithBrowserAsync()
    {
        string? cookie = BrowserLoginWindow.SignIn(_host.Paths.BrowserProfiles);
        if (cookie is null)
        {
            return;
        }

        await AddCookieAsync(cookie);
    }

    private async Task AddCookieAsync(string cookie)
    {
        try
        {
            (Account account, bool isNew) = await _host.Accounts.AddOrUpdateFromCookieAsync(cookie);
            _host.Log.Success(isNew ? "Account added." : "Signed in again.", account.DisplayLabel);
            Ui.Success(isNew ? $"Added {account.Username}" : $"Updated {account.Username}");
            _host.Instances.Get(account.Id);
            UpdateInstance(account.Id);
        }
        catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or ArgumentException)
        {
            Ui.Error("Couldn't add the account", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportCookiesAsync()
    {
        var content = new ImportCookiesView();
        var dialog = new ContentDialog
        {
            Title = "Import accounts",
            Content = content,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 640,
        };
        if (await Ui.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(content.Text))
        {
            return;
        }

        IsImporting = true;
        try
        {
            ImportResult result = await _host.Accounts.ImportCookiesAsync(content.Text);
            string summary = $"{result.Added} added, {result.Updated} updated";
            if (result.Errors.Count > 0)
            {
                foreach (string error in result.Errors)
                {
                    _host.Log.Warning(error);
                }

                Ui.Notify("Import finished with problems", $"{summary}. {result.Errors.Count} failed; see Activity for details.", ControlAppearance.Caution);
            }
            else
            {
                Ui.Success("Import finished", summary);
            }
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private async Task LaunchAsync(AccountItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        (bool ok, JoinTarget? target) = await GetJoinOverrideAsync();
        if (!ok)
        {
            return;
        }

        if (IsFollowingItself(item, target))
        {
            Ui.Notify($"{item.Title} can't follow itself", "Pick another account to follow, or switch back to joining a game.", ControlAppearance.Caution);
            return;
        }

        if (!_host.Instances.Enqueue(item.Account, target))
        {
            Ui.Notify($"{item.Title} is already running");
        }
    }

    [RelayCommand]
    private async Task LaunchSelectedAsync()
    {
        (bool ok, JoinTarget? target) = await GetJoinOverrideAsync();
        if (!ok)
        {
            return;
        }

        // An account can't follow itself; when following one of ours, launch the others only.
        List<AccountItemViewModel> targets = (Selected.Count > 0 ? Selected.ToList() : View.Cast<AccountItemViewModel>().ToList())
            .Where(t => !IsFollowingItself(t, target))
            .ToList();
        int queued = _host.Instances.EnqueueMany(targets.Select(t => t.Account), target);
        if (queued > 0)
        {
            int stagger = _host.Settings.Current.LaunchStaggerSeconds;
            Ui.Notify($"Launching {queued} account{(queued == 1 ? "" : "s")}", queued > 1 ? $"One every {stagger} seconds." : "");
        }
    }

    [RelayCommand]
    private void Stop(AccountItemViewModel? item)
    {
        if (item is not null)
        {
            _host.Instances.Stop(item.Id);
        }
    }

    [RelayCommand]
    private void StopSelected()
    {
        foreach (AccountItemViewModel item in Selected.ToList())
        {
            _host.Instances.Stop(item.Id);
        }
    }

    [RelayCommand]
    private void Restart(AccountItemViewModel? item)
    {
        if (item is not null)
        {
            _host.Instances.ResetCrashCount(item.Id);
            _host.Instances.Restart(item.Account);
        }
    }

    [RelayCommand]
    private async Task EditAsync(AccountItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var editor = new AccountEditorViewModel(item.Account);
        var dialog = new ContentDialog
        {
            Title = $"Edit {item.Account.Username}",
            Content = new AccountEditorView { DataContext = editor },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            DialogMaxWidth = 620,
        };
        if (await Ui.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        if (!editor.TryApply(out string? error))
        {
            Ui.Error("Not saved", error!);
            return;
        }

        if (editor.PendingFollowUsername is { } username)
        {
            try
            {
                RobloxUser? user = await _host.Api.GetUserByUsernameAsync(username);
                if (user is null)
                {
                    Ui.Error("User not found", $"No Roblox user is called \"{username}\". Other changes were saved.");
                }
                else
                {
                    item.Account.DefaultTarget = JoinTarget.ForFollow(user.Id, user.Name);
                }
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException)
            {
                Ui.Error("Couldn't look up that user", ex.Message);
            }
        }

        _host.Accounts.Save(item.Account);
        _host.Instances.ReapplyTuning();
        Ui.Success("Saved");
    }

    [RelayCommand]
    private async Task SignInAgainAsync(AccountItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        string? cookie = BrowserLoginWindow.SignIn(_host.Paths.BrowserProfiles, item.Account.Username);
        if (cookie is null)
        {
            return;
        }

        try
        {
            RobloxUser user = await _host.Api.GetAuthenticatedUserAsync(SessionCookie.Normalize(cookie) ?? "");
            if (user.Id != item.Account.UserId
                && !await Ui.ConfirmAsync("Different account", $"You signed in as {user.Name}, not {item.Account.Username}. Add {user.Name} as a separate account?", "Add account"))
            {
                return;
            }
        }
        catch (RobloxApiException ex)
        {
            Ui.Error("Sign-in failed", ex.Message);
            return;
        }

        await AddCookieAsync(cookie);
    }

    [RelayCommand]
    private async Task ValidateAsync(AccountItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            bool valid = await _host.Accounts.ValidateAsync(item.Account);
            if (valid)
            {
                Ui.Success($"{item.Title} is signed in");
            }
            else
            {
                Ui.Notify($"{item.Title} is signed out", "Use Sign in again to refresh it.", ControlAppearance.Caution);
            }
        }
        catch (Exception ex) when (ex is RobloxApiException or HttpRequestException)
        {
            Ui.Error("Couldn't check the account", ex.Message);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenWebsite(AccountItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        string cookie;
        try
        {
            cookie = _host.Accounts.GetCookie(item.Account);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            Ui.Notify($"{item.Title} is signed out", "Use Sign in again to refresh it.", ControlAppearance.Caution);
            return;
        }

        RobloxBrowserWindow.Open(_host.Paths.BrowserProfiles, item.Account, cookie);
    }

    [RelayCommand]
    private void ToggleFavorite(AccountItemViewModel? item)
    {
        if (item is not null)
        {
            item.Account.IsFavorite = !item.Account.IsFavorite;
            _host.Accounts.Save(item.Account);
        }
    }

    [RelayCommand]
    private void CopyUsername(AccountItemViewModel? item)
    {
        if (item is not null && Ui.TrySetClipboard(item.Account.Username))
        {
            Ui.Notify("Username copied");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(AccountItemViewModel? item)
    {
        List<AccountItemViewModel> targets = item is not null && !Selected.Contains(item) ? [item] : Selected.ToList();
        if (targets.Count == 0)
        {
            return;
        }

        string what = targets.Count == 1 ? targets[0].Title : $"{targets.Count} accounts";
        if (!await Ui.ConfirmAsync($"Remove {what}?", "The saved sign-in is deleted from this PC. The Roblox account itself is not affected.", "Remove", destructive: true))
        {
            return;
        }

        foreach (AccountItemViewModel target in targets)
        {
            _host.Instances.Stop(target.Id);
            _host.Accounts.Delete(target.Account);
            _host.Log.Info("Account removed.", target.Account.Username);
        }
    }

    public void MoveItem(AccountItemViewModel item, int newIndex)
    {
        int oldIndex = Items.IndexOf(item);
        if (oldIndex < 0 || newIndex < 0 || newIndex >= Items.Count || oldIndex == newIndex)
        {
            return;
        }

        Items.Move(oldIndex, newIndex);
        _host.Accounts.Reorder(Items.Select(i => i.Account).ToList());
    }

    /// <summary>
    /// The target typed in the launch bar, or null to use each account's own default. Returns false (after telling
    /// the user why) when the input can't be used.
    /// </summary>
    private async Task<(bool Ok, JoinTarget? Target)> GetJoinOverrideAsync()
    {
        if (FollowMode)
        {
            string name = FollowInput.Trim();
            if (name.Length == 0)
            {
                return (true, null);
            }

            if (!IsValidUsername(name))
            {
                Ui.Error("Can't follow that", "Roblox usernames are 3–20 letters, digits or underscores.");
                return (false, null);
            }

            // One of ours: the user id is already known.
            Account? own = _host.Accounts.FindByUsername(name);
            if (own is not null)
            {
                return (true, JoinTarget.ForFollow(own.UserId, own.Username));
            }

            try
            {
                RobloxUser? user = await _host.Api.GetUserByUsernameAsync(name);
                if (user is null)
                {
                    Ui.Error("User not found", $"No Roblox user is called \"{name}\".");
                    return (false, null);
                }

                return (true, JoinTarget.ForFollow(user.Id, user.Name));
            }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or TaskCanceledException)
            {
                Ui.Error("Couldn't look up that user", ex.Message);
                return (false, null);
            }
        }

        if (string.IsNullOrWhiteSpace(JoinInput) && string.IsNullOrWhiteSpace(JobIdInput))
        {
            return (true, null);
        }

        JoinTarget? target = JoinTarget.Parse(JoinInput, JobIdInput);
        if (target is null)
        {
            Ui.Error("Can't join that", DescribeJoinProblem());
            return (false, null);
        }

        return (true, target);
    }

    private static bool IsFollowingItself(AccountItemViewModel item, JoinTarget? target) =>
        target is { Kind: JoinKind.FollowUser } && target.FollowUserId == item.Account.UserId;

    private static bool IsValidUsername(string name) =>
        name.Length is >= 3 and <= 20 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private bool Filter(object obj)
    {
        if (string.IsNullOrWhiteSpace(Search) || obj is not AccountItemViewModel item)
        {
            return true;
        }

        return item.SearchText.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void Reload()
    {
        IReadOnlyList<Account> accounts = _host.Accounts.All;
        var existing = Items.ToDictionary(i => i.Id);
        var next = new List<AccountItemViewModel>();
        for (int i = 0; i < accounts.Count; i++)
        {
            AccountItemViewModel item = existing.TryGetValue(accounts[i].Id, out AccountItemViewModel? found) ? found : new AccountItemViewModel(accounts[i], i);
            item.UpdateAccount(accounts[i], i);
            item.StreamerMode = _host.Settings.Current.StreamerMode;
            item.Apply(_host.Instances.Get(accounts[i].Id));
            next.Add(item);
        }

        Items.Clear();
        foreach (AccountItemViewModel item in next)
        {
            Items.Add(item);
        }

        foreach (AccountItemViewModel stale in Selected.Where(s => !next.Contains(s)).ToList())
        {
            Selected.Remove(stale);
        }

        HasSelection = Selected.Count > 0;
        OnPropertyChanged(nameof(IsEmpty));
        UpdateSummary();
    }

    private void UpdateInstance(long id)
    {
        AccountItemViewModel? item = Items.FirstOrDefault(i => i.Id == id);
        item?.Apply(_host.Instances.Get(id));
        UpdateSummary();
    }

    private void RefreshAll()
    {
        foreach (AccountItemViewModel item in Items)
        {
            if (item.IsActive)
            {
                item.Apply(_host.Instances.Get(item.Id));
            }
        }

        UpdateSummary();
    }

    private void ApplyStreamerMode()
    {
        foreach (AccountItemViewModel item in Items)
        {
            item.StreamerMode = _host.Settings.Current.StreamerMode;
        }
    }

    private void UpdateSummary()
    {
        int running = Items.Count(i => i.StatusText == "Running");
        int queued = Items.Count(i => i.StatusText is "Queued" or "Launching");
        string text = $"{Items.Count} account{(Items.Count == 1 ? "" : "s")} · {running} running";
        if (queued > 0)
        {
            text += $" · {queued} starting";
        }

        Summary = text;
    }
}

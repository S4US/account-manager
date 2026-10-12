using AccountManager.Core.Models;
using AccountManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccountManager.App.ViewModels;

public enum StatusTone
{
    Neutral,
    Success,
    Caution,
    Critical,
    Accent,
}

/// <summary>
/// A section of the account list. One instance per group name lives for the whole session, so its expanded state
/// survives the list being rebuilt.
/// </summary>
public sealed partial class AccountGroup : ObservableObject
{
    private readonly Action<AccountGroup>? _expandedChanged;

    public AccountGroup(string name, bool isExpanded, Action<AccountGroup>? expandedChanged)
    {
        Name = name;
        _isExpanded = isExpanded;
        _expandedChanged = expandedChanged;
    }

    /// <summary>The stored group name; empty for accounts without a group.</summary>
    public string Name { get; }

    public string Title => Name.Length == 0 ? "No group" : Name;

    /// <summary>Named groups alphabetically, then "No group" last.</summary>
    public string SortKey => Name.Length == 0 ? "￿" : Name.ToLowerInvariant();

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => _expandedChanged?.Invoke(this);
}

/// <summary>Where a dragged account would land relative to this row.</summary>
public enum DropPosition
{
    None,
    Before,
    After,
}

/// <summary>One row in the accounts list: the stored account plus its live client state.</summary>
public sealed partial class AccountItemViewModel : ObservableObject
{
    public AccountItemViewModel(Account account, int index)
    {
        Account = account;
        Index = index;
    }

    public Account Account { get; private set; }

    public long Id => Account.Id;

    public int Index { get; private set; }

    [ObservableProperty]
    private bool _streamerMode;

    [ObservableProperty]
    private string _statusText = "Offline";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    private StatusTone _tone;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _memoryText = "";

    [ObservableProperty]
    private string _cpuText = "";

    [ObservableProperty]
    private string _uptimeText = "";

    [ObservableProperty]
    private bool _needsSignIn;

    public string Title => StreamerMode ? $"Account {Index + 1}" : Account.DisplayLabel;

    public string Subtitle
    {
        get
        {
            if (StreamerMode)
            {
                return "Hidden";
            }

            return string.IsNullOrWhiteSpace(Account.Alias) ? $"@{Account.Username}" : $"{Account.Username}";
        }
    }

    public string SubtitleLine => StreamerMode || string.IsNullOrWhiteSpace(Account.Tags) ? Subtitle : $"{Subtitle}  ·  {Account.Tags}";

    public string? AvatarUrl => StreamerMode || string.IsNullOrEmpty(Account.AvatarUrl) ? null : Account.AvatarUrl;

    public string Initial => StreamerMode ? "#" : (Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?");

    public string TargetText => Account.DefaultTarget.IsEmpty ? "Home screen" : Account.DefaultTarget.ToString();

    public bool AutoRestart => Account.AutoRestart;

    public bool AntiAfk => Account.AntiAfk;

    public bool IsOptimized => Account.Optimize;

    public bool HasPassword => !string.IsNullOrEmpty(Account.EncryptedPassword);

    public int SortOrder => Account.SortOrder;

    /// <summary>The section this row is shown in; assigned by the list when it (re)loads.</summary>
    [ObservableProperty]
    private AccountGroup? _group;

    public string GroupSortKey => Group?.SortKey ?? "￿";

    [ObservableProperty]
    private DropPosition _dropPosition;

    partial void OnGroupChanged(AccountGroup? value) => OnPropertyChanged(nameof(GroupSortKey));

    public bool IsFavorite => Account.IsFavorite;

    public string Tags => Account.Tags;

    public bool CanLaunch => !IsActive && !IsBusy;

    public string SearchText => $"{Account.Username} {Account.Alias} {Account.DisplayName} {Account.Tags} {Account.Notes} {Account.Group}";

    public void UpdateAccount(Account account, int index)
    {
        Account = account;
        Index = index;
        OnPropertyChanged(string.Empty);
    }

    partial void OnStreamerModeChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SubtitleLine));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(Initial));
    }

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(CanLaunch));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanLaunch));

    public void Apply(InstanceInfo info)
    {
        IsActive = info.State is InstanceState.Running or InstanceState.Launching or InstanceState.Queued
            or InstanceState.Disconnected or InstanceState.Restarting;
        NeedsSignIn = info.CookieRejected;

        (StatusText, Tone) = info.State switch
        {
            InstanceState.Running => ("Running", StatusTone.Success),
            InstanceState.Launching => ("Launching", StatusTone.Accent),
            InstanceState.Queued => ("Queued", StatusTone.Accent),
            InstanceState.Restarting => ("Restarting", StatusTone.Caution),
            InstanceState.Disconnected => ("Disconnected", StatusTone.Caution),
            InstanceState.Failed => (info.CookieRejected ? "Signed out" : "Failed", StatusTone.Critical),
            _ => ("Offline", StatusTone.Neutral),
        };
        Detail = info.Detail;

        if (info.State == InstanceState.Running)
        {
            MemoryText = info.MemoryMb >= 1024 ? $"{info.MemoryMb / 1024:0.00} GB" : $"{info.MemoryMb:0} MB";
            CpuText = $"{info.CpuPercent:0.0}%";
            UptimeText = info.StartedAt is { } started ? FormatUptime(DateTime.Now - started) : "";
        }
        else
        {
            MemoryText = "";
            CpuText = "";
            UptimeText = "";
        }
    }

    internal static string FormatUptime(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";
}

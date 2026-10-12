using System.Globalization;
using AccountManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AccountManager.App.ViewModels;

public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Edits one account's preferences. Changes are copied to the account only on <see cref="TryApply"/>.</summary>
public sealed partial class AccountEditorViewModel : ObservableObject
{
    private readonly Account _account;

    private readonly string _originalPassword;

    public AccountEditorViewModel(Account account, IReadOnlyList<string>? groups = null, string password = "")
    {
        _account = account;
        GroupNames = groups ?? [];
        Group = account.Group;
        _originalPassword = password;
        Password = password;
        Alias = account.Alias;
        Tags = account.Tags;
        Notes = account.Notes;
        IsFavorite = account.IsFavorite;
        JoinInput = Describe(account.DefaultTarget);
        FollowUsername = account.DefaultTarget.Kind == JoinKind.FollowUser ? account.DefaultTarget.FollowUsername : "";
        FpsCap = FpsCaps.FirstOrDefault(c => c.Value == account.FpsCap) ?? new Choice<int>($"{account.FpsCap} FPS", account.FpsCap);
        UsePerformancePreset = account.UsePerformancePreset;
        Optimize = account.Optimize;
        AutoRestart = account.AutoRestart;
        AntiAfk = account.AntiAfk;
        RamLimit = RamLimits.FirstOrDefault(c => c.Value == account.RamLimitMb) ?? RamLimits[0];
    }

    public static IReadOnlyList<Choice<int>> FpsCaps { get; } =
        [new("Use global setting", 0), .. PerformanceViewModel.FpsCaps.Where(c => c.Value > 0)];

    public static IReadOnlyList<Choice<int>> RamLimits { get; } =
    [
        new("Use global setting", 0),
        new("512 MB", 512),
        new("768 MB", 768),
        new("1 GB", 1024),
        new("1.5 GB", 1536),
        new("2 GB", 2048),
        new("3 GB", 3072),
    ];

    public string Username => _account.Username;

    /// <summary>Existing groups offered in the group box; any new name can be typed too.</summary>
    public IReadOnlyList<string> GroupNames { get; }

    [ObservableProperty]
    private string _group = "";

    /// <summary>Optional account password. Saved encrypted; only used for copying.</summary>
    [ObservableProperty]
    private string _password = "";

    public bool PasswordChanged => Password != _originalPassword;

    [ObservableProperty]
    private string _alias = "";

    [ObservableProperty]
    private string _tags = "";

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private string _joinInput = "";

    [ObservableProperty]
    private string _followUsername = "";

    [ObservableProperty]
    private Choice<int> _fpsCap;

    [ObservableProperty]
    private bool _usePerformancePreset;

    [ObservableProperty]
    private bool _optimize;

    [ObservableProperty]
    private bool _autoRestart;

    [ObservableProperty]
    private bool _antiAfk;

    [ObservableProperty]
    private Choice<int> _ramLimit;

    [ObservableProperty]
    private string _joinHint = "";

    partial void OnJoinInputChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            JoinHint = "Opens to the Roblox home screen.";
            return;
        }

        JoinTarget? target = JoinTarget.Parse(value);
        JoinHint = target is null ? "Not recognised. Use a place ID, game link or private server link." : "Joins: " + target;
    }

    /// <summary>Validates input and copies it to the account. Follow targets are resolved by the caller.</summary>
    public bool TryApply(out string? error)
    {
        error = null;
        JoinTarget target = JoinTarget.None;
        if (!string.IsNullOrWhiteSpace(FollowUsername))
        {
            // Resolved to a user id at launch time would need the API; keep the name and id together.
            if (_account.DefaultTarget.Kind == JoinKind.FollowUser
                && _account.DefaultTarget.FollowUsername.Equals(FollowUsername.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                target = _account.DefaultTarget;
            }
            else
            {
                PendingFollowUsername = FollowUsername.Trim();
            }
        }
        else if (!string.IsNullOrWhiteSpace(JoinInput))
        {
            JoinTarget? parsed = JoinTarget.Parse(JoinInput);
            if (parsed is null)
            {
                error = "The join link wasn't recognised. Use a place ID, a game link or a private server link.";
                return false;
            }

            target = parsed;
        }

        _account.Alias = Alias.Trim();
        _account.Group = Group.Trim();
        _account.Tags = string.Join(", ", Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _account.Notes = Notes.Trim();
        _account.IsFavorite = IsFavorite;
        if (PendingFollowUsername is null)
        {
            _account.DefaultTarget = target;
        }

        _account.FpsCap = FpsCap.Value;
        _account.UsePerformancePreset = UsePerformancePreset;
        _account.Optimize = Optimize;
        _account.AutoRestart = AutoRestart;
        _account.AntiAfk = AntiAfk;
        _account.RamLimitMb = RamLimit.Value;
        return true;
    }

    /// <summary>Set when the user entered a new username to follow; the caller resolves it to a user id.</summary>
    public string? PendingFollowUsername { get; private set; }

    private static string Describe(JoinTarget target) => target.Kind switch
    {
        JoinKind.Place => target.PlaceId.ToString(CultureInfo.InvariantCulture),
        JoinKind.Server => $"{target.PlaceId} {target.JobId}",
        JoinKind.PrivateServer when !string.IsNullOrEmpty(target.ShareCode) => $"https://www.roblox.com/share?code={target.ShareCode}&type=Server",
        JoinKind.PrivateServer => $"https://www.roblox.com/games/{target.PlaceId}?privateServerLinkCode={target.LinkCode}",
        _ => "",
    };
}

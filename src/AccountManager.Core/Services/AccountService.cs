using AccountManager.Core.Models;
using AccountManager.Core.Roblox;
using AccountManager.Core.Security;
using AccountManager.Core.Storage;

namespace AccountManager.Core.Services;

public sealed record ImportResult(int Added, int Updated, IReadOnlyList<string> Errors);

/// <summary>Account CRUD on top of the store, with cookie encryption and validation against Roblox.</summary>
public sealed class AccountService
{
    private readonly AccountStore _store;
    private readonly Vault _vault;
    private readonly RobloxApi _api;
    private readonly object _lock = new();
    private List<Account> _accounts = [];

    public AccountService(AccountStore store, Vault vault, RobloxApi api)
    {
        _store = store;
        _vault = vault;
        _api = api;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<Account> All
    {
        get
        {
            lock (_lock)
            {
                return _accounts.ToList();
            }
        }
    }

    public Account? Find(long id)
    {
        lock (_lock)
        {
            return _accounts.FirstOrDefault(a => a.Id == id);
        }
    }

    public Account? FindByUsername(string username)
    {
        lock (_lock)
        {
            return _accounts.FirstOrDefault(a => a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Load()
    {
        List<Account> loaded = _store.GetAll();
        lock (_lock)
        {
            _accounts = loaded;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string GetCookie(Account account) => _vault.Decrypt(account.EncryptedCookie);

    /// <summary>The saved password, or "" when none was saved. Throws CryptographicException after a vault reset.</summary>
    public string GetPassword(Account account) =>
        string.IsNullOrEmpty(account.EncryptedPassword) ? "" : _vault.Decrypt(account.EncryptedPassword);

    /// <summary>Encrypts and sets the password (not saved until <see cref="Save"/>). Null or empty removes it.</summary>
    public void SetPassword(Account account, string? password) =>
        account.EncryptedPassword = string.IsNullOrEmpty(password) ? "" : _vault.Encrypt(password);

    /// <summary>Distinct group names in use, sorted.</summary>
    public IReadOnlyList<string> Groups => All
        .Select(a => a.Group.Trim())
        .Where(g => g.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Moves accounts into a group ("" for none) with one reload at the end.</summary>
    public void MoveToGroup(IEnumerable<Account> accounts, string group)
    {
        foreach (Account account in accounts)
        {
            account.Group = group.Trim();
            _store.Save(account);
        }

        Load();
    }

    /// <summary>Saves a new list order, optionally moving one account into a group in the same step.</summary>
    public void Reorder(IReadOnlyList<Account> ordered, Account? movedAccount, string? newGroup)
    {
        if (movedAccount is not null && newGroup is not null)
        {
            movedAccount.Group = newGroup.Trim();
            _store.Save(movedAccount);
        }

        _store.SaveOrder(ordered);
        Load();
    }

    /// <summary>
    /// Validates the cookie with Roblox and stores the account it belongs to. If the account already exists its
    /// cookie is replaced (for example after signing in again). Returns the stored account and whether it was new.
    /// </summary>
    public async Task<(Account Account, bool IsNew)> AddOrUpdateFromCookieAsync(string rawCookie, CancellationToken ct = default)
    {
        string cookie = SessionCookie.Normalize(rawCookie)
            ?? throw new ArgumentException("That doesn't look like a Roblox session cookie.");

        RobloxUser user = await _api.GetAuthenticatedUserAsync(cookie, ct).ConfigureAwait(false);
        Dictionary<long, string> avatars = await _api.GetAvatarHeadshotsAsync([user.Id], ct).ConfigureAwait(false);

        Account? existing = _store.FindByUserId(user.Id);
        Account account = existing ?? new Account
        {
            BrowserTrackerId = Account.NewBrowserTrackerId(),
            SortOrder = All.Count,
        };
        account.UserId = user.Id;
        account.Username = user.Name;
        account.DisplayName = user.DisplayName;
        account.EncryptedCookie = _vault.Encrypt(cookie);
        account.CookieValidatedAt = DateTime.UtcNow;
        if (avatars.TryGetValue(user.Id, out string? avatar))
        {
            account.AvatarUrl = avatar;
        }

        _store.Save(account);
        Load();
        return (account, existing is null);
    }

    public async Task<ImportResult> ImportCookiesAsync(string text, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        IReadOnlyList<string> cookies = SessionCookie.ParseMany(text);
        int added = 0;
        int updated = 0;
        var errors = new List<string>();
        for (int i = 0; i < cookies.Count; i++)
        {
            try
            {
                (Account _, bool isNew) = await AddOrUpdateFromCookieAsync(cookies[i], ct).ConfigureAwait(false);
                if (isNew)
                {
                    added++;
                }
                else
                {
                    updated++;
                }
            }
            catch (RobloxApiException ex)
            {
                errors.Add($"Cookie {i + 1}: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                errors.Add($"Cookie {i + 1}: network error ({ex.Message})");
            }

            progress?.Report(i + 1);

            // Stay well under Roblox's rate limits when importing many accounts.
            if (i < cookies.Count - 1)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }

        if (cookies.Count == 0)
        {
            errors.Add("No Roblox session cookies were found in the text.");
        }

        return new ImportResult(added, updated, errors);
    }

    /// <summary>Checks the stored cookie. Returns false when Roblox rejects it.</summary>
    public async Task<bool> ValidateAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            RobloxUser user = await _api.GetAuthenticatedUserAsync(GetCookie(account), ct).ConfigureAwait(false);
            account.Username = user.Name;
            account.DisplayName = user.DisplayName;
            account.CookieValidatedAt = DateTime.UtcNow;
            Save(account);
            return true;
        }
        catch (RobloxApiException ex) when (ex.IsUnauthorized)
        {
            return false;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Encrypted with a vault key that no longer exists, so it can't be sent at all.
            return false;
        }
    }

    public async Task RefreshAvatarsAsync(CancellationToken ct = default)
    {
        List<Account> accounts = All.ToList();
        if (accounts.Count == 0)
        {
            return;
        }

        Dictionary<long, string> avatars = await _api.GetAvatarHeadshotsAsync(accounts.Select(a => a.UserId), ct).ConfigureAwait(false);
        foreach (Account account in accounts)
        {
            if (avatars.TryGetValue(account.UserId, out string? url) && url != account.AvatarUrl)
            {
                account.AvatarUrl = url;
                _store.Save(account);
            }
        }

        Load();
    }

    public void Save(Account account)
    {
        _store.Save(account);
        Load();
    }

    public void Delete(Account account)
    {
        _store.Delete(account.Id);
        Load();
    }
}

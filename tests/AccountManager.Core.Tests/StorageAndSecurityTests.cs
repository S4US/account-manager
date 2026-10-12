using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AccountManager.Core.Models;
using AccountManager.Core.Roblox;
using AccountManager.Core.Security;
using AccountManager.Core.Storage;

namespace AccountManager.Core.Tests;

public sealed class VaultTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("am-vault").FullName;

    private string VaultPath => Path.Combine(_dir, "vault.json");

    [Fact]
    public void Round_trips_without_password()
    {
        var vault = new Vault(VaultPath);
        vault.Create();
        string secret = vault.Encrypt("hello");

        var reopened = new Vault(VaultPath);
        Assert.True(reopened.TryUnlockWithoutPassword());
        Assert.Equal("hello", reopened.Decrypt(secret));
        Assert.False(reopened.HasPassword);
    }

    [Fact]
    public void Ciphertext_is_randomized_and_tamper_evident()
    {
        var vault = new Vault(VaultPath);
        vault.Create();

        string a = vault.Encrypt("same");
        string b = vault.Encrypt("same");
        Assert.NotEqual(a, b);

        byte[] blob = Convert.FromBase64String(a[3..]);
        blob[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => vault.Decrypt("v1:" + Convert.ToBase64String(blob)));
    }

    [Fact]
    public void Password_is_required_after_setting_it()
    {
        var vault = new Vault(VaultPath);
        vault.Create();
        string secret = vault.Encrypt("cookie");
        vault.SetPassword("correct horse");

        var reopened = new Vault(VaultPath);
        Assert.True(reopened.HasPassword);
        Assert.False(reopened.TryUnlockWithoutPassword());
        Assert.False(reopened.TryUnlock("wrong"));
        Assert.False(reopened.IsUnlocked);
        Assert.True(reopened.TryUnlock("correct horse"));
        Assert.Equal("cookie", reopened.Decrypt(secret));
    }

    [Fact]
    public void Removing_password_keeps_secrets_readable()
    {
        var vault = new Vault(VaultPath);
        vault.Create();
        string secret = vault.Encrypt("cookie");
        vault.SetPassword("pw");
        vault.SetPassword(null);

        var reopened = new Vault(VaultPath);
        Assert.True(reopened.TryUnlockWithoutPassword());
        Assert.Equal("cookie", reopened.Decrypt(secret));
    }

    [Fact]
    public void Vault_file_does_not_contain_the_password_or_plaintext_key()
    {
        var vault = new Vault(VaultPath);
        vault.Create();
        vault.SetPassword("hunter2-unique");

        string file = File.ReadAllText(VaultPath);
        Assert.DoesNotContain("hunter2-unique", file, StringComparison.Ordinal);

        // With a password set, no copy of the key may remain that DPAPI alone can unwrap.
        JsonNode json = JsonNode.Parse(file)!;
        Assert.Null(json["DpapiKey"]);
        Assert.NotNull(json["Password"]);
    }

    [Fact]
    public void Locked_vault_refuses_to_encrypt()
    {
        var vault = new Vault(VaultPath);
        vault.Create();
        vault.Lock();

        Assert.Throws<InvalidOperationException>(() => vault.Encrypt("x"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public sealed class AccountStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("am-db").FullName;

    [Fact]
    public void Saves_loads_updates_and_deletes()
    {
        var store = new AccountStore(Path.Combine(_dir, "accounts.db"));
        store.Initialize();
        store.Initialize(); // idempotent

        var account = new Account
        {
            UserId = 42,
            Username = "builderman",
            DisplayName = "Builder",
            EncryptedCookie = "v1:abc",
            DefaultTarget = new JoinTarget { Kind = JoinKind.PrivateServer, PlaceId = 9, LinkCode = "L" },
            FpsCap = 30,
            AutoRestart = true,
            RamLimitMb = 768,
            LastLaunchedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };
        long id = store.Save(account);

        Account loaded = Assert.Single(store.GetAll());
        Assert.Equal(id, loaded.Id);
        Assert.Equal("builderman", loaded.Username);
        Assert.False(string.IsNullOrEmpty(loaded.BrowserTrackerId));
        Assert.Equal(JoinKind.PrivateServer, loaded.DefaultTarget.Kind);
        Assert.Equal("L", loaded.DefaultTarget.LinkCode);
        Assert.Equal(30, loaded.FpsCap);
        Assert.True(loaded.AutoRestart);
        Assert.Equal(768, loaded.RamLimitMb);
        Assert.Equal(account.LastLaunchedAt, loaded.LastLaunchedAt);

        loaded.Alias = "Main";
        store.Save(loaded);
        Assert.Equal("Main", store.FindByUserId(42)!.Alias);

        store.Delete(id);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Favorites_sort_first_then_order()
    {
        var store = new AccountStore(Path.Combine(_dir, "accounts.db"));
        store.Initialize();
        store.Save(new Account { UserId = 1, Username = "a", EncryptedCookie = "x", SortOrder = 0 });
        store.Save(new Account { UserId = 2, Username = "b", EncryptedCookie = "x", SortOrder = 1, IsFavorite = true });
        store.Save(new Account { UserId = 3, Username = "c", EncryptedCookie = "x", SortOrder = 2 });

        Assert.Equal(["b", "a", "c"], store.GetAll().Select(a => a.Username));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}

public sealed class ClientSettingsWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("am-flags").FullName;

    private string SettingsDir => Path.Combine(_dir, "ClientSettings");

    private string FlagFile => Path.Combine(SettingsDir, "ClientAppSettings.json");

    [Fact]
    public void Writes_overrides_and_cleans_up_when_empty()
    {
        var preset = new JsonObject { ["FIntDebugForceMSAASamples"] = 0 };
        ClientSettingsWriter.ApplyToVersion(ClientSettingsWriter.BuildOverrides(preset), SettingsDir);

        JsonObject written = Read();
        Assert.Equal(0, (int)written["FIntDebugForceMSAASamples"]!);
        Assert.Null(written["DFIntTaskSchedulerTargetFps"]);

        ClientSettingsWriter.ApplyToVersion(new JsonObject(), SettingsDir);
        Assert.False(Directory.Exists(SettingsDir));
    }

    [Fact]
    public void Preserves_and_restores_user_flags()
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(FlagFile, """{ "FFlagUserOwn": true, "DFIntTaskSchedulerTargetFps": 240 }""");

        var preset = new JsonObject { ["FFlagDisablePostFx"] = true };
        ClientSettingsWriter.ApplyToVersion(ClientSettingsWriter.BuildOverrides(preset), SettingsDir);

        JsonObject merged = Read();
        Assert.True((bool)merged["FFlagUserOwn"]!);
        Assert.True((bool)merged["FFlagDisablePostFx"]!);

        // A later launch without overrides gets exactly the user's flags back.
        ClientSettingsWriter.ApplyToVersion(new JsonObject(), SettingsDir);
        JsonObject restored = Read();
        Assert.Equal(240, (int)restored["DFIntTaskSchedulerTargetFps"]!);
        Assert.Null(restored["FFlagDisablePostFx"]);

        ClientSettingsWriter.RestoreAll([new DirectoryInfo(_dir)]);
        Assert.Equal(240, (int)Read()["DFIntTaskSchedulerTargetFps"]!);
        Assert.False(File.Exists(Path.Combine(SettingsDir, ".account-manager")));
    }

    [Fact]
    public void Default_preset_parses()
    {
        (JsonObject? preset, string? error) = ClientSettingsWriter.ParsePreset(ClientSettingsWriter.DefaultPresetJson);

        Assert.Null(error);
        Assert.NotNull(preset);
        Assert.Equal(ClientSettingsWriter.DefaultPerformancePreset.Count, preset.Count);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"A\": { \"nested\": 1 } }")]
    [InlineData("{ not json")]
    public void Invalid_presets_are_rejected(string json)
    {
        (JsonObject? preset, string? error) = ClientSettingsWriter.ParsePreset(json);

        Assert.Null(preset);
        Assert.False(string.IsNullOrEmpty(error));
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(FlagFile))!;

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

public sealed class FramerateCapFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Directory.CreateTempSubdirectory("am-gbs").FullName, "GlobalBasicSettings_13.xml");

    [Fact]
    public void Replaces_existing_value()
    {
        File.WriteAllText(_path, "<roblox><Item><Properties>\n<int name=\"FramerateCap\">144</int>\n</Properties></Item></roblox>");

        Assert.Equal(144, FramerateCapFile.Read(_path));
        Assert.True(FramerateCapFile.Write(_path, 30));
        Assert.Equal(30, FramerateCapFile.Read(_path));
    }

    [Fact]
    public void Inserts_when_missing_and_clears_read_only()
    {
        File.WriteAllText(_path, "<roblox><Item><Properties>\n<bool name=\"X\">true</bool>\n</Properties></Item></roblox>");
        File.SetAttributes(_path, FileAttributes.ReadOnly);

        Assert.Null(FramerateCapFile.Read(_path));
        Assert.True(FramerateCapFile.Write(_path, 60));
        Assert.Equal(60, FramerateCapFile.Read(_path));
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        Assert.False(FramerateCapFile.Write(_path + ".missing", 30));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(5, 5)]
    [InlineData(45, 45)]
    [InlineData(5000, 1000)]
    public void Keeps_caps_in_range(int requested, int expected)
    {
        Assert.Equal(expected, FramerateCapFile.Normalize(requested));
    }

    [Fact]
    public void Locks_the_file_while_capped_and_unlocks_on_restore()
    {
        File.WriteAllText(_path, "<roblox><Item><Properties>\n<int name=\"FramerateCap\">60</int>\n</Properties></Item></roblox>");

        Assert.True(FramerateCapFile.ApplyForLaunch(_path, 5));
        Assert.Equal(5, FramerateCapFile.Read(_path));
        Assert.True(File.GetAttributes(_path).HasFlag(FileAttributes.ReadOnly));

        // A second launch can change the cap of the locked file.
        Assert.True(FramerateCapFile.ApplyForLaunch(_path, 10));
        Assert.Equal(10, FramerateCapFile.Read(_path));

        Assert.True(FramerateCapFile.RestoreUserCap(_path));
        Assert.Equal(60, FramerateCapFile.Read(_path));
        Assert.False(File.GetAttributes(_path).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Backs_up_the_users_value_once_and_restores_it()
    {
        File.WriteAllText(_path, "<roblox><Item><Properties>\n<int name=\"FramerateCap\">144</int>\n</Properties></Item></roblox>");

        Assert.True(FramerateCapFile.ApplyForLaunch(_path, 30));
        Assert.True(FramerateCapFile.ApplyForLaunch(_path, 60));
        Assert.Equal(60, FramerateCapFile.Read(_path));

        // Even if the session value ends up written back, the backup still holds the user's own.
        FramerateCapFile.Write(_path, 30);
        Assert.True(FramerateCapFile.RestoreUserCap(_path));
        Assert.Equal(144, FramerateCapFile.Read(_path));
        Assert.False(FramerateCapFile.RestoreUserCap(_path));
    }

    [Fact]
    public void A_cap_roblox_cannot_produce_is_not_kept_as_the_users_own()
    {
        File.WriteAllText(_path, "<roblox><Item><Properties>\n<int name=\"FramerateCap\">10</int>\n</Properties></Item></roblox>");

        FramerateCapFile.ApplyForLaunch(_path, 60);
        FramerateCapFile.RestoreUserCap(_path);
        Assert.Null(FramerateCapFile.Read(_path));
    }

    [Fact]
    public void Restore_removes_a_cap_the_user_never_had()
    {
        File.WriteAllText(_path, "<roblox><Item><Properties>\n<bool name=\"X\">true</bool>\n</Properties></Item></roblox>");

        FramerateCapFile.ApplyForLaunch(_path, 30);
        Assert.Equal(30, FramerateCapFile.Read(_path));
        FramerateCapFile.RestoreUserCap(_path);
        Assert.Null(FramerateCapFile.Read(_path));
        Assert.Contains("name=\"X\"", File.ReadAllText(_path));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.SetAttributes(_path, FileAttributes.Normal);
        }

        Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AccountManager.Core.Security;

/// <summary>
/// Protects secrets (session cookies) at rest.
///
/// A random 256-bit data key encrypts every secret with AES-GCM. The data key itself is wrapped:
/// <list type="bullet">
/// <item>without a master password: by Windows DPAPI for the current user, so only this Windows account can read it;</item>
/// <item>with a master password: by AES-GCM under a PBKDF2-SHA256 key, and that blob is additionally DPAPI-protected,
/// so the vault file is useless on another machine or Windows account and still needs the password here.</item>
/// </list>
/// </summary>
public sealed class Vault
{
    public const int Pbkdf2Iterations = 600_000;
    private const string SecretPrefix = "v1:";
    private static readonly byte[] SecretAad = Encoding.UTF8.GetBytes("AccountManager.secret.v1");
    private static readonly byte[] KeyAad = Encoding.UTF8.GetBytes("AccountManager.datakey.v1");
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("AccountManager.vault");

    private readonly string _path;
    private byte[]? _dataKey;

    public Vault(string path)
    {
        _path = path;
    }

    public bool Exists => File.Exists(_path);

    public bool IsUnlocked => _dataKey is not null;

    public bool HasPassword => Exists && ReadFile().Password is not null;

    /// <summary>Creates a new vault protected by DPAPI only.</summary>
    public void Create()
    {
        _dataKey = RandomNumberGenerator.GetBytes(32);
        WriteFile(new VaultFile { DpapiKey = Convert.ToBase64String(DpapiProtect(_dataKey)) });
    }

    /// <summary>Unlocks a DPAPI-only vault. Returns false when a master password is required.</summary>
    public bool TryUnlockWithoutPassword()
    {
        VaultFile file = ReadFile();
        if (file.Password is not null || file.DpapiKey is null)
        {
            return false;
        }

        _dataKey = DpapiUnprotect(Convert.FromBase64String(file.DpapiKey));
        return true;
    }

    public bool TryUnlock(string password)
    {
        VaultFile file = ReadFile();
        if (file.Password is null)
        {
            return TryUnlockWithoutPassword();
        }

        try
        {
            byte[] wrapped = DpapiUnprotect(Convert.FromBase64String(file.Password.WrappedKey));
            byte[] passwordKey = DerivePasswordKey(password, Convert.FromBase64String(file.Password.Salt), file.Password.Iterations);
            _dataKey = AesGcmDecrypt(passwordKey, wrapped, KeyAad);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Sets, changes or (with null/empty) removes the master password. The vault must be unlocked.</summary>
    public void SetPassword(string? password)
    {
        byte[] key = RequireKey();
        if (string.IsNullOrEmpty(password))
        {
            WriteFile(new VaultFile { DpapiKey = Convert.ToBase64String(DpapiProtect(key)) });
            return;
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] passwordKey = DerivePasswordKey(password, salt, Pbkdf2Iterations);
        byte[] wrapped = AesGcmEncrypt(passwordKey, key, KeyAad);
        WriteFile(new VaultFile
        {
            Password = new PasswordBlock
            {
                Salt = Convert.ToBase64String(salt),
                Iterations = Pbkdf2Iterations,
                WrappedKey = Convert.ToBase64String(DpapiProtect(wrapped)),
            },
        });
    }

    public void Lock()
    {
        if (_dataKey is not null)
        {
            CryptographicOperations.ZeroMemory(_dataKey);
            _dataKey = null;
        }
    }

    public string Encrypt(string plaintext)
    {
        byte[] blob = AesGcmEncrypt(RequireKey(), Encoding.UTF8.GetBytes(plaintext), SecretAad);
        return SecretPrefix + Convert.ToBase64String(blob);
    }

    public string Decrypt(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return "";
        }

        if (!protectedValue.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown secret format.");
        }

        byte[] blob = Convert.FromBase64String(protectedValue[SecretPrefix.Length..]);
        return Encoding.UTF8.GetString(AesGcmDecrypt(RequireKey(), blob, SecretAad));
    }

    /// <summary>Destroys the vault file. Every secret encrypted with it becomes unreadable.</summary>
    public void Reset()
    {
        Lock();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private byte[] RequireKey() => _dataKey ?? throw new InvalidOperationException("The vault is locked.");

    private static byte[] DerivePasswordKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    // Layout: nonce(12) | tag(16) | ciphertext
    private static byte[] AesGcmEncrypt(byte[] key, byte[] plaintext, byte[] aad)
    {
        byte[] output = new byte[12 + 16 + plaintext.Length];
        Span<byte> nonce = output.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, output.AsSpan(28), output.AsSpan(12, 16), aad);
        return output;
    }

    private static byte[] AesGcmDecrypt(byte[] key, byte[] blob, byte[] aad)
    {
        if (blob.Length < 28)
        {
            throw new CryptographicException("Ciphertext is too short.");
        }

        byte[] plaintext = new byte[blob.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plaintext, aad);
        return plaintext;
    }

    private static byte[] DpapiProtect(byte[] data) =>
        ProtectedData.Protect(data, DpapiEntropy, DataProtectionScope.CurrentUser);

    private static byte[] DpapiUnprotect(byte[] data) =>
        ProtectedData.Unprotect(data, DpapiEntropy, DataProtectionScope.CurrentUser);

    private VaultFile ReadFile()
    {
        if (!File.Exists(_path))
        {
            throw new InvalidOperationException("The vault has not been created.");
        }

        return JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(_path)) ?? throw new InvalidDataException("The vault file is empty.");
    }

    private void WriteFile(VaultFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;

        public string? DpapiKey { get; set; }

        public PasswordBlock? Password { get; set; }
    }

    private sealed class PasswordBlock
    {
        public string Salt { get; set; } = "";

        public int Iterations { get; set; }

        public string WrappedKey { get; set; } = "";
    }
}

using System.Text.Json;
using AccountManager.Core.Models;

namespace AccountManager.Core.Storage;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
            }
        }
        catch (JsonException)
        {
            // A corrupt settings file should not stop the app; keep a copy and start from defaults.
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }

        return new AppSettings();
    }
}

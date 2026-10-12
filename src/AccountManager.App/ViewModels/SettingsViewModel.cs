using System.Reflection;
using AccountManager.App.Services;
using AccountManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _loading;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        _loading = true;
        StreamerMode = host.Settings.Current.StreamerMode;
        ConfirmKillAll = host.Settings.Current.ConfirmKillAll;
        WebhookUrl = host.Settings.Current.DiscordWebhookUrl;
        NotifyOnCrash = host.Settings.Current.NotifyOnCrash;
        _loading = false;
        HasPassword = host.Vault.HasPassword;
    }

    public string Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public string DataFolder => _host.Paths.Root;

    public UpdatesViewModel Updates => ViewModelLocator.Updates;

    [ObservableProperty] private bool _streamerMode;
    [ObservableProperty] private bool _confirmKillAll;
    [ObservableProperty] private string _webhookUrl = "";
    [ObservableProperty] private bool _notifyOnCrash;
    [ObservableProperty] private bool _hasPassword;
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _passwordMessage = "";
    [ObservableProperty] private bool _passwordHasError;

    partial void OnStreamerModeChanged(bool value) => Save(s => s.StreamerMode = value);

    partial void OnConfirmKillAllChanged(bool value) => Save(s => s.ConfirmKillAll = value);

    partial void OnNotifyOnCrashChanged(bool value) => Save(s => s.NotifyOnCrash = value);

    private void Save(Action<Core.Models.AppSettings> change)
    {
        if (!_loading)
        {
            _host.Settings.Update(change);
        }
    }

    [RelayCommand]
    private void SaveWebhook()
    {
        string url = WebhookUrl.Trim();
        if (url.Length > 0 && !DiscordNotifier.IsValidWebhookUrl(url))
        {
            Ui.Error("That isn't a Discord webhook URL", "It should start with https://discord.com/api/webhooks/");
            return;
        }

        _host.Settings.Update(s => s.DiscordWebhookUrl = url);
        Ui.Success(url.Length == 0 ? "Webhook removed" : "Webhook saved");
    }

    [RelayCommand]
    private async Task TestWebhookAsync()
    {
        SaveWebhook();
        bool ok = await _host.Discord.SendAsync("Test message", "Notifications from Account manager will appear here.", 0x4C8DFF);
        if (ok)
        {
            Ui.Success("Test message sent");
        }
        else
        {
            Ui.Error("Couldn't send the test message", "Check the webhook URL.");
        }
    }

    [RelayCommand]
    private void SetPassword()
    {
        if (NewPassword.Length < 8)
        {
            ShowPasswordMessage("Use at least 8 characters.", true);
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ShowPasswordMessage("The passwords don't match.", true);
            return;
        }

        _host.Vault.SetPassword(NewPassword);
        NewPassword = ConfirmPassword = "";
        HasPassword = true;
        ShowPasswordMessage("Master password set. You'll be asked for it when the app starts.", false);
    }

    [RelayCommand]
    private async Task RemovePasswordAsync()
    {
        if (!await Ui.ConfirmAsync("Remove the master password?", "Saved sign-ins stay encrypted with your Windows account, but the app will open without asking for a password.", "Remove"))
        {
            return;
        }

        _host.Vault.SetPassword(null);
        HasPassword = false;
        ShowPasswordMessage("Master password removed.", false);
    }

    [RelayCommand]
    private void OpenDataFolder() => Ui.OpenFolder(DataFolder);

    [RelayCommand]
    private void OpenRepository() => Ui.OpenUrl("https://github.com/S4US/account-manager");

    private void ShowPasswordMessage(string message, bool isError)
    {
        PasswordMessage = message;
        PasswordHasError = isError;
    }
}

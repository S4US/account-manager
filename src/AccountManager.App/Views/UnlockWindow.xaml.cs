using System.Windows;
using AccountManager.Core.Security;
using Wpf.Ui.Controls;

namespace AccountManager.App.Views;

public enum UnlockResult
{
    Unlocked,
    ResetRequested,
    Cancelled,
}

/// <summary>
/// Asks for the master password at startup. Shown before the main window and the app's services exist,
/// so it talks to the vault directly and uses its own message boxes.
/// </summary>
public partial class UnlockWindow : FluentWindow
{
    private readonly Vault _vault;
    private UnlockResult _result = UnlockResult.Cancelled;
    private int _failures;

    private UnlockWindow(Vault vault)
    {
        _vault = vault;
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public static UnlockResult Prompt(Vault vault)
    {
        var window = new UnlockWindow(vault);
        window.ShowDialog();
        return window._result;
    }

    private async void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        string password = PasswordInput.Password ?? "";
        if (password.Length == 0)
        {
            ShowError("Enter your password.");
            return;
        }

        SetBusy(true);

        // Key derivation is deliberately slow; keep the window responsive while it runs.
        bool unlocked = await Task.Run(() => _vault.TryUnlock(password));
        SetBusy(false);

        if (unlocked)
        {
            _result = UnlockResult.Unlocked;
            Close();
            return;
        }

        _failures++;
        PasswordInput.Password = "";
        PasswordInput.Focus();
        ShowError(_failures < 3
            ? "That password isn't right."
            : "That password isn't right. If you're sure it is, Windows may no longer be able to read the vault, for example after a Windows reinstall or password reset. Use \"Forgot it?\" to start over.");
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = "Start over without your password?",
            Content = "Your accounts and settings stay, but every saved sign-in is erased. You'll need to sign in to each account again before it can launch.",
            PrimaryButtonText = "Erase sign-ins",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "Cancel",
        };

        if (await confirm.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            _result = UnlockResult.ResetRequested;
            Close();
        }
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UnlockButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        PasswordInput.IsEnabled = !busy;
        if (busy)
        {
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}

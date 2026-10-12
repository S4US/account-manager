using System.Diagnostics;
using System.Windows;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace AccountManager.App.Services;

/// <summary>Dialog and notification helpers bound to the main window's hosts.</summary>
public static class Ui
{
    public static ContentDialogService Dialogs { get; } = new();

    public static SnackbarService Snackbar { get; } = new() { DefaultTimeOut = TimeSpan.FromSeconds(4) };

    public static void Notify(string title, string message = "", ControlAppearance appearance = ControlAppearance.Secondary)
    {
        RunOnUi(() =>
        {
            SymbolRegular icon = appearance switch
            {
                ControlAppearance.Success => SymbolRegular.CheckmarkCircle24,
                ControlAppearance.Caution => SymbolRegular.Warning24,
                ControlAppearance.Danger => SymbolRegular.ErrorCircle24,
                _ => SymbolRegular.Info24,
            };
            Snackbar.Show(title, message, appearance, new SymbolIcon(icon), Snackbar.DefaultTimeOut);
        });
    }

    public static void Success(string title, string message = "") => Notify(title, message, ControlAppearance.Success);

    public static void Error(string title, string message = "") => Notify(title, message, ControlAppearance.Danger);

    public static async Task<bool> ConfirmAsync(string title, string message, string primary, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonAppearance = destructive ? ControlAppearance.Danger : ControlAppearance.Primary,
        };
        return await Dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    public static Task<ContentDialogResult> ShowAsync(ContentDialog dialog) => Dialogs.ShowAsync(dialog, CancellationToken.None);

    public static void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
    }

    public static bool TrySetClipboard(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }
}

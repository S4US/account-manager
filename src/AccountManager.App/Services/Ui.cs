using System.Diagnostics;
using System.Windows;
using AccountManager.App.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace AccountManager.App.Services;

/// <summary>Dialog and notification helpers bound to the main window's hosts.</summary>
public static class Ui
{
    public static ContentDialogService Dialogs { get; } = new();

    public static void Notify(string title, string message = "", ControlAppearance appearance = ControlAppearance.Secondary)
    {
        StatusTone tone = appearance switch
        {
            ControlAppearance.Success => StatusTone.Success,
            ControlAppearance.Caution => StatusTone.Caution,
            ControlAppearance.Danger => StatusTone.Critical,
            _ => StatusTone.Neutral,
        };
        RunOnUi(() => Toasts.Show(title, message, tone));
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

    private static readonly TimeSpan SensitiveClipboardLifetime = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Copies text. With <paramref name="sensitive"/>, Windows is told to keep it out of clipboard history and
    /// cloud clipboard, and it is cleared after 30 seconds unless something else was copied since.
    /// </summary>
    public static bool TrySetClipboard(string text, bool sensitive = false)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        if (sensitive)
        {
            // Formats documented by Windows for password managers.
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0]));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                if (sensitive)
                {
                    ClearLater(text);
                }

                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }

    private static void ClearLater(string text)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = SensitiveClipboardLifetime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == text)
                {
                    Clipboard.Clear();
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another app holds the clipboard; leave it.
            }
        };
        timer.Start();
    }

    /// <summary>Asks for a line of text. Returns the trimmed text, or null when cancelled or left empty.</summary>
    public static async Task<string?> PromptAsync(string title, string placeholder, string primary, string initial = "")
    {
        var input = new Wpf.Ui.Controls.TextBox { PlaceholderText = placeholder, Text = initial, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        if (await Dialogs.ShowAsync(dialog, CancellationToken.None) != ContentDialogResult.Primary)
        {
            return null;
        }

        string text = input.Text.Trim();
        return text.Length == 0 ? null : text;
    }
}

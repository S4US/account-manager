using System.Collections.ObjectModel;
using System.Windows.Threading;
using AccountManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace AccountManager.App.Services;

/// <summary>One notification card in the bottom-right corner of the main window.</summary>
public sealed class Toast
{
    public Toast(string title, string message, StatusTone tone)
    {
        Title = title;
        Message = message;
        Tone = tone;
    }

    public string Title { get; }

    public string Message { get; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public StatusTone Tone { get; }

    public SymbolRegular Icon => Tone switch
    {
        StatusTone.Success => SymbolRegular.CheckmarkCircle24,
        StatusTone.Caution => SymbolRegular.Warning24,
        StatusTone.Critical => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24,
    };
}

/// <summary>The stack of visible notifications. Must be used on the UI thread (see <see cref="Ui.Notify"/>).</summary>
public static class Toasts
{
    private const int MaxVisible = 3;

    public static ObservableCollection<Toast> Items { get; } = [];

    public static void Show(string title, string message, StatusTone tone)
    {
        // A repeat of the newest card (for example a double click) refreshes nothing; it would only add noise.
        if (Items.Count > 0 && Items[^1].Title == title && Items[^1].Message == message)
        {
            return;
        }

        var toast = new Toast(title, message, tone);
        Items.Add(toast);
        while (Items.Count > MaxVisible)
        {
            Items.RemoveAt(0);
        }

        // Errors stay a little longer so they can be read.
        TimeSpan visible = tone == StatusTone.Critical ? TimeSpan.FromSeconds(7) : TimeSpan.FromSeconds(4);
        var timer = new DispatcherTimer { Interval = visible };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Items.Remove(toast);
        };
        timer.Start();
    }

    public static void Dismiss(Toast toast) => Items.Remove(toast);
}

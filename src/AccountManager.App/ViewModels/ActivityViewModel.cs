using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AccountManager.App.Services;
using AccountManager.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

public sealed partial class ActivityViewModel : ObservableObject
{
    private const int MaxShown = 1000;
    private readonly AppHost _host;

    public ActivityViewModel(AppHost host)
    {
        _host = host;
        foreach (LogEntry entry in host.Log.Snapshot().TakeLast(MaxShown).Reverse())
        {
            Entries.Add(new LogEntryViewModel(entry));
        }

        View = CollectionViewSource.GetDefaultView(Entries);
        View.Filter = Filter;
        host.Log.Added += entry => Ui.RunOnUi(() =>
        {
            Entries.Insert(0, new LogEntryViewModel(entry));
            if (Entries.Count > MaxShown)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        });
    }

    public ObservableCollection<LogEntryViewModel> Entries { get; } = [];

    public ICollectionView View { get; }

    public IReadOnlyList<string> Filters { get; } = ["All", "Problems only"];

    [ObservableProperty]
    private string _selectedFilter = "All";

    [ObservableProperty]
    private string _search = "";

    partial void OnSelectedFilterChanged(string value) => View.Refresh();

    partial void OnSearchChanged(string value) => View.Refresh();

    private bool Filter(object obj)
    {
        if (obj is not LogEntryViewModel entry)
        {
            return false;
        }

        if (SelectedFilter == "Problems only" && entry.Entry.Severity is not (LogSeverity.Warning or LogSeverity.Error))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(Search)
            || entry.Message.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || (entry.Account?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    private void OpenLogFolder() => Ui.OpenFolder(_host.Paths.Logs);

    [RelayCommand]
    private void OpenRobloxLogs() => Ui.OpenFolder(Core.Roblox.RobloxInstallation.LogsDirectory);

    [RelayCommand]
    private void Clear() => Entries.Clear();
}

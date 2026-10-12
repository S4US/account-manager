using AccountManager.App.Services;

namespace AccountManager.App.ViewModels;

/// <summary>Single instances of the page view models, created on first use.</summary>
public static class ViewModelLocator
{
    private static readonly Lazy<AccountsViewModel> AccountsLazy = new(() => new AccountsViewModel(AppHost.Current));
    private static readonly Lazy<DashboardViewModel> DashboardLazy = new(() => new DashboardViewModel(AppHost.Current));
    private static readonly Lazy<PerformanceViewModel> PerformanceLazy = new(() => new PerformanceViewModel(AppHost.Current));
    private static readonly Lazy<RemoteViewModel> RemoteLazy = new(() => new RemoteViewModel(AppHost.Current));
    private static readonly Lazy<ActivityViewModel> ActivityLazy = new(() => new ActivityViewModel(AppHost.Current));
    private static readonly Lazy<SettingsViewModel> SettingsLazy = new(() => new SettingsViewModel(AppHost.Current));

    public static AccountsViewModel Accounts => AccountsLazy.Value;

    public static DashboardViewModel Dashboard => DashboardLazy.Value;

    public static PerformanceViewModel Performance => PerformanceLazy.Value;

    public static RemoteViewModel Remote => RemoteLazy.Value;

    public static ActivityViewModel Activity => ActivityLazy.Value;

    public static SettingsViewModel Settings => SettingsLazy.Value;
}

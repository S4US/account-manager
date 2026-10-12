using System.Globalization;
using System.Windows;
using AccountManager.App.Services;
using AccountManager.Core.Models;
using AccountManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace AccountManager.App.Views;

/// <summary>An embedded browser on roblox.com signed in as one account (its own persistent profile).</summary>
public partial class RobloxBrowserWindow : FluentWindow
{
    private readonly string _profile;
    private readonly string _cookie;

    private RobloxBrowserWindow(string profilesRoot, Account account, string cookie)
    {
        InitializeComponent();
        _profile = Path.Combine(profilesRoot, account.UserId.ToString(CultureInfo.InvariantCulture));
        _cookie = cookie;
        Title = $"Roblox · {account.DisplayLabel}";
        TitleBar.Title = Title;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => Browser.Dispose();
    }

    public static void Open(string profilesRoot, Account account, string cookie)
    {
        new RobloxBrowserWindow(profilesRoot, account, cookie).Show();
    }

    private async Task InitializeAsync()
    {
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _profile);
            await Browser.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = Browser.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled = false;

            CoreWebView2Cookie session = core.CookieManager.CreateCookie(SessionCookie.Name, _cookie, ".roblox.com", "/");
            session.IsSecure = true;
            session.IsHttpOnly = true;
            core.CookieManager.AddOrUpdateCookie(session);

            core.SourceChanged += (_, _) =>
            {
                AddressText.Text = core.Source;
                BackButton.IsEnabled = core.CanGoBack;
            };
            core.NewWindowRequested += (_, e) =>
            {
                // Keep popups in this window so they stay signed in as this account.
                e.Handled = true;
                core.Navigate(e.Uri);
            };
            core.Navigate("https://www.roblox.com/home");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Ui.Error("WebView2 is missing", "Install the Microsoft Edge WebView2 Runtime to use the built-in browser.");
            Close();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
        {
            Browser.CoreWebView2.GoBack();
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();

    private void OnOpenExternal(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.Source is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Ui.OpenUrl(url);
        }
    }
}

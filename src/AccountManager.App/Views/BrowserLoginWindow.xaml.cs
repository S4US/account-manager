using System.Windows;
using System.Windows.Threading;
using AccountManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace AccountManager.App.Views;

/// <summary>
/// Lets the user sign in on roblox.com in an embedded browser and captures the resulting session cookie.
/// Each sign-in uses a fresh, throwaway browser profile that is deleted afterwards, so sessions never mix.
/// </summary>
public partial class BrowserLoginWindow : FluentWindow
{
    private readonly string _profile;
    private readonly DispatcherTimer _poll;
    private string? _cookie;

    private BrowserLoginWindow(string profilesRoot, string? usernameHint)
    {
        InitializeComponent();
        _profile = Path.Combine(profilesRoot, "signin-" + Guid.NewGuid().ToString("N"));
        if (!string.IsNullOrEmpty(usernameHint))
        {
            HintText.Text = $"Sign in as {usernameHint}. This window closes by itself once you're signed in.";
        }

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await CheckForSessionAsync();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => Cleanup();
    }

    /// <summary>Shows the sign-in window. Returns the session cookie, or null if the user closed it.</summary>
    public static string? SignIn(string profilesRoot, string? usernameHint = null)
    {
        var window = new BrowserLoginWindow(profilesRoot, usernameHint) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        return window._cookie;
    }

    private async Task InitializeAsync()
    {
        try
        {
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _profile);
            await Browser.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SourceChanged += (_, _) => AddressText.Text = TryHost(core.Source);
            core.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            core.Navigate("https://www.roblox.com/login");
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            System.Windows.MessageBox.Show(this, "Signing in needs the Microsoft Edge WebView2 Runtime. Install it from microsoft.com/edge/webview2 and try again.",
                "WebView2 is missing", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private async Task CheckForSessionAsync()
    {
        if (Browser.CoreWebView2 is not { } core)
        {
            return;
        }

        List<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync("https://www.roblox.com");
        string? value = cookies.FirstOrDefault(c => c.Name == SessionCookie.Name)?.Value;
        string? cookie = SessionCookie.Normalize(value);
        if (cookie is null)
        {
            return;
        }

        _poll.Stop();
        _cookie = cookie;
        Close();
    }

    private void Cleanup()
    {
        _poll.Stop();
        Browser.Dispose();

        // The browser process releases the profile folder shortly after it exits.
        string profile = _profile;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20 && Directory.Exists(profile); attempt++)
            {
                try
                {
                    Directory.Delete(profile, recursive: true);
                }
                catch (IOException)
                {
                    await Task.Delay(500);
                }
                catch (UnauthorizedAccessException)
                {
                    await Task.Delay(500);
                }
            }
        });
    }

    private static string TryHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url;
}

using System.Collections.ObjectModel;
using AccountManager.App.Services;
using AccountManager.Core.Remote;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AccountManager.App.ViewModels;

public sealed record PanelAddress(string Url, string Label);

public sealed partial class RemoteViewModel : ObservableObject
{
    private readonly AppHost _host;
    private bool _loading;

    public RemoteViewModel(AppHost host)
    {
        _host = host;
        _loading = true;
        IsEnabled = host.Settings.Current.RemoteEnabled;
        AllowLan = host.Settings.Current.RemoteAllowLan;
        Port = host.Settings.Current.RemotePort;
        _loading = false;
        Refresh();
    }

    public ObservableCollection<PanelAddress> Addresses { get; } = [];

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _allowLan;
    [ObservableProperty] private double _port;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _tokenRevealed;

    public string Token => _host.Settings.Current.RemoteToken;

    public string MaskedToken => string.IsNullOrEmpty(Token) ? "Not generated yet" : TokenRevealed ? Token : new string('•', 24) + Token[^4..];

    partial void OnTokenRevealedChanged(bool value) => OnPropertyChanged(nameof(MaskedToken));

    partial void OnIsEnabledChanged(bool value) => Apply(s => s.RemoteEnabled = value);

    partial void OnAllowLanChanged(bool value) => Apply(s => s.RemoteAllowLan = value);

    partial void OnPortChanged(double value)
    {
        int port = (int)Math.Round(value);
        if (port is >= 1024 and <= 65535)
        {
            Apply(s => s.RemotePort = port);
        }
    }

    private void Apply(Action<Core.Models.AppSettings> change)
    {
        if (_loading)
        {
            return;
        }

        _host.Settings.Update(change);
        string? error = _host.ApplyRemoteSettings();
        if (error is not null)
        {
            Ui.Error("Remote panel couldn't start", error);
        }

        Refresh();
    }

    [RelayCommand]
    private void RegenerateToken()
    {
        _host.Settings.Update(s => s.RemoteToken = RemoteServer.GenerateToken());
        _host.ApplyRemoteSettings();
        Refresh();
        Ui.Notify("New token created", "Devices using the old token are signed out.");
    }

    [RelayCommand]
    private void CopyToken()
    {
        if (!string.IsNullOrEmpty(Token) && Ui.TrySetClipboard(Token))
        {
            Ui.Notify("Token copied");
        }
    }

    [RelayCommand]
    private void OpenPanel(PanelAddress? address)
    {
        if (address is not null)
        {
            // The token travels in the URL fragment, which browsers never send to the server.
            Ui.OpenUrl($"{address.Url}#t={Token}");
        }
    }

    [RelayCommand]
    private void CopyAddress(PanelAddress? address)
    {
        if (address is not null && Ui.TrySetClipboard(address.Url))
        {
            Ui.Notify("Address copied");
        }
    }

    private void Refresh()
    {
        RemoteServer? server = _host.Remote;
        IsRunning = server?.IsRunning == true;
        Addresses.Clear();
        if (server is { IsRunning: true })
        {
            Addresses.Add(new PanelAddress($"http://127.0.0.1:{server.Port}/", "This PC"));
            if (server.AllowLan)
            {
                foreach (string ip in RemoteServer.GetLanAddresses())
                {
                    Addresses.Add(new PanelAddress($"http://{ip}:{server.Port}/", "Local network"));
                }
            }

            Status = server.AllowLan ? $"Running on port {server.Port}. Reachable from your local network." : $"Running on port {server.Port}. Only reachable from this PC.";
        }
        else
        {
            Status = IsEnabled ? "Not running. Check the port and the Activity page." : "Off";
        }

        OnPropertyChanged(nameof(Token));
        OnPropertyChanged(nameof(MaskedToken));
    }
}

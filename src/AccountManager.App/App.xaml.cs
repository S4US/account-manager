using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AccountManager.App.Services;
using AccountManager.App.Views;
using AccountManager.Core.Security;
using AccountManager.Core.Services;
using AccountManager.Core.Storage;
using Wpf.Ui.Controls;

namespace AccountManager.App;

public partial class App : Application
{
    private const string MutexName = @"Local\AccountManager.SingleInstance";
    private const string ActivateEventName = @"Local\AccountManager.Activate";
    private const string ElevatedRestartArg = "--elevated-restart";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;
    private AppPaths? _paths;
    private AppHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 1. One running copy. A second launch asks the first to come forward and exits.
        if (!AcquireSingleInstance(e.Args.Contains(ElevatedRestartArg)))
        {
            Shutdown();
            return;
        }

        // 2. Data folder and settings.
        // ACCOUNTMANAGER_DATA points a development copy at a separate data folder.
        _paths = new AppPaths(Environment.GetEnvironmentVariable("ACCOUNTMANAGER_DATA") is { Length: > 0 } root ? root : null);
        _paths.EnsureCreated();
        var settings = new SettingsStore(_paths.Settings);

        // 3. Vault.
        var vault = new Vault(_paths.Vault);
        bool wasReset;
        try
        {
            bool? opened = await OpenVaultAsync(vault);
            if (opened is null)
            {
                Shutdown();
                return;
            }

            wasReset = opened.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowStartupErrorAsync("Couldn't open your data", $"The app couldn't read its data folder:\n{_paths.Root}\n\n{ex.Message}");
            Shutdown();
            return;
        }

        // 4. Services and main window.
        _host = new AppHost(_paths, settings, vault);
        AppHost.Current = _host;
        _host.Log.Info("Account manager started.");
        if (wasReset)
        {
            _host.Log.Warning("Saved sign-ins were erased. Sign in to each account again before launching it.");
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();

        try
        {
            await _host.StartBackgroundAsync();
        }
        catch (Exception ex)
        {
            LogError("Startup tasks failed: " + ex.Message, ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 6. Stops the remote panel, releases the multi-instance hold and restores the user's flag file.
        try
        {
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            LogError("Error while closing: " + ex.Message, ex);
        }

        _activateEvent?.Dispose();
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private bool AcquireSingleInstance(bool isElevatedRestart)
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew && isElevatedRestart)
            {
                // The copy that restarted us as administrator is still shutting down; give it time to let go.
                try
                {
                    _ownsMutex = _instanceMutex.WaitOne(TimeSpan.FromSeconds(15));
                }
                catch (AbandonedMutexException)
                {
                    _ownsMutex = true;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is elevated and this one isn't, so its mutex can't be opened. It exists, though.
            _ownsMutex = false;
        }

        if (!_ownsMutex)
        {
            try
            {
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                System.Windows.MessageBox.Show("Account manager is already running.", "Account manager", System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var listener = new Thread(ListenForActivation) { IsBackground = true, Name = "Second instance listener" };
        listener.Start();
        return true;
    }

    private void ListenForActivation()
    {
        EventWaitHandle? signal = _activateEvent;
        try
        {
            while (signal is not null && signal.WaitOne())
            {
                Dispatcher.InvokeAsync(() => (MainWindow as MainWindow)?.Activate(fromSecondInstance: true));
            }
        }
        catch (ObjectDisposedException)
        {
            // The app is exiting.
        }
    }

    /// <summary>
    /// Creates or unlocks the vault. Returns false when it opened normally, true when the user chose to erase it
    /// and start over, and null when the user cancelled and the app should exit.
    /// </summary>
    private static async Task<bool?> OpenVaultAsync(Vault vault)
    {
        if (!vault.Exists)
        {
            vault.Create();
            return false;
        }

        try
        {
            if (vault.TryUnlockWithoutPassword())
            {
                return false;
            }

            switch (UnlockWindow.Prompt(vault))
            {
                case UnlockResult.Unlocked:
                    return false;
                case UnlockResult.ResetRequested:
                    ResetVault(vault);
                    return true;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or FormatException)
        {
            // DPAPI keys belong to the Windows account; after a reinstall or a forced password reset they are gone.
            var prompt = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Your saved sign-ins can't be read",
                Content = "Windows can no longer decrypt the file that protects your saved sign-ins. This happens after reinstalling Windows, "
                    + "moving the data folder to another PC or user, or a forced Windows password reset.\n\n"
                    + "You can start over: your accounts and settings stay, but you'll need to sign in to each account again.",
                PrimaryButtonText = "Start over",
                PrimaryButtonAppearance = ControlAppearance.Danger,
                CloseButtonText = "Quit",
            };
            if (await prompt.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                return null;
            }

            ResetVault(vault);
            return true;
        }
    }

    private static void ResetVault(Vault vault)
    {
        vault.Reset();
        vault.Create();
    }

    private static async Task ShowStartupErrorAsync(string title, string message)
    {
        var box = new Wpf.Ui.Controls.MessageBox { Title = title, Content = message, CloseButtonText = "Close" };
        await box.ShowDialogAsync();
    }

    // 5. Unhandled errors: log them and keep running where possible.

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("Unexpected error: " + e.Exception.Message, e.Exception);
        e.Handled = true;

        if (_host is not null && MainWindow is MainWindow { IsLoaded: true })
        {
            Ui.Error("Something went wrong", e.Exception.Message);
        }
        else
        {
            System.Windows.MessageBox.Show(e.Exception.Message, "Account manager: something went wrong", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
            if (_host is null)
            {
                // Failed before the main window existed; there is nothing to return to.
                Shutdown(1);
            }
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        LogError("Fatal error: " + (ex?.Message ?? e.ExceptionObject.ToString()), ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Exception ex = e.Exception.GetBaseException();
        LogError("Background task failed: " + ex.Message, ex);
        e.SetObserved();
    }

    /// <summary>Puts a one-line summary in the Activity log and the full details in an error file next to it.</summary>
    private void LogError(string summary, Exception? details = null)
    {
        try
        {
            AppPaths paths = _paths ?? new AppPaths();
            Directory.CreateDirectory(paths.Logs);
            if (details is not null)
            {
                File.AppendAllText(Path.Combine(paths.Logs, $"errors-{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:HH:mm:ss} {details}{Environment.NewLine}{Environment.NewLine}");
            }

            (_host?.Log ?? new ActivityLog(paths.Logs)).Error(summary);
        }
        catch (Exception)
        {
            // Nowhere left to report it.
        }
    }
}

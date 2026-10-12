using System.Windows;
using System.Windows.Interop;
using AccountManager.App.Services;
using AccountManager.App.Views.Pages;
using AccountManager.Core.Processes;
using Wpf.Ui.Controls;

namespace AccountManager.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        RootNavigation.SetServiceProvider(new PageProvider());
        Ui.Dialogs.SetDialogHost(RootContentDialog);
        Ui.Snackbar.SetSnackbarPresenter(SnackbarPresenter);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(AccountsPage));
    }

    /// <summary>Brings the window to the front, e.g. when a second copy of the app is started.</summary>
    public void Activate(bool fromSecondInstance)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        if (fromSecondInstance)
        {
            // Windows only lets the foreground app take focus; the other copy had it, this one doesn't.
            WindowTools.Focus(new WindowInteropHelper(this).Handle);
        }
        else
        {
            Activate();
        }

        Topmost = true;
        Topmost = false;
    }

    /// <summary>Creates each page once and keeps it, so page state survives navigation.</summary>
    private sealed class PageProvider : IServiceProvider
    {
        private readonly Dictionary<Type, object> _pages = [];

        public object? GetService(Type serviceType)
        {
            if (!_pages.TryGetValue(serviceType, out object? page))
            {
                page = Activator.CreateInstance(serviceType);
                if (page is not null)
                {
                    _pages[serviceType] = page;
                }
            }

            return page;
        }
    }
}

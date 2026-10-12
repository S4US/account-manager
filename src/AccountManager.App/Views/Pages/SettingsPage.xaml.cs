using System.Windows.Controls;
using AccountManager.App.ViewModels;

namespace AccountManager.App.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        DataContext = ViewModelLocator.Settings;
        InitializeComponent();
    }
}

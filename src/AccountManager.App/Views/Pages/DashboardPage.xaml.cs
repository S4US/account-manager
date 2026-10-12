using System.Windows.Controls;
using AccountManager.App.ViewModels;

namespace AccountManager.App.Views.Pages;

public partial class DashboardPage : Page
{
    public DashboardPage()
    {
        DataContext = ViewModelLocator.Dashboard;
        InitializeComponent();
    }
}

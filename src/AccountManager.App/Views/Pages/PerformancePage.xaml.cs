using System.Windows.Controls;
using AccountManager.App.ViewModels;

namespace AccountManager.App.Views.Pages;

public partial class PerformancePage : Page
{
    public PerformancePage()
    {
        DataContext = ViewModelLocator.Performance;
        InitializeComponent();
    }
}

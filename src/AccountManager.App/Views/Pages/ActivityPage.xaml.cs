using System.Windows.Controls;
using AccountManager.App.ViewModels;

namespace AccountManager.App.Views.Pages;

public partial class ActivityPage : Page
{
    public ActivityPage()
    {
        DataContext = ViewModelLocator.Activity;
        InitializeComponent();
    }
}

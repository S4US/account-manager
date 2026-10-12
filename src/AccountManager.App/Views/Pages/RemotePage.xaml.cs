using System.Windows.Controls;
using AccountManager.App.ViewModels;

namespace AccountManager.App.Views.Pages;

public partial class RemotePage : Page
{
    public RemotePage()
    {
        DataContext = ViewModelLocator.Remote;
        InitializeComponent();
    }
}

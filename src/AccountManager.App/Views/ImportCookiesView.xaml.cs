using System.Windows.Controls;

namespace AccountManager.App.Views;

public partial class ImportCookiesView : UserControl
{
    public ImportCookiesView()
    {
        InitializeComponent();
        Loaded += (_, _) => Input.Focus();
    }

    public string Text => Input.Text;
}

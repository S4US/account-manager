using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccountManager.App.ViewModels;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AccountManager.App.Views.Pages;

public partial class AccountsPage : Page
{
    public AccountsPage()
    {
        DataContext = ViewModel;
        InitializeComponent();
    }

    private AccountsViewModel ViewModel => ViewModelLocator.Accounts;

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection(AccountList.SelectedItems.Cast<AccountItemViewModel>());

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(e.OriginalSource) is { } item && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            ViewModel.EditCommand.Execute(item);
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && button.DataContext is AccountItemViewModel item)
        {
            ShowMenu(button, item);
        }
    }

    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement row && row.DataContext is AccountItemViewModel item)
        {
            ShowMenu(row, item);
            e.Handled = true;
        }
    }

    private void ShowMenu(FrameworkElement target, AccountItemViewModel item)
    {
        AccountsViewModel vm = ViewModel;
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        menu.Items.Add(Item("Edit…", SymbolRegular.Edit24, () => vm.EditCommand.Execute(item)));
        if (item.IsActive)
        {
            menu.Items.Add(Item("Restart", SymbolRegular.ArrowClockwise24, () => vm.RestartCommand.Execute(item)));
            menu.Items.Add(Item("Stop", SymbolRegular.Stop24, () => vm.StopCommand.Execute(item)));
        }
        else
        {
            menu.Items.Add(Item("Launch", SymbolRegular.Play24, () => vm.LaunchCommand.Execute(item)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item(item.IsFavorite ? "Remove from favorites" : "Add to favorites", SymbolRegular.Star24, () => vm.ToggleFavoriteCommand.Execute(item)));
        menu.Items.Add(Item("Open roblox.com as this account", SymbolRegular.Globe24, () => vm.OpenWebsiteCommand.Execute(item)));
        menu.Items.Add(Item("Check sign-in", SymbolRegular.ShieldCheckmark24, () => vm.ValidateCommand.Execute(item)));
        menu.Items.Add(Item("Sign in again", SymbolRegular.PersonSync24, () => vm.SignInAgainCommand.Execute(item)));
        menu.Items.Add(Item("Copy username", SymbolRegular.Copy24, () => vm.CopyUsernameCommand.Execute(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(AccountList.SelectedItems.Count > 1 && AccountList.SelectedItems.Contains(item) ? $"Remove {AccountList.SelectedItems.Count} accounts" : "Remove", SymbolRegular.Delete24, () => vm.DeleteCommand.Execute(item)));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, SymbolRegular icon, Action action)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private static AccountItemViewModel? ItemFrom(object source) =>
        (source as FrameworkElement)?.DataContext as AccountItemViewModel
        ?? ((source as FrameworkContentElement)?.DataContext as AccountItemViewModel);
}

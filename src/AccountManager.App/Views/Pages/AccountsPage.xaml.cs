using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AccountManager.App.ViewModels;
using Wpf.Ui.Controls;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AccountManager.App.Views.Pages;

public partial class AccountsPage : Page
{
    private const string DragFormat = "AccountManager.AccountRow";
    private Point _dragStart;
    private AccountItemViewModel? _dragCandidate;
    private AccountItemViewModel? _dropHint;

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
        if (ItemFrom(e.OriginalSource) is { } item && !IsInsideButton(e.OriginalSource))
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
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        int selected = AccountList.SelectedItems.Count > 1 && AccountList.SelectedItems.Contains(item) ? AccountList.SelectedItems.Count : 1;

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

        // Copy
        MenuItem copy = Item("Copy", SymbolRegular.Copy24, null);
        copy.Items.Add(Item("Username", SymbolRegular.Person24, () => vm.CopyUsernameCommand.Execute(item)));
        copy.Items.Add(Item("Password", SymbolRegular.Key24, () => vm.CopyPasswordCommand.Execute(item), item.HasPassword));
        copy.Items.Add(Item("Username:password", SymbolRegular.PersonKey20, () => vm.CopyLoginCommand.Execute(item), item.HasPassword));
        if (!item.HasPassword)
        {
            copy.Items.Add(new MenuItem { Header = "Add a password in Edit to copy it", IsEnabled = false });
        }

        menu.Items.Add(copy);

        // Group and order
        string current = item.Account.Group.Trim();
        MenuItem groups = Item(selected > 1 ? $"Move {selected} to group" : "Move to group", SymbolRegular.Folder24, null);
        foreach (string name in vm.GroupNames.Where(g => !g.Equals(current, StringComparison.OrdinalIgnoreCase)))
        {
            groups.Items.Add(Item(name, SymbolRegular.Folder24, () => vm.MoveToGroup(item, name)));
        }

        if (groups.Items.Count > 0)
        {
            groups.Items.Add(new Separator());
        }

        groups.Items.Add(Item("New group…", SymbolRegular.FolderAdd24, () => _ = vm.MoveToNewGroupAsync(item)));
        if (current.Length > 0 || selected > 1)
        {
            groups.Items.Add(Item("Remove from group", SymbolRegular.FolderProhibited20, () => vm.MoveToGroup(item, "")));
        }

        menu.Items.Add(groups);
        menu.Items.Add(Item("Move up", SymbolRegular.ArrowUp24, () => vm.Nudge(item, -1), vm.CanNudge(item, -1)));
        menu.Items.Add(Item("Move down", SymbolRegular.ArrowDown24, () => vm.Nudge(item, 1), vm.CanNudge(item, 1)));

        menu.Items.Add(new Separator());
        menu.Items.Add(Item(item.IsFavorite ? "Remove from favorites" : "Add to favorites", SymbolRegular.Star24, () => vm.ToggleFavoriteCommand.Execute(item)));
        menu.Items.Add(Item("Open roblox.com as this account", SymbolRegular.Globe24, () => vm.OpenWebsiteCommand.Execute(item)));
        menu.Items.Add(Item("Check sign-in", SymbolRegular.ShieldCheckmark24, () => vm.ValidateCommand.Execute(item)));
        menu.Items.Add(Item("Sign in again", SymbolRegular.PersonSync24, () => vm.SignInAgainCommand.Execute(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(selected > 1 ? $"Remove {selected} accounts" : "Remove", SymbolRegular.Delete24, () => vm.DeleteCommand.Execute(item)));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, SymbolRegular icon, Action? action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Icon = new SymbolIcon(icon), IsEnabled = enabled };
        if (action is not null)
        {
            item.Click += (_, _) => action();
        }

        return item;
    }

    // ---- Drag and drop to rearrange ---------------------------------------------------------------------------

    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(AccountList);
        _dragCandidate = IsInsideButton(e.OriginalSource) ? null : ItemFrom(e.OriginalSource);
    }

    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Vector moved = e.GetPosition(AccountList) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        AccountItemViewModel dragged = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(AccountList, new DataObject(DragFormat, dragged), DragDropEffects.Move);
        ClearDropHint();
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(DragFormat) is AccountItemViewModel dragged && RowAt(e) is { } row && row.Item != dragged)
        {
            e.Effects = DragDropEffects.Move;
            SetDropHint(row.Item, row.After ? DropPosition.After : DropPosition.Before);
        }
        else
        {
            ClearDropHint();
        }

        e.Handled = true;
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => ClearDropHint();

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is AccountItemViewModel dragged && RowAt(e) is { } row && row.Item != dragged)
        {
            ClearDropHint();
            ViewModel.MoveAccount(dragged, row.Item, row.After);
        }

        e.Handled = true;
    }

    /// <summary>The row under the pointer and whether the pointer is on its lower half.</summary>
    private (AccountItemViewModel Item, bool After)? RowAt(DragEventArgs e)
    {
        DependencyObject? hit = AccountList.InputHitTest(e.GetPosition(AccountList)) as DependencyObject;
        while (hit is not null and not ListViewItem)
        {
            hit = VisualTreeHelper.GetParent(hit);
        }

        if (hit is not ListViewItem container || container.DataContext is not AccountItemViewModel item)
        {
            return null;
        }

        return (item, e.GetPosition(container).Y > container.ActualHeight / 2);
    }

    private void SetDropHint(AccountItemViewModel item, DropPosition position)
    {
        if (_dropHint != item)
        {
            ClearDropHint();
        }

        _dropHint = item;
        item.DropPosition = position;
    }

    private void ClearDropHint()
    {
        if (_dropHint is not null)
        {
            _dropHint.DropPosition = DropPosition.None;
            _dropHint = null;
        }
    }

    private static bool IsInsideButton(object source)
    {
        for (var node = source as DependencyObject; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }

            if (node is ListViewItem)
            {
                return false;
            }
        }

        return false;
    }

    private static AccountItemViewModel? ItemFrom(object source) =>
        (source as FrameworkElement)?.DataContext as AccountItemViewModel
        ?? ((source as FrameworkContentElement)?.DataContext as AccountItemViewModel);
}

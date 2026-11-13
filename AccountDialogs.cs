using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DustLoadScada;

internal sealed class CredentialsDialog : Window
{
    private readonly TextBox name = new() { MaxLength = 50 };
    private readonly PasswordBox password = new() { MaxLength = 128 };
    private readonly PasswordBox confirmation = new() { MaxLength = 128 };
    private readonly ComboBox roles = new() { ItemsSource = new[] { "操作员", "负责人" }, SelectedIndex = 0 };

    public CredentialsDialog(string title, bool create, bool chooseRole)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = title;
        Width = 410;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new StackPanel { Margin = new Thickness(22) };
        Content = layout;
        AddField(layout, "用户名", name);
        AddField(layout, create ? "密码（至少 8 个字符）" : "密码", password);
        if (create)
            AddField(layout, "确认密码", confirmation);
        if (chooseRole)
            AddField(layout, "角色", roles);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        layout.Children.Add(buttons);
        var submit = new Button
        {
            Content = create ? "创建账号" : "登录", IsDefault = true,
            Style = (Style)Application.Current.FindResource("Primary"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        submit.Click += (_, _) =>
        {
            if (UserName.Length == 0 || Password.Length == 0)
            {
                MessageBox.Show(this, "请输入用户名及密码。");
                return;
            }
            if (create && (Password.Length < 8 || Password != confirmation.Password))
            {
                MessageBox.Show(this, "密码至少 8 个字符，两次输入须一致。");
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(submit);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true });
        Loaded += (_, _) => name.Focus();
    }

    public string UserName => name.Text.Trim();
    public string Password => password.Password;
    public UserRole Role => roles.SelectedIndex == 1 ? UserRole.Supervisor : UserRole.Operator;

    private static void AddField(Panel layout, string label, Control control)
    {
        layout.Children.Add(new Label
        {
            Content = label, Target = control, Padding = new Thickness(0, 0, 0, 5),
            Foreground = (Brush)Application.Current.FindResource("Muted")
        });
        control.Margin = new Thickness(0, 0, 0, 11);
        layout.Children.Add(control);
    }
}

internal sealed class UsersDialog : Window
{
    private readonly StationStore store;
    private readonly UserAccount current;
    private readonly DataGrid grid = new();

    public UsersDialog(StationStore store, UserAccount current)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        this.store = store;
        this.current = current;
        Title = "用户管理";
        Width = 580;
        Height = 425;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(18) };
        Content = layout;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        MainWindow.AddDialogButton(buttons, "新建账号", AddUser, true);
        MainWindow.AddDialogButton(buttons, "启用／停用选中账号", Toggle);
        MainWindow.AddDialogButton(buttons, "关闭", Close);

        AddColumn("用户名", nameof(UserAccount.Name), new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn("角色", nameof(UserAccount.RoleText), new DataGridLength(120));
        AddColumn("状态", nameof(UserAccount.StateText), new DataGridLength(95));
        layout.Children.Add(grid);
        Reload();
    }

    private void AddColumn(string header, string property, DataGridLength width) =>
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header, Width = width, Binding = new System.Windows.Data.Binding(property)
        });

    private void Reload() => grid.ItemsSource = store.Users();

    private async void AddUser()
    {
        var dialog = new CredentialsDialog("新建账号", true, true) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        string name = dialog.UserName;
        string password = dialog.Password;
        UserRole role = dialog.Role;
        IsEnabled = false;
        try
        {
            await Task.Run(() => store.CreateUser(name, password, role, false));
            Reload();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        finally { IsEnabled = true; }
    }

    private void Toggle()
    {
        if (grid.SelectedItem is not UserAccount account)
            return;
        if (string.Equals(account.Name, current.Name, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "当前登录的账号无法在本次会话中停用。");
            return;
        }
        string action = account.Enabled ? "停用" : "启用";
        if (MessageBox.Show(this, $"确认{action}账号“{account.Name}”？", "用户管理",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            store.ToggleUser(account);
            Reload();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message); }
    }
}

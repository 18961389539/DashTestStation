using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DustLoadScada;

internal sealed class NewRecipeDialog : Window
{
    public NewRecipeDialog(Recipe template, Action<Recipe> create)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "新建配方";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(22) };
        Content = layout;
        var name = AddField(layout, "配方名称", "");
        var model = AddField(layout, "产品型号", template.Model);
        var hint = new TextBlock
        {
            Text = $"测试参数从「{template.Name}」复制。",
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap
        };
        layout.Children.Add(hint);

        var error = new TextBlock
        {
            Foreground = (Brush)FindResource("Red"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };
        layout.Children.Add(error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var confirm = new Button { Content = "创建", IsDefault = true, Style = (Style)FindResource("Primary") };
        confirm.Click += (_, _) =>
        {
            string recipeName = name.Text.Trim();
            string productModel = model.Text.Trim();
            if (recipeName.Length == 0 || productModel.Length == 0)
            {
                bool missingName = recipeName.Length == 0;
                ShowError(missingName ? "请输入配方名称。" : "请输入产品型号。");
                (missingName ? name : model).Focus();
                return;
            }

            var recipe = template with { Id = Guid.NewGuid().ToString("N"), Name = recipeName, Model = productModel };
            try { create(recipe); }
            catch (Exception ex)
            {
                ShowError(ex.Message);
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(confirm);
        Loaded += (_, _) => name.Focus();

        void ShowError(string message)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
        }
    }

    private TextBox AddField(Panel layout, string label, string value)
    {
        var input = new TextBox { Text = value, MaxLength = 80, Margin = new Thickness(0, 0, 0, 12) };
        layout.Children.Add(new Label
        {
            Content = label, Target = input, Foreground = (Brush)FindResource("Text"),
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 5)
        });
        layout.Children.Add(input);
        return input;
    }
}

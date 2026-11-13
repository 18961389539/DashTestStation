using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DustLoadScada;

internal sealed class RecipePointDialog : Window
{
    public ushort? Address { get; private set; }

    public RecipePointDialog(RecipePoint point, ushort? current, ushort? defaultAddress)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = $"编辑{point.Label}地址";
        Width = 465;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new StackPanel { Margin = new Thickness(22) };
        Content = layout;

        var address = new TextBox
        {
            Text = current?.ToString(CultureInfo.InvariantCulture) ?? "",
            MaxLength = 6, Margin = new Thickness(0, 0, 0, 10)
        };
        layout.Children.Add(new Label
        {
            Content = "Modbus 写入地址", Target = address, Foreground = (Brush)FindResource("Text"),
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 5)
        });
        layout.Children.Add(address);
        int max = 65536 - point.WordCount;
        string format = point.WordCount == 1 ? "UInt16" : "Int32";
        string scale = point.Scale == 1 ? "" : $"，写入值为配方值 × {point.Scale}";
        layout.Children.Add(new TextBlock
        {
            Text = $"保持寄存器，地址从 0 开始。{format} 占用 {point.WordCount} 个寄存器{scale}。"
                + (defaultAddress.HasValue ? $" 留空使用默认地址 {defaultAddress}。" : " 留空清除该点位。"),
            TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 12)
        });
        var error = new TextBlock { Foreground = (Brush)FindResource("Red"), TextWrapping = TextWrapping.Wrap };
        layout.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = "保存设置", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) =>
        {
            string text = address.Text.Trim();
            if (text.Length != 0)
            {
                if (!ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ushort value) || value > max)
                {
                    error.Text = $"请输入 0–{max} 之间的整数地址。";
                    address.Focus();
                    return;
                }
                Address = value;
            }
            DialogResult = true;
        };
        buttons.Children.Add(save);
        Loaded += (_, _) => { address.Focus(); address.SelectAll(); };
    }
}

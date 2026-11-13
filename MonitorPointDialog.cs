using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DustLoadScada;

internal sealed class MonitorPointDialog : Window
{
    private readonly TextBox address = new() { MaxLength = 5 };
    private readonly ComboBox dataType = new();
    private readonly TextBlock help = new() { TextWrapping = TextWrapping.Wrap };

    public MonitorPointDialog(MonitorPointDefinition definition, MonitorPoint current)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = $"编辑{definition.Label}";
        Width = 465;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(22) };
        Content = layout;
        var fieldLabel = new Style(typeof(Label));
        fieldLabel.Setters.Add(new Setter(ForegroundProperty, (Brush)FindResource("Text")));
        fieldLabel.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        fieldLabel.Setters.Add(new Setter(PaddingProperty, new Thickness(0, 0, 0, 5)));
        layout.Children.Add(new TextBlock
        {
            Text = Title, Style = (Style)FindResource("Section"), Margin = new Thickness(0, 0, 0, 12)
        });
        layout.Children.Add(new Label { Content = "Modbus 地址", Target = address, Style = fieldLabel });
        address.Text = current.Address.ToString(CultureInfo.InvariantCulture);
        layout.Children.Add(address);
        layout.Children.Add(new Label
        {
            Content = "数据类型", Target = dataType, Style = fieldLabel, Margin = new Thickness(0, 12, 0, 0)
        });
        dataType.Items.Add(new ComboBoxItem { Content = "Int16（16 位有符号）", Tag = RegisterDataType.Int16 });
        dataType.Items.Add(new ComboBoxItem { Content = "UInt16（16 位无符号）", Tag = RegisterDataType.UInt16 });
        dataType.Items.Add(new ComboBoxItem { Content = "Int32（32 位有符号）", Tag = RegisterDataType.Int32 });
        dataType.Items.Add(new ComboBoxItem { Content = "UInt32（32 位无符号）", Tag = RegisterDataType.UInt32 });
        dataType.Items.Add(new ComboBoxItem { Content = "Float32（32 位浮点）", Tag = RegisterDataType.Float32 });
        dataType.SelectedIndex = (int)current.DataType;
        layout.Children.Add(dataType);

        help.Style = (Style)FindResource("MutedText");
        help.Margin = new Thickness(0, 11, 0, 13);
        layout.Children.Add(help);
        void UpdateHelp()
        {
            var point = new MonitorPoint(0, SelectedType);
            help.Text = $"地址从 0 开始，{SelectedType} 占用 {point.WordCount} 个寄存器。";
            if (definition.ScaledUnit is { } unit)
                help.Text += SelectedType == RegisterDataType.Float32
                    ? $" 浮点值直接以 {unit} 显示。" : $" 整数原始值 × 0.01 {unit}。";
        }
        dataType.SelectionChanged += (_, _) => UpdateHelp();
        UpdateHelp();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = "保存设置", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) =>
        {
            if (!ushort.TryParse(address.Text, NumberStyles.None, CultureInfo.InvariantCulture, out ushort value)
                || new MonitorPoint(value, SelectedType).End > 65536)
            {
                int max = 65536 - new MonitorPoint(0, SelectedType).WordCount;
                MessageBox.Show(this, $"请输入 0–{max} 之间的整数地址。", "地址无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                address.Focus();
                return;
            }
            Point = new MonitorPoint(value, SelectedType);
            DialogResult = true;
        };
        buttons.Children.Add(save);
        Loaded += (_, _) => { address.Focus(); address.SelectAll(); };
    }

    private RegisterDataType SelectedType => (RegisterDataType)((ComboBoxItem)dataType.SelectedItem).Tag;
    public MonitorPoint? Point { get; private set; }
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DustLoadScada;

internal sealed class PointAddressDialog : Window
{
    private readonly StackPanel fields = new();
    private readonly TextBlock help = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel buttons = new()
    {
        Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly Button save = new() { Content = "保存设置", IsDefault = true };

    public ManualPoint? ManualResult => InspectionResult is { WriteAddress: ushort write, FeedbackAddress: ushort feedback }
        ? new ManualPoint(write, feedback) : null;
    public InspectionPoint? InspectionResult { get; private set; }
    public MonitorPoint? MonitorResult { get; private set; }
    public ushort? RecipeAddress { get; private set; }
    public BarcodePoint? BarcodeResult { get; private set; }
    public ushort? RecordTriggerAddress { get; private set; }
    public ushort? ResetAddress { get; private set; }

    private PointAddressDialog(string title)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = title;
        Width = 465;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new StackPanel { Margin = new Thickness(22) };
        Content = layout;
        layout.Children.Add(new TextBlock
        {
            Text = title, Style = (Style)FindResource("Section"), Margin = new Thickness(0, 0, 0, 12)
        });
        layout.Children.Add(fields);
        help.Style = (Style)FindResource("MutedText");
        help.Margin = new Thickness(0, 0, 0, 12);
        layout.Children.Add(help);
        error.Foreground = (Brush)FindResource("Red");
        layout.Children.Add(error);
        layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        save.Style = (Style)FindResource("Primary");
        buttons.Children.Add(save);
    }

    public PointAddressDialog(ManualAction action)
        : this(action.Label, action.Point.WriteAddress, action.Point.FeedbackAddress, false) { }

    public PointAddressDialog(string label, ushort? writeAddress, ushort? feedbackAddress, bool inspection)
        : this(inspection ? $"编辑{label}地址" : $"编辑{label}")
    {
        var write = AddTextField(inspection ? "写入地址（开 1，关 0）" : "写入地址（按下 1，松开 0）",
            writeAddress is ushort w ? ManualAddress.Format(w, false) : "", 12);
        var feedback = AddTextField("反馈地址（读取输出状态）",
            feedbackAddress is ushort f ? ManualAddress.Format(f, true) : "", 12);
        help.Text = "可填写 M50、Y10，或从 0 开始的十进制 Modbus 线圈地址。Y 编号使用八进制。"
            + (inspection ? "\n写入留空使用通讯配置的默认地址，反馈留空使用原监控反馈位。" : "");

        ConfigureSave(() =>
        {
            bool hasWrite = !string.IsNullOrWhiteSpace(write.Text);
            bool hasFeedback = !string.IsNullOrWhiteSpace(feedback.Text);
            if (!ManualAddress.TryParse(write.Text, false, out ushort writeValue) && (!inspection || hasWrite))
                return InvalidField(write, "请输入有效的 M 编号或 0–65535 的线圈地址。");
            if (!ManualAddress.TryParse(feedback.Text, true, out ushort feedbackValue) && (!inspection || hasFeedback))
                return InvalidField(feedback, "请输入有效的 Y 编号或 0–65535 的线圈地址。");

            InspectionResult = new InspectionPoint(hasWrite ? writeValue : null, hasFeedback ? feedbackValue : null);
            return true;
        });
    }

    public PointAddressDialog(MonitorPointDefinition definition, MonitorPoint? current)
        : this($"编辑{definition.Label}")
    {
        var address = AddTextField("Modbus 地址", current?.Address.ToString(CultureInfo.InvariantCulture) ?? "", 5);
        var dataType = new ComboBox();
        dataType.Items.Add(new ComboBoxItem { Content = "Int16（16 位有符号）", Tag = RegisterDataType.Int16 });
        dataType.Items.Add(new ComboBoxItem { Content = "UInt16（16 位无符号）", Tag = RegisterDataType.UInt16 });
        dataType.Items.Add(new ComboBoxItem { Content = "Int32（32 位有符号）", Tag = RegisterDataType.Int32 });
        dataType.Items.Add(new ComboBoxItem { Content = "UInt32（32 位无符号）", Tag = RegisterDataType.UInt32 });
        dataType.Items.Add(new ComboBoxItem { Content = "Float32（32 位浮点）", Tag = RegisterDataType.Float32 });
        dataType.SelectedIndex = (int)(current?.DataType ?? definition.DefaultType);
        AddField("数据类型", dataType);

        RegisterDataType SelectedType() => dataType.SelectedItem is ComboBoxItem { Tag: RegisterDataType type }
            ? type : throw new InvalidOperationException("请选择有效的数据类型。");
        void UpdateHelp()
        {
            var type = SelectedType();
            var point = new MonitorPoint(0, type);
            help.Text = $"地址从 0 开始，{type} 占用 {point.WordCount} 个寄存器。";
            if (definition.ScaledUnit is { } unit)
                help.Text += type == RegisterDataType.Float32
                    ? $" 浮点值直接以 {unit} 显示。" : $" 整数原始值 × 0.01 {unit}。";
            if (definition.Offset is null)
                help.Text += " 地址留空可取消采集。";
        }
        dataType.SelectionChanged += (_, _) => UpdateHelp();
        UpdateHelp();

        ConfigureSave(() =>
        {
            string input = address.Text.Trim();
            if (definition.Offset is null && input.Length == 0)
            {
                MonitorResult = null;
                return true;
            }
            var type = SelectedType();
            int max = 65536 - new MonitorPoint(0, type).WordCount;
            if (!ushort.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out ushort value) || value > max)
                return InvalidField(address, $"请输入 0–{max} 之间的整数地址。");

            MonitorResult = new MonitorPoint(value, type);
            return true;
        });
    }

    public PointAddressDialog(RecipePoint point, ushort? current, ushort? defaultAddress)
        : this($"编辑{point.Label}地址")
    {
        var address = AddTextField("Modbus 写入地址", current?.ToString(CultureInfo.InvariantCulture) ?? "", 6);
        int max = 65536 - point.WordCount;
        string format = point.IsFloat ? "Float32" : point.WordCount == 1 ? "UInt16" : "Int32";
        string scale = point.Scale == 1 ? "" : $"，写入值为配方值 × {point.Scale}";
        help.Text = $"保持寄存器，地址从 0 开始。{format} 占用 {point.WordCount} 个寄存器{scale}。"
            + (defaultAddress.HasValue ? $" 留空使用默认地址 {defaultAddress}。" : " 留空清除该点位。");

        ConfigureSave(() =>
        {
            string text = address.Text.Trim();
            if (text.Length != 0)
            {
                if (!ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ushort value) || value > max)
                    return InvalidField(address, $"请输入 0–{max} 之间的整数地址。");
                RecipeAddress = value;
            }
            return true;
        });
    }

    public PointAddressDialog(BarcodePoint current, BarcodePoint defaultPoint)
        : this("编辑二维码点位")
    {
        var address = AddTextField("Modbus 起始地址", current.Address.ToString(CultureInfo.InvariantCulture), 5);
        var count = AddTextField("寄存器数量", current.WordCount.ToString(CultureInfo.InvariantCulture), 3);
        help.Text = "地址从 0 开始。二维码按 UTF-8 解码，每个寄存器存放 2 个字节，高字节在前，遇到 NUL 结束。";
        var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            address.Text = defaultPoint.Address.ToString(CultureInfo.InvariantCulture);
            count.Text = defaultPoint.WordCount.ToString(CultureInfo.InvariantCulture);
            error.Text = "";
        };
        buttons.Children.Insert(0, reset);

        ConfigureSave(() =>
        {
            if (!ushort.TryParse(address.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort start))
                return InvalidField(address, "请输入 0–65535 之间的整数地址。");
            if (!ushort.TryParse(count.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort words)
                || words is < 1 or > BarcodePoint.MaxWords || start + words > 65536)
                return InvalidField(count, $"请输入 1–{BarcodePoint.MaxWords} 个寄存器，且起始地址与数量之和不得超过 65536。");

            BarcodeResult = new BarcodePoint(start, words);
            return true;
        });
    }

    public PointAddressDialog(ushort? currentTrigger)
        : this("编辑记录触发线圈")
    {
        ConfigureCoilAddress("触发线圈地址", currentTrigger,
            "可输入 M11 或零基线圈地址 11。线圈为 1 时采集，存档成功后写 0。留空恢复按测试完成状态存档。",
            value => RecordTriggerAddress = value);
    }

    public PointAddressDialog(ushort? currentReset, ushort? defaultReset)
        : this("编辑复位地址")
    {
        var fallback = defaultReset is ushort address
            ? $"留空使用通讯配置中的 {ManualAddress.Format(address, false)}。" : "留空表示未配置。";
        ConfigureCoilAddress("复位线圈地址", currentReset ?? defaultReset,
            "支持 M 编号或零基十进制线圈地址。点击复位写入 1，由 PLC 处理并清零。" + fallback,
            value => ResetAddress = value);
    }

    private void ConfigureCoilAddress(string label, ushort? current, string description, Action<ushort?> setAddress)
    {
        var address = AddTextField(label, current is ushort point ? ManualAddress.Format(point, false) : "", 6);
        help.Text = description;
        ConfigureSave(() =>
        {
            ushort? result = null;
            if (!string.IsNullOrWhiteSpace(address.Text))
            {
                if (!ManualAddress.TryParse(address.Text, false, out ushort value) || value >= ManualPoints.OutputBase)
                    return InvalidField(address, "请输入有效的 M 线圈或可写的零基十进制地址。");
                result = value;
            }
            setAddress(result);
            return true;
        });
    }

    private TextBox AddTextField(string label, string value, int maxLength)
    {
        var input = new TextBox { Text = value, MaxLength = maxLength };
        if (fields.Children.Count == 0)
            Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        AddField(label, input);
        return input;
    }

    private void AddField(string label, Control input)
    {
        fields.Children.Add(new Label
        {
            Content = label, Target = input, Foreground = (Brush)FindResource("Text"),
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 5)
        });
        input.Margin = new Thickness(0, 0, 0, 12);
        fields.Children.Add(input);
    }

    private bool InvalidField(Control input, string message)
    {
        error.Text = message;
        input.Focus();
        return false;
    }

    private void ConfigureSave(Func<bool> trySave)
    {
        save.Click += (_, _) =>
        {
            error.Text = "";
            if (trySave())
                DialogResult = true;
        };
    }
}

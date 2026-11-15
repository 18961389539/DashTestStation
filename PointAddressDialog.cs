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
    private readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock readResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button readTest = new() { Content = "读取测试", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Dictionary<Control, TextBlock> fieldErrors = new();
    private Func<Task>? saveAction;
    private Func<Task<string>>? readAction;
    private Func<bool>? trySave;
    private Func<string>? previewText;
    private TextBox? coilWriteAddress;
    private TextBox? coilFeedbackAddress;
    private TextBox? coilAddress;
    private string communicationInfo = "";
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
    public double? RecipeScale { get; private set; }
    public RegisterDataType? RecipeType { get; private set; }
    public BarcodePoint? BarcodeResult { get; private set; }
    public ushort? RecordTriggerAddress { get; private set; }
    public ushort? TimerTriggerAddress { get; private set; }
    public ushort? ResetAddress { get; private set; }
    public ushort? ScannerTriggerAddress { get; private set; }
    public ushort ScannerBarcodeAddress { get; private set; }
    public ushort ScannerVerdictAddress { get; private set; }

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
        preview.Style = (Style)FindResource("MutedText");
        preview.Margin = new Thickness(0, 0, 0, 8);
        layout.Children.Add(preview);
        help.Style = (Style)FindResource("MutedText");
        help.Margin = new Thickness(0, 0, 0, 12);
        layout.Children.Add(help);
        error.Foreground = (Brush)FindResource("Red");
        error.Margin = new Thickness(0, 0, 0, 8);
        layout.Children.Add(error);
        readResult.Style = (Style)FindResource("MutedText");
        readResult.Margin = new Thickness(0, 0, 0, 10);
        layout.Children.Add(readResult);
        layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        readTest.ToolTip = "配置 PLC 后执行只读测试，不会写入设备。";
        ToolTipService.SetShowOnDisabled(readTest, true);
        readTest.Click += async (_, _) => await RunReadTestAsync();
        buttons.Children.Add(readTest);
        save.Style = (Style)FindResource("Primary");
        save.Click += async (_, _) => await RunSaveAsync();
        buttons.Children.Add(save);
    }

    public void SetSaveAction(Func<Task> action) => saveAction = action;

    public void SetReadTest(Func<Task<string>> action)
    {
        readAction = action;
        readTest.IsEnabled = true;
        readTest.ToolTip = "只读测试，不会写入设备。";
    }

    public void SetPreview(string text)
    {
        previewText = null;
        preview.Text = text;
    }

    public void SetPreview(Func<string> getText)
    {
        previewText = getText;
        RefreshPreview();
    }

    public void SetCommunicationInfo(string text)
    {
        communicationInfo = text;
        if (!help.Text.Contains(text, StringComparison.Ordinal))
            help.Text += $"\n{text}";
    }

    public PointAddressDialog(ManualAction action)
        : this(action.Label, action.Point.WriteAddress, action.Point.FeedbackAddress, false)
    {
        var fallback = ManualPoints.Default(action.Id);
        SetPreview(() =>
        {
            bool sameAsDefault = PlcAddress.TryParseCoil(coilWriteAddress!.Text, out ushort write)
                && PlcAddress.TryParseCoil(coilFeedbackAddress!.Text, out ushort feedback)
                && write == fallback.WriteAddress && feedback == fallback.FeedbackAddress;
            return $"有效写入：{FormatInput(coilWriteAddress!)}；反馈：{FormatInput(coilFeedbackAddress!)}；"
                + (sameAsDefault ? "当前使用默认地址。" : "当前为自定义地址。");
        });
    }

    public PointAddressDialog(string label, ushort? writeAddress, ushort? feedbackAddress, bool inspection,
        ushort? defaultWriteAddress = null)
        : this(inspection ? $"编辑{label}地址" : $"编辑{label}")
    {
        var write = AddTextField(inspection ? "写入地址（开 1，关 0）" : "写入地址（按下 1，松开 0）",
            writeAddress is ushort w ? PlcAddress.FormatCoil(w) : "", 12);
        var feedback = AddTextField("反馈地址（读取输出状态）",
            feedbackAddress is ushort f ? PlcAddress.FormatCoil(f) : "", 12);
        coilWriteAddress = write;
        coilFeedbackAddress = feedback;
        help.Text = "信捷 XD3：可填写 M50、Y10、X0、S0、HM0，或从 0 开始的十进制 Modbus 线圈地址。X/Y 编号使用八进制。"
            + "写入地址不能位于 X/Y 区。"
            + (inspection ? "\n写入留空使用通讯配置的默认地址，反馈留空使用原监控反馈位。" : "");

        ConfigureSave(() =>
        {
            bool hasWrite = !string.IsNullOrWhiteSpace(write.Text);
            bool hasFeedback = !string.IsNullOrWhiteSpace(feedback.Text);
            if (!PlcAddress.TryParseCoil(write.Text, out ushort writeValue) && (!inspection || hasWrite))
                return InvalidField(write, "请输入有效的 M/S/HM 编号或 0–65535 的线圈地址。");
            if (hasWrite && PlcAddress.IsPhysicalIo(writeValue))
                return InvalidField(write, "写入地址不能位于 X 输入区或 Y 输出区。");
            if (!PlcAddress.TryParseCoil(feedback.Text, out ushort feedbackValue) && (!inspection || hasFeedback))
                return InvalidField(feedback, "请输入有效的 Y/X/M 编号或 0–65535 的线圈地址。");

            InspectionResult = new InspectionPoint(hasWrite ? writeValue : null, hasFeedback ? feedbackValue : null);
            return true;
        });
        SetPreview(() =>
        {
            string writeValue = FormatInput(write);
            if (inspection && string.IsNullOrWhiteSpace(write.Text))
                writeValue = defaultWriteAddress is ushort fallback
                    ? $"留空 → {PlcAddress.FormatCoil(fallback)}" : "留空 → 未配置";
            string feedbackValue = FormatInput(feedback);
            if (inspection && string.IsNullOrWhiteSpace(feedback.Text))
                feedbackValue = "留空 → PLC 默认反馈";
            string defaultStatus = inspection
                ? string.IsNullOrWhiteSpace(write.Text)
                    ? defaultWriteAddress.HasValue ? "使用通讯配置默认写入地址。" : "未配置默认写入地址。"
                    : PlcAddress.TryParseCoil(write.Text, out ushort selected) && selected == defaultWriteAddress
                        ? "写入地址为默认值。" : "写入地址为自定义值。"
                : "写入和反馈地址均须填写。";
            return $"有效写入：{writeValue}；有效反馈：{feedbackValue}。{defaultStatus}"
                + (inspection ? "反馈留空沿用监控区反馈。" : "");
        });
    }

    public PointAddressDialog(MonitorPointDefinition definition, MonitorPoint? current,
        MonitorPoint? defaultPoint, bool hasOverride)
        : this($"编辑{definition.Label}")
    {
        var address = AddTextField("寄存器地址", current is { } point0 ? PlcAddress.FormatRegister(point0.Address) : "", 6);
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

        var scale = AddTextField("倍率（工程值 = 原始值 × 倍率）",
            FormatScale(current?.Scale ?? definition.DefaultScale(SelectedType())), 12);

        void UpdateHelp()
        {
            var type = SelectedType();
            var point = new MonitorPoint(0, type);
            int? addressValue = PlcAddress.TryParseRegister(address.Text, out ushort parsed) ? parsed : null;
            string selectedRange = addressValue is int start
                ? start + point.WordCount <= 65536 ? $"{PlcAddress.FormatRegisterRange(start, point.WordCount)}（{start}）" : "地址超出范围"
                : string.IsNullOrWhiteSpace(address.Text) ? "尚未填写" : "地址无效";
            string defaultRange = defaultPoint is MonitorPoint fallback
                ? PlcAddress.FormatRegisterRange(fallback.Address, fallback.WordCount) : "无";
            bool matchesDefault = defaultPoint is not null && addressValue == defaultPoint.Address
                && type == defaultPoint.DataType;
            double defaultScale = definition.DefaultScale(type);
            bool isFloat = type == RegisterDataType.Float32;
            SetFieldVisible(scale, !isFloat);
            string scaleInfo = isFloat ? "Float32 不使用倍率。"
                : $"倍率 {(TryParseScale(scale.Text, out double scaleValue) ? FormatScale(scaleValue) : "无效")}（默认 {FormatScale(defaultScale)}）。";
            SetPreview($"有效寄存器：{selectedRange}；默认：{defaultRange}；"
                + (matchesDefault ? "当前使用默认地址和类型。" : defaultPoint is null
                    ? "默认未配置。" : hasOverride ? "当前为自定义地址或类型。" : "地址或类型已修改。")
                + scaleInfo);
            help.Text = $"信捷 XD3：可填写 D100、HD0、HM1 或从 0 开始的十进制寄存器地址。{type} 占用 {point.WordCount} 个寄存器。"
                + (isFloat ? "Float32 直接读取工程量，不使用倍率。" : $"工程值 = 原始值 × 倍率，默认倍率 {FormatScale(defaultScale)}。");
            if (definition.ScaledUnit is { } unit)
                help.Text += isFloat ? $" 以 {unit} 显示。" : $" 整数默认原值 × 0.01 {unit}。";
            if (definition.Offset is null)
                help.Text += " 地址留空可取消采集。";
            if (communicationInfo.Length != 0 && !help.Text.Contains(communicationInfo, StringComparison.Ordinal))
                help.Text += $"\n{communicationInfo}";
        }
        dataType.SelectionChanged += (_, _) =>
        {
            scale.Text = FormatScale(definition.DefaultScale(SelectedType()));
            UpdateHelp();
        };
        address.TextChanged += (_, _) => UpdateHelp();
        scale.TextChanged += (_, _) => UpdateHelp();
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
            if (!PlcAddress.TryParseRegister(input, out ushort value) || value > max)
                return InvalidField(address, $"请输入 D/HD/HM 编号或 0–{max} 之间的整数地址。");
            double? overrideScale = null;
            if (type != RegisterDataType.Float32)
            {
                if (!TryParseScale(scale.Text, out double scaleValue) || scaleValue <= 0)
                    return InvalidField(scale, "请输入大于 0 的倍率，例如 1、0.01、10。");
                overrideScale = scaleValue == definition.DefaultScale(type) ? null : scaleValue;
            }

            MonitorResult = new MonitorPoint(value, type, overrideScale);
            return true;
        });
    }

    public PointAddressDialog(RecipePoint point, ushort? current, ushort? defaultAddress, double? currentScale = null,
        RegisterDataType? currentType = null)
        : this($"编辑{point.Label}地址")
    {
        var address = AddTextField("寄存器写入地址", current is ushort value0 ? PlcAddress.FormatRegister(value0) : "", 6);
        var dataType = new ComboBox();
        dataType.Items.Add(new ComboBoxItem { Content = "Int16（16 位有符号）", Tag = RegisterDataType.Int16 });
        dataType.Items.Add(new ComboBoxItem { Content = "UInt16（16 位无符号）", Tag = RegisterDataType.UInt16 });
        dataType.Items.Add(new ComboBoxItem { Content = "Int32（32 位有符号）", Tag = RegisterDataType.Int32 });
        dataType.Items.Add(new ComboBoxItem { Content = "UInt32（32 位无符号）", Tag = RegisterDataType.UInt32 });
        dataType.Items.Add(new ComboBoxItem { Content = "Float32（32 位浮点）", Tag = RegisterDataType.Float32 });
        dataType.SelectedIndex = (int)(currentType ?? point.DefaultType);
        AddField("数据类型", dataType);

        RegisterDataType SelectedType() => dataType.SelectedItem is ComboBoxItem { Tag: RegisterDataType type }
            ? type : throw new InvalidOperationException("请选择有效的数据类型。");

        var scale = AddTextField("倍率（工程值 = 原始值 × 倍率）", FormatScale(currentScale ?? point.DefaultScale), 12);

        void UpdateHelp()
        {
            var type = SelectedType();
            bool isFloat = type == RegisterDataType.Float32;
            SetFieldVisible(scale, !isFloat);
            int words = RecipePoint.WordsFor(type);
            int max = 65536 - words;
            string scaleInfo = isFloat ? "Float32 不使用倍率。"
                : $"倍率 {(TryParseScale(scale.Text, out double scaleValue) ? FormatScale(scaleValue) : "无效")}（默认 {FormatScale(point.DefaultScale)}）。";
            help.Text = $"保持寄存器，可填写 D100、HD0、HM1 或从 0 开始的十进制地址。{type} 占用 {words} 个寄存器。"
                + (isFloat ? "Float32 直接写入工程量，不使用倍率。"
                    : $"工程值 = 原始值 × 倍率，默认倍率 {FormatScale(point.DefaultScale)}（写入原始值 = 配方值 ÷ 倍率）。")
                + (defaultAddress is ushort fallback0 ? $" 留空使用默认地址 {PlcAddress.FormatRegister(fallback0)}。" : " 留空清除该点位。");

            bool hasInput = !string.IsNullOrWhiteSpace(address.Text);
            bool validInput = PlcAddress.TryParseRegister(address.Text, out ushort parsed);
            string addressInfo;
            if (hasInput && !validInput)
                addressInfo = "有效寄存器：地址无效，请填写 D/HD/HM 编号或 0–65535 范围内的整数。";
            else if (parsed > max)
                addressInfo = $"有效寄存器：地址超出范围（最大起始地址 {max}）。";
            else
            {
                ushort? selected = validInput ? parsed : null;
                ushort? effective = selected ?? defaultAddress;
                addressInfo = $"有效寄存器：{(effective is ushort start ? $"{PlcAddress.FormatRegisterRange(start, words)}（{start}）" : "未配置")}；"
                    + (selected is null ? defaultAddress.HasValue ? "留空使用默认地址。" : "留空清除该点位。"
                        : selected == defaultAddress ? "当前使用默认地址。"
                            : $"默认地址：{(defaultAddress is ushort d ? PlcAddress.FormatRegister(d) : "无")}。");
            }
            SetPreview(addressInfo + scaleInfo);
        }
        dataType.SelectionChanged += (_, _) =>
        {
            scale.Text = FormatScale(point.DefaultScale);
            UpdateHelp();
        };
        address.TextChanged += (_, _) => UpdateHelp();
        scale.TextChanged += (_, _) => UpdateHelp();
        UpdateHelp();

        ConfigureSave(() =>
        {
            var type = SelectedType();
            string text = address.Text.Trim();
            if (text.Length != 0)
            {
                int max = 65536 - RecipePoint.WordsFor(type);
                if (!PlcAddress.TryParseRegister(text, out ushort value) || value > max)
                    return InvalidField(address, $"请输入 D/HD/HM 编号或 0–{max} 之间的整数地址。");
                RecipeAddress = value;
            }
            RecipeType = type == point.DefaultType ? null : type;
            if (type == RegisterDataType.Float32)
            {
                RecipeScale = null;
                return true;
            }
            if (!TryParseScale(scale.Text, out double scaleValue) || scaleValue <= 0)
                return InvalidField(scale, "请输入大于 0 的倍率，例如 1、0.1、10。");
            RecipeScale = scaleValue == point.DefaultScale ? null : scaleValue;
            return true;
        });
    }

    public PointAddressDialog(BarcodePoint current, BarcodePoint defaultPoint)
        : this("编辑二维码点位")
    {
        var address = AddTextField("寄存器起始地址", PlcAddress.FormatRegister(current.Address), 6);
        var count = AddTextField("寄存器数量", current.WordCount.ToString(CultureInfo.InvariantCulture), 3);
        help.Text = "可填写 D100、HD0、HM1 或从 0 开始的十进制地址。二维码按 UTF-8 解码，每个寄存器存放 2 个字节，高字节在前，遇到 NUL 结束。";
        var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            address.Text = PlcAddress.FormatRegister(defaultPoint.Address);
            count.Text = defaultPoint.WordCount.ToString(CultureInfo.InvariantCulture);
            error.Text = "";
        };
        buttons.Children.Insert(0, reset);

        ConfigureSave(() =>
        {
            if (!PlcAddress.TryParseRegister(address.Text, out ushort start))
                return InvalidField(address, "请输入 D/HD/HM 编号或 0–65535 之间的整数地址。");
            if (!ushort.TryParse(count.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort words)
                || words is < 1 or > BarcodePoint.MaxWords || start + words > 65536)
                return InvalidField(count, $"请输入 1–{BarcodePoint.MaxWords} 个寄存器，且起始地址与数量之和不得超过 65536。");

            BarcodeResult = new BarcodePoint(start, words);
            return true;
        });
        SetPreview(() =>
        {
            bool validAddress = PlcAddress.TryParseRegister(address.Text, out ushort start);
            bool validCount = ushort.TryParse(count.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort words);
            return validAddress && validCount && words > 0 && words <= BarcodePoint.MaxWords && start + words <= 65536
                ? $"有效寄存器：{PlcAddress.FormatRegisterRange(start, words)}（{start}–{start + words - 1}）；"
                    + (start == defaultPoint.Address && words == defaultPoint.WordCount
                        ? "当前使用默认地址和数量。" : $"默认值：{PlcAddress.FormatRegisterRange(defaultPoint.Address, defaultPoint.WordCount)}。")
                : "地址或数量无效，请检查范围及寄存器总数。";
        });
    }

    public PointAddressDialog(ushort? currentTrigger)
        : this("编辑记录触发线圈")
    {
        ConfigureCoilAddress("触发线圈地址", currentTrigger,
            "可输入 M11 或零基线圈地址 11。线圈为 1 时采集，存档成功后写 0。PLC 模式只按此线圈存档，留空则不存档。",
            value => RecordTriggerAddress = value);
        SetPreview(() => string.IsNullOrWhiteSpace(coilAddress!.Text)
            ? "当前未配置记录触发，PLC 测试结果不会存档。"
            : $"记录触发线圈：{FormatInput(coilAddress)}。");
    }

    public static PointAddressDialog ForTimerTrigger(ushort? currentTrigger)
    {
        var dialog = new PointAddressDialog("编辑计时触发线圈");
        dialog.ConfigureCoilAddress("计时线圈地址", currentTrigger,
            "可输入 M、X、Y 等线圈地址。线圈为 1 期间由上位机计时，变 0 时停止并保留最后时间；再次变 1 时清零重新计时。留空不计时。",
            value => dialog.TimerTriggerAddress = value, readOnly: true);
        dialog.SetPreview(() => string.IsNullOrWhiteSpace(dialog.coilAddress!.Text)
            ? "当前未配置计时触发，运行时间保持 0。"
            : $"计时触发线圈：{FormatInput(dialog.coilAddress)}。");
        return dialog;
    }

    public static PointAddressDialog ForScannerTrigger(ushort? current)
    {
        var dialog = new PointAddressDialog("编辑扫码触发线圈");
        dialog.ConfigureCoilAddress("触发线圈地址", current,
            "可输入 M、Y、X、S、HM 编号或零基线圈地址。线圈置 1 触发扫码，写回完成后由上位机清零。留空停用扫码触发，默认 Y11。",
            value => dialog.ScannerTriggerAddress = value, allowPhysicalIo: true);
        dialog.SetPreview(() => string.IsNullOrWhiteSpace(dialog.coilAddress!.Text)
            ? "当前未配置扫码触发，PLC 不会触发扫码。"
            : $"扫码触发线圈：{FormatInput(dialog.coilAddress)}。");
        return dialog;
    }

    public static PointAddressDialog ForScannerBarcode(ushort current)
    {
        var dialog = new PointAddressDialog("编辑二维码写回地址");
        ushort defaultAddress = new ScannerTriggerSettings().BarcodeAddress;
        int words = ScannerTriggerSettings.MaxBarcodeWords;
        var address = dialog.AddTextField("寄存器起始地址", PlcAddress.FormatRegister(current), 6);
        dialog.help.Text = $"保持寄存器，可填写 D4000、HD0、HM1 或从 0 开始的十进制地址。固定占用 {words} 个寄存器（{words * 2} 字节），"
            + "按 UTF-8 写入扫码内容，高字节在前，遇到 NUL 结束；每次写回先清零整块。";
        dialog.ConfigureSave(() =>
        {
            if (!PlcAddress.TryParseRegister(address.Text, out ushort value) || value + words > 65536)
                return dialog.InvalidField(address, $"请输入 D/HD/HM 编号或 0–{65536 - words} 之间的整数地址。");
            dialog.ScannerBarcodeAddress = value;
            return true;
        });
        dialog.SetPreview(() =>
        {
            bool valid = PlcAddress.TryParseRegister(address.Text, out ushort start) && start + words <= 65536;
            return valid
                ? $"有效寄存器：{PlcAddress.FormatRegisterRange(start, words)}（{start}–{start + words - 1}）；"
                    + (start == defaultAddress ? "当前使用默认地址。" : $"默认地址：{PlcAddress.FormatRegister(defaultAddress)}。")
                : $"地址无效，请填写 D/HD/HM 编号或 0–{65536 - words} 范围内的整数。";
        });
        return dialog;
    }

    public static PointAddressDialog ForScannerVerdict(ushort current)
    {
        var dialog = new PointAddressDialog("编辑比对结果写回地址");
        ushort defaultAddress = new ScannerTriggerSettings().VerdictAddress;
        var address = dialog.AddTextField("寄存器地址", PlcAddress.FormatRegister(current), 6);
        dialog.help.Text = "单个保持寄存器，可填写 D3、HD0、HM1 或从 0 开始的十进制地址。协议固定 UInt16：1=匹配，2=不匹配，不使用倍率。";
        dialog.ConfigureSave(() =>
        {
            if (!PlcAddress.TryParseRegister(address.Text, out ushort value))
                return dialog.InvalidField(address, "请输入 D/HD/HM 编号或 0–65535 之间的整数地址。");
            dialog.ScannerVerdictAddress = value;
            return true;
        });
        dialog.SetPreview(() =>
        {
            bool valid = PlcAddress.TryParseRegister(address.Text, out ushort start);
            return valid
                ? $"有效寄存器：{PlcAddress.FormatRegister(start)}（{start}）；"
                    + (start == defaultAddress ? "当前使用默认地址。" : $"默认地址：{PlcAddress.FormatRegister(defaultAddress)}。")
                : "地址无效，请填写 D/HD/HM 编号或 0–65535 范围内的整数。";
        });
        return dialog;
    }

    public PointAddressDialog(ushort? currentReset, ushort? defaultReset)
        : this("编辑复位地址")
    {
        var fallback = defaultReset is ushort address
            ? $"留空使用通讯配置中的 {PlcAddress.FormatCoil(address)}。" : "留空表示未配置。";
        ConfigureCoilAddress("复位线圈地址", currentReset ?? defaultReset,
            "支持 M 编号或零基十进制线圈地址。点击复位写入 1，由 PLC 处理并清零。" + fallback,
            value => ResetAddress = value);
        SetPreview(() =>
        {
            ushort? selected = null;
            bool hasInput = !string.IsNullOrWhiteSpace(coilAddress!.Text);
            ushort parsed = 0;
            bool validInput = !hasInput || PlcAddress.TryParseCoil(coilAddress.Text, out parsed)
                && !PlcAddress.IsPhysicalIo(parsed);
            if (hasInput && !validInput)
                return "有效复位线圈：地址无效。";
            if (hasInput && PlcAddress.TryParseCoil(coilAddress.Text, out parsed))
                selected = parsed;
            ushort? effective = selected ?? defaultReset;
            string state = selected == defaultReset
                ? defaultReset.HasValue ? "当前使用默认地址。" : "未配置默认值。"
                : selected.HasValue ? "当前为自定义地址。" : "留空恢复默认地址。";
            return $"有效复位线圈：{(effective is ushort value ? PlcAddress.FormatCoil(value) : "未配置")}；{state}";
        });
    }

    private void ConfigureCoilAddress(string label, ushort? current, string description, Action<ushort?> setAddress,
        bool readOnly = false, bool allowPhysicalIo = false)
    {
        var address = AddTextField(label, current is ushort point ? PlcAddress.FormatCoil(point) : "", 6);
        coilAddress = address;
        help.Text = description;
        ConfigureSave(() =>
        {
            ushort? result = null;
            if (!string.IsNullOrWhiteSpace(address.Text))
            {
                if (!PlcAddress.TryParseCoil(address.Text, out ushort value))
                    return InvalidField(address, "请输入 M/X/Y/S/HM 编号或零基十进制线圈地址。");
                if (!readOnly && !allowPhysicalIo && PlcAddress.IsPhysicalIo(value))
                    return InvalidField(address, "请输入 M/S/HM 编号或可写的零基十进制地址，不能位于 X/Y 区。");
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
            var fieldError = new TextBlock { Foreground = (Brush)FindResource("Red"), TextWrapping = TextWrapping.Wrap };
            fieldError.Margin = new Thickness(0, -9, 0, 8);
            fields.Children.Add(fieldError);
            fieldErrors.Add(input, fieldError);
            if (input is TextBox textBox)
                textBox.TextChanged += (_, _) =>
                {
                    ClearFieldError(input);
                    error.Text = "";
                    RefreshPreview();
                };
            else if (input is ComboBox comboBox)
                comboBox.SelectionChanged += (_, _) =>
                {
                    ClearFieldError(input);
                    error.Text = "";
                    RefreshPreview();
                };
    }

    private void SetFieldVisible(Control input, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        input.Visibility = visibility;
        if (fieldErrors.TryGetValue(input, out var message))
            message.Visibility = visibility;
        foreach (var child in fields.Children)
            if (child is Label label && ReferenceEquals(label.Target, input))
                label.Visibility = visibility;
    }

    private bool InvalidField(Control input, string message)
    {
        ClearErrors();
        fieldErrors[input].Text = message;
        input.BorderBrush = (Brush)FindResource("Red");
        input.BorderThickness = new Thickness(1);
        input.ToolTip = message;
        input.Focus();
        return false;
    }

    private void ConfigureSave(Func<bool> trySave) => this.trySave = trySave;

    private async Task RunSaveAsync()
    {
        ClearErrors();
        if (trySave?.Invoke() != true)
            return;
        if (saveAction is null)
        {
            error.Text = "保存操作未配置。";
            return;
        }

        save.IsEnabled = false;
        readTest.IsEnabled = false;
        try
        {
            await saveAction();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            error.Text = ex.Message;
        }
        finally
        {
            save.IsEnabled = true;
            readTest.IsEnabled = readAction is not null;
        }
    }

    private async Task RunReadTestAsync()
    {
        ClearErrors();
        if (trySave?.Invoke() != true || readAction is null)
            return;

        readTest.IsEnabled = false;
        save.IsEnabled = false;
        readResult.Text = "正在读取 PLC…";
        readResult.Foreground = (Brush)FindResource("Muted");
        try
        {
            readResult.Text = await readAction();
        }
        catch (Exception ex)
        {
            readResult.Text = $"读取失败：{ex.Message}";
            readResult.Foreground = (Brush)FindResource("Red");
        }
        finally
        {
            save.IsEnabled = true;
            readTest.IsEnabled = readAction is not null;
        }
    }

    private void ClearErrors()
    {
        error.Text = "";
        foreach (var input in fieldErrors.Keys)
            ClearFieldError(input);
    }

    private void ClearFieldError(Control input)
    {
        if (!fieldErrors.TryGetValue(input, out var message))
            return;
        message.Text = "";
        input.ClearValue(Control.BorderBrushProperty);
        input.ClearValue(Control.BorderThicknessProperty);
        input.ClearValue(FrameworkElement.ToolTipProperty);
    }

    private void RefreshPreview()
    {
        if (previewText is not null)
            preview.Text = previewText();
    }

    private static string FormatInput(TextBox input) =>
        string.IsNullOrWhiteSpace(input.Text) ? "留空" :
            PlcAddress.TryParseCoil(input.Text, out ushort value)
                ? $"{PlcAddress.FormatCoil(value)}（线圈 {value}）" : "无效地址";

    private static string FormatScale(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static bool TryParseScale(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

}

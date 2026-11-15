using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DustLoadScada;

internal sealed class NewRecipeDialog : Window
{
    public NewRecipeDialog(Recipe template, Action<Recipe> create)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "新建配方";
        Width = 760;
        Height = 760;
        MinHeight = 560;
        MinWidth = 640;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var fields = new Dictionary<string, TextBox>();
        var content = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(scroll);

        content.Children.Add(new TextBlock
        {
            Text = $"以下参数默认沿用「{template.Name}」，可在创建前修改。",
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        AddSection(content, "基本信息",
            ("配方名称", "Name", "", 80),
            ("产品型号", "Model", template.Model, 80));
        var cameraRecipe = new ComboBox { SelectedValuePath = "Tag", ToolTip = "启用配方时通过相机 TCP 发送对应命令。" };
        cameraRecipe.Items.Add(new ComboBoxItem { Content = "头灯-跑马灯", Tag = CameraRecipeId.HeadlightAndIndicator, ToolTip = "switch 1" });
        cameraRecipe.Items.Add(new ComboBoxItem { Content = "头灯", Tag = CameraRecipeId.Headlight, ToolTip = "switch 2" });
        cameraRecipe.Items.Add(new ComboBoxItem { Content = "跑马灯", Tag = CameraRecipeId.Indicator, ToolTip = "switch 3" });
        cameraRecipe.SelectedValue = template.CameraRecipe;
        var cameraSection = new StackPanel();
        cameraSection.Children.Add(new Label
        {
            Content = "相机配方", Target = cameraRecipe, Foreground = (Brush)FindResource("Text"),
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 3)
        });
        cameraSection.Children.Add(cameraRecipe);
        content.Children.Add(new Border
        {
            Style = (Style)FindResource("Panel"), Padding = new Thickness(16, 10, 16, 12),
            Margin = new Thickness(0, 0, 0, 10), Child = cameraSection
        });
        AddSection(content, "判定范围",
            ("电压下限 (V)", "VoltageMin", Number(template.VoltageMin, 4), 24),
            ("电压上限 (V)", "VoltageMax", Number(template.VoltageMax, 4), 24),
            ("功率差下限 (W)", "PowerDeltaMin", Number(template.PowerDeltaMin, 4), 24),
            ("功率差上限 (W)", "PowerDeltaMax", Number(template.PowerDeltaMax, 4), 24),
            ("转速差下限（整数）", "SpeedDeltaMin", Number(template.SpeedDeltaMin, 0), 24),
            ("转速差上限（整数）", "SpeedDeltaMax", Number(template.SpeedDeltaMax, 0), 24));
        AddSection(content, "判定限值",
            ("正常电流下限 (A)", "NormalCurrentMin", Number(template.NormalCurrentMin, 3), 24),
            ("正常电流上限 (A)", "NormalCurrentMax", Number(template.NormalCurrentMax, 3), 24),
            ("正常功率下限 (W)", "NormalPowerMin", Number(template.NormalPowerMin, 3), 24),
            ("正常功率上限 (W)", "NormalPowerMax", Number(template.NormalPowerMax, 3), 24),
            ("正常转速下限 (r/min)", "NormalSpeedMin", Number(template.NormalSpeedMin, 3), 24),
            ("正常转速上限 (r/min)", "NormalSpeedMax", Number(template.NormalSpeedMax, 3), 24),
            ("灰尘电流下限 (A)", "DustCurrentMin", Number(template.DustCurrentMin, 3), 24),
            ("灰尘电流上限 (A)", "DustCurrentMax", Number(template.DustCurrentMax, 3), 24),
            ("灰尘功率下限 (W)", "DustPowerMin", Number(template.DustPowerMin, 3), 24),
            ("灰尘功率上限 (W)", "DustPowerMax", Number(template.DustPowerMax, 3), 24),
            ("灰尘转速下限 (r/min)", "DustSpeedMin", Number(template.DustSpeedMin, 3), 24),
            ("灰尘转速上限 (r/min)", "DustSpeedMax", Number(template.DustSpeedMax, 3), 24));
        AddSection(content, "扫码设置",
            ("开始序号（从 1 开始）", "BarcodeStartIndex", Number(template.BarcodeStartIndex), 3),
            ("对比长度", "BarcodeCompareLength", Number(template.BarcodeCompareLength), 3));
        AddSection(content, "测试流程",
            ("正常测试时间 (整数秒)", "NormalSeconds", Number(template.NormalSeconds, 0), 24),
            ("灰尘测试时间 (整数秒)", "DustSeconds", Number(template.DustSeconds, 0), 24),
            ("开机动作时长 (s)", "PowerActionSeconds", Number(template.PowerActionSeconds, 1), 24),
            ("档位动作时长 (s)", "GearActionSeconds", Number(template.GearActionSeconds, 1), 24),
            ("开机间隔时间 (s)", "PowerIntervalSeconds", Number(template.PowerIntervalSeconds, 1), 24),
            ("档位间隔时间 (s)", "GearIntervalSeconds", Number(template.GearIntervalSeconds, 1), 24),
            ("开机次数", "PowerCycles", Number(template.PowerCycles), 4),
            ("档位次数", "GearCycles", Number(template.GearCycles), 4),
            ("报警时间 (s)", "AlarmSeconds", Number(template.AlarmSeconds, 1), 24),
            ("跑马灯灭时间设定 (s)", "IndicatorOffSeconds", Number(template.IndicatorOffSeconds, 1), 24));

        var footer = new StackPanel();
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);
        var error = new TextBlock
        {
            Foreground = (Brush)FindResource("Red"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0)
        };
        footer.Children.Add(error);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        footer.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var confirm = new Button { Content = "创建", IsDefault = true, Style = (Style)FindResource("Primary") };
        confirm.Click += (_, _) =>
        {
            try
            {
                var recipe = template with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = fields["Name"].Text.Trim(),
                    Model = fields["Model"].Text.Trim(),
                    CameraRecipe = cameraRecipe.SelectedValue is CameraRecipeId selectedCameraRecipe
                        ? selectedCameraRecipe : throw new InvalidOperationException("请选择相机配方。"),
                    VoltageMin = ReadDouble("VoltageMin", "电压下限"),
                    VoltageMax = ReadDouble("VoltageMax", "电压上限"),
                    PowerDeltaMin = ReadDouble("PowerDeltaMin", "功率差下限"),
                    PowerDeltaMax = ReadDouble("PowerDeltaMax", "功率差上限"),
                    SpeedDeltaMin = ReadInt("SpeedDeltaMin", "转速差下限"),
                    SpeedDeltaMax = ReadInt("SpeedDeltaMax", "转速差上限"),
                    NormalCurrentMin = ReadDouble("NormalCurrentMin", "正常电流下限"),
                    NormalCurrentMax = ReadDouble("NormalCurrentMax", "正常电流上限"),
                    NormalPowerMin = ReadDouble("NormalPowerMin", "正常功率下限"),
                    NormalPowerMax = ReadDouble("NormalPowerMax", "正常功率上限"),
                    NormalSpeedMin = ReadDouble("NormalSpeedMin", "正常转速下限"),
                    NormalSpeedMax = ReadDouble("NormalSpeedMax", "正常转速上限"),
                    DustCurrentMin = ReadDouble("DustCurrentMin", "灰尘电流下限"),
                    DustCurrentMax = ReadDouble("DustCurrentMax", "灰尘电流上限"),
                    DustPowerMin = ReadDouble("DustPowerMin", "灰尘功率下限"),
                    DustPowerMax = ReadDouble("DustPowerMax", "灰尘功率上限"),
                    DustSpeedMin = ReadDouble("DustSpeedMin", "灰尘转速下限"),
                    DustSpeedMax = ReadDouble("DustSpeedMax", "灰尘转速上限"),
                    BarcodeStartIndex = ReadInt("BarcodeStartIndex", "开始序号"),
                    BarcodeCompareLength = ReadInt("BarcodeCompareLength", "对比长度"),
                    NormalSeconds = ReadInt("NormalSeconds", "正常测试时间"),
                    DustSeconds = ReadInt("DustSeconds", "灰尘测试时间"),
                    PowerActionSeconds = ReadDouble("PowerActionSeconds", "开机动作时长"),
                    GearActionSeconds = ReadDouble("GearActionSeconds", "档位动作时长"),
                    PowerIntervalSeconds = ReadDouble("PowerIntervalSeconds", "开机间隔时间"),
                    GearIntervalSeconds = ReadDouble("GearIntervalSeconds", "档位间隔时间"),
                    PowerCycles = ReadInt("PowerCycles", "开机次数"),
                    GearCycles = ReadInt("GearCycles", "档位次数"),
                    AlarmSeconds = ReadDouble("AlarmSeconds", "报警时间"),
                    IndicatorOffSeconds = ReadDouble("IndicatorOffSeconds", "跑马灯灭时间")
                };
                recipe.Validate();
                create(recipe);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
            }
        };
        buttons.Children.Add(confirm);
        Loaded += (_, _) => fields["Name"].Focus();

        void AddSection(StackPanel parent, string title, params (string Label, string Key, string Value, int MaxLength)[] items)
        {
            var section = new StackPanel();
            section.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)FindResource("Section"),
                Margin = new Thickness(0, 2, 0, 8)
            });
            var inputs = new UniformGrid { Columns = 2 };
            foreach (var item in items)
            {
                var field = new StackPanel { Margin = new Thickness(0, 0, 14, 10) };
                var input = new TextBox { Text = item.Value, MaxLength = item.MaxLength };
                fields.Add(item.Key, input);
                field.Children.Add(new Label
                {
                    Content = item.Label,
                    Target = input,
                    Foreground = (Brush)FindResource("Text"),
                    FontWeight = FontWeights.SemiBold,
                    Padding = new Thickness(0, 0, 0, 3)
                });
                field.Children.Add(input);
                inputs.Children.Add(field);
            }
            section.Children.Add(inputs);
            parent.Children.Add(new Border
            {
                Style = (Style)FindResource("Panel"),
                Padding = new Thickness(16, 10, 4, 2),
                Margin = new Thickness(0, 0, 0, 10),
                Child = section
            });
        }

        double ReadDouble(string key, string label)
        {
            var input = fields[key];
            if (double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value))
                return value;
            input.Focus();
            throw new InvalidOperationException($"{label}请输入有效数字。");
        }

        int ReadInt(string key, string label)
        {
            var input = fields[key];
            if (int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int value))
                return value;
            input.Focus();
            throw new InvalidOperationException($"{label}请输入整数。");
        }
    }

    private static string Number<T>(T value) where T : IFormattable =>
        value.ToString(null, CultureInfo.CurrentCulture);

    private static string Number(double value, int decimals) =>
        value.ToString($"F{decimals}", CultureInfo.CurrentCulture);
}

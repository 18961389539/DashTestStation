using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DustLoadScada;

internal sealed class ManualPointDialog : Window
{
    public ManualPoint? Point => Addresses is { WriteAddress: ushort write, FeedbackAddress: ushort feedback }
        ? new ManualPoint(write, feedback) : null;
    public InspectionPoint? Addresses { get; private set; }

    public ManualPointDialog(ManualAction action)
        : this(action.Label, action.Point.WriteAddress, action.Point.FeedbackAddress, false) { }

    public ManualPointDialog(string label, ushort? writeAddress, ushort? feedbackAddress, bool inspection)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = inspection ? $"编辑{label}地址" : $"编辑{label}";
        Width = 465;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new StackPanel { Margin = new Thickness(22) };
        Content = layout;

        var write = AddField(layout, inspection ? "写入地址（开 1，关 0）" : "写入地址（按下 1，松开 0）",
            writeAddress is ushort w ? ManualAddress.Format(w, false) : "");
        var feedback = AddField(layout, "反馈地址（读取输出状态）", feedbackAddress is ushort f ? ManualAddress.Format(f, true) : "");
        layout.Children.Add(new TextBlock
        {
            Text = "可填写 M50、Y10，或从 0 开始的十进制 Modbus 线圈地址。Y 编号使用八进制。"
                + (inspection ? "\n写入留空使用通讯配置的默认地址，反馈留空使用原监控反馈位。" : ""),
            TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 2, 0, 14)
        });
        var error = new TextBlock { Foreground = (Brush)FindResource("Red"), TextWrapping = TextWrapping.Wrap };
        layout.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) });
        var save = new Button { Content = "保存设置", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) =>
        {
            bool hasWrite = !string.IsNullOrWhiteSpace(write.Text);
            bool hasFeedback = !string.IsNullOrWhiteSpace(feedback.Text);
            if (!ManualAddress.TryParse(write.Text, false, out ushort writeValue) && (!inspection || hasWrite))
            {
                error.Text = "请输入有效的 M 编号或 0–65535 的线圈地址。";
                write.Focus();
                return;
            }
            if (!ManualAddress.TryParse(feedback.Text, true, out ushort feedbackValue) && (!inspection || hasFeedback))
            {
                error.Text = "请输入有效的 Y 编号或 0–65535 的线圈地址。";
                feedback.Focus();
                return;
            }
            Addresses = new InspectionPoint(hasWrite ? writeValue : null, hasFeedback ? feedbackValue : null);
            DialogResult = true;
        };
        buttons.Children.Add(save);
        Loaded += (_, _) => { write.Focus(); write.SelectAll(); };
    }

    private TextBox AddField(Panel layout, string label, string value)
    {
        var input = new TextBox { Text = value, MaxLength = 12, Margin = new Thickness(0, 0, 0, 12) };
        layout.Children.Add(new Label
        {
            Content = label, Target = input, Foreground = (Brush)FindResource("Text"),
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(0, 0, 0, 5)
        });
        layout.Children.Add(input);
        return input;
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace DustLoadScada;

public sealed record DeviceLogEntry(string Time, string Kind, string Text);

// 串口参数下拉项的友好显示；SelectedItem 仍为原始枚举，读写逻辑不变。
public sealed class SerialLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Parity.None => "无校验 (N)",
        Parity.Odd => "奇校验 (O)",
        Parity.Even => "偶校验 (E)",
        Parity.Mark => "标记校验 (M)",
        Parity.Space => "空格校验 (S)",
        StopBits.One => "1 位",
        StopBits.OnePointFive => "1.5 位",
        StopBits.Two => "2 位",
        Handshake.None => "无",
        Handshake.XOnXOff => "XOn/XOff",
        Handshake.RequestToSend => "RTS/CTS",
        Handshake.RequestToSendXOnXOff => "RTS + XOn/XOff",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class DeviceManagement : UserControl
{
    private const int LogLimit = 300;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly ObservableCollection<DeviceLogEntry> cameraLog = new();
    private readonly ObservableCollection<DeviceLogEntry> plcLog = new();
    private readonly ObservableCollection<DeviceLogEntry> scannerLog = new();
    private StationStore store = null!;
    private PeripheralSettings peripherals = new(new(), new());
    private DevicePageState pageState = new();
    private string folder = "";
    private PlcSettings plc = new();
    private Func<PlcSettings, Task> connectPlc = null!;
    private Func<Task> disconnectPlc = null!;
    private string selectedDevice = "camera";
    private string? lastCameraState, lastScannerState, lastPlcState;
    private string? lastCameraRx, lastScannerRx;
    private bool busy;
    internal bool IsBusy => busy;

    public DeviceManagement()
    {
        InitializeComponent();
        CameraLog.ItemsSource = cameraLog;
        PlcLog.ItemsSource = plcLog;
        ScannerLog.ItemsSource = scannerLog;
        ScannerDataBits.ItemsSource = new[] { 5, 6, 7, 8 };
        ScannerParity.ItemsSource = Enum.GetValues<Parity>();
        ScannerStopBits.ItemsSource = new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two };
        ScannerHandshake.ItemsSource = Enum.GetValues<Handshake>();
        ScannerPort.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ScannerParam_Changed));
        timer.Tick += (_, _) => RefreshStatus();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshStatus();
                timer.Start();
            }
            else
                timer.Stop();
        };
        Unloaded += (_, _) => timer.Stop();
        ShowSettings();
        UpdatePlcTestArea();
    }

    internal void Initialize(StationStore stationStore, string dataFolder, Func<PlcSettings, Task> connect, Func<Task> disconnect)
    {
        store = stationStore;
        folder = dataFolder;
        connectPlc = connect;
        disconnectPlc = disconnect;
        peripherals = store.LoadPeripherals();
        try { plc = File.Exists(PlcPath) ? PlcSettings.Load(PlcPath) : new PlcSettings(); }
        catch (Exception ex) { PlcDetail.Text = ex.Message; }
        pageState = store.LoadDevicePageState();
        ShowSettings();
        RestorePlcTest();
        RefreshPorts();
    }

    private void RestorePlcTest()
    {
        // 先选数据区（会触发清空写入值/结果），再回填其余字段。
        PlcTestAreaPicker.SelectedValue = pageState.PlcTestArea;
        PlcTestAddress.Text = pageState.PlcTestAddress;
        PlcTestCount.Text = pageState.PlcTestCount;
        PlcTestValue.Text = pageState.PlcTestValue;
        if (pageState.PlcTestResult.Length > 0)
            PlcTestResult.Text = pageState.PlcTestResult;
    }

    private string PlcPath => Path.Combine(folder, "plc.json");

    private void DeviceSelect_Click(object sender, RoutedEventArgs e)
    {
        string device = ReferenceEquals(sender, RailPlcButton) ? "plc"
            : ReferenceEquals(sender, RailScannerButton) ? "scanner" : "camera";
        selectedDevice = device;
        RailCameraButton.Tag = device == "camera" ? "active" : null;
        RailPlcButton.Tag = device == "plc" ? "active" : null;
        RailScannerButton.Tag = device == "scanner" ? "active" : null;
        CameraPanel.Visibility = device == "camera" ? Visibility.Visible : Visibility.Collapsed;
        PlcPanel.Visibility = device == "plc" ? Visibility.Visible : Visibility.Collapsed;
        ScannerPanel.Visibility = device == "scanner" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLog(string device, string kind, string text)
    {
        var log = device switch { "plc" => plcLog, "scanner" => scannerLog, _ => cameraLog };
        log.Add(new DeviceLogEntry(DateTime.Now.ToString("HH:mm:ss.fff"), kind, text));
        while (log.Count > LogLimit)
            log.RemoveAt(0);
        var list = device switch { "plc" => PlcLog, "scanner" => ScannerLog, _ => CameraLog };
        var count = device switch { "plc" => LogCountPlc, "scanner" => LogCountScanner, _ => LogCountCamera };
        var empty = device switch { "plc" => PlcLogEmpty, "scanner" => ScannerLogEmpty, _ => CameraLogEmpty };
        var auto = device switch { "plc" => PlcLogAuto, "scanner" => ScannerLogAuto, _ => CameraLogAuto };
        count.Text = log.Count.ToString();
        empty.Visibility = Visibility.Collapsed;
        if (auto.IsChecked == true && log.Count > 0)
            list.ScrollIntoView(log[^1]);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        string device = ((Button)sender).Tag as string ?? "camera";
        var log = device switch { "plc" => plcLog, "scanner" => scannerLog, _ => cameraLog };
        log.Clear();
        var count = device switch { "plc" => LogCountPlc, "scanner" => LogCountScanner, _ => LogCountCamera };
        var empty = device switch { "plc" => PlcLogEmpty, "scanner" => ScannerLogEmpty, _ => CameraLogEmpty };
        count.Text = "0";
        empty.Visibility = Visibility.Visible;
    }

    private void ObserveState(string device, ref string? previous, string state)
    {
        if (previous is null)
        {
            previous = state;
            return;
        }
        if (previous != state)
        {
            previous = state;
            AppendLog(device, "SYS", $"状态：{state}");
        }
    }

    private Brush StateBrush(string state) => state switch
    {
        "已连接" or "PLC 已连接" => (Brush)FindResource("Green"),
        "连接失败" or "连接异常" or "已断开" => (Brush)FindResource("Red"),
        "连接中" or "重连中" or "模拟模式" => (Brush)FindResource("Accent"),
        _ => (Brush)FindResource("StateOff")
    };

    private void ShowSettings()
    {
        CameraHost.Text = peripherals.Camera.Host;
        CameraPort.Text = peripherals.Camera.Port.ToString();
        CameraMessage.Text = peripherals.Camera.SendText;
        CameraEnding.SelectedIndex = peripherals.Camera.SendEnding;
        PlcHost.Text = plc.Host;
        PlcPort.Text = plc.Port.ToString();
        PlcUnit.Text = plc.UnitId.ToString();
        ScannerPort.Text = peripherals.Scanner.Port;
        ScannerBaud.Text = peripherals.Scanner.BaudRate.ToString();
        ScannerDataBits.SelectedItem = peripherals.Scanner.DataBits;
        ScannerParity.SelectedItem = peripherals.Scanner.Parity;
        ScannerStopBits.SelectedItem = peripherals.Scanner.StopBits;
        ScannerHandshake.SelectedItem = peripherals.Scanner.Handshake;
        ScannerSendText.Text = peripherals.Scanner.SendText;
        ScannerSendFormat.SelectedIndex = peripherals.Scanner.SendHex ? 1 : 0;
        ScannerSendEnding.SelectedIndex = peripherals.Scanner.SendEnding;
        UpdateEndpoints();
        UpdateScannerSummary();
    }

    private void ScannerParam_Changed(object sender, RoutedEventArgs e) => UpdateScannerSummary();

    private void UpdateScannerSummary()
    {
        if (ScannerConfigSummary is null)
            return;
        string port = ScannerPort.Text.Trim();
        string baud = ScannerBaud.Text.Trim();
        int dataBits = ScannerDataBits.SelectedItem is int db ? db : 8;
        string parity = ScannerParity.SelectedItem is Parity p ? ParityLetter(p) : "N";
        string stop = ScannerStopBits.SelectedItem is StopBits sb ? StopNumber(sb) : "1";
        string flow = ScannerHandshake.SelectedItem is Handshake h ? FlowLabel(h) : "无";
        ScannerConfigSummary.Text = $"{(port.Length == 0 ? "未选串口" : port)} · {(baud.Length == 0 ? "—" : baud)} · {dataBits}{parity}{stop} · 流控 {flow}";
    }

    private static string ParityLetter(Parity parity) => parity switch
    {
        Parity.Odd => "O", Parity.Even => "E", Parity.Mark => "M", Parity.Space => "S", _ => "N"
    };

    private static string StopNumber(StopBits stop) => stop switch
    {
        StopBits.OnePointFive => "1.5", StopBits.Two => "2", _ => "1"
    };

    private static string FlowLabel(Handshake flow) => flow switch
    {
        Handshake.XOnXOff => "XOn/XOff", Handshake.RequestToSend => "RTS/CTS",
        Handshake.RequestToSendXOnXOff => "RTS+XOn/XOff", _ => "无"
    };

    private void UpdateEndpoints()
    {
        RailCameraEndpoint.Text = peripherals.Camera.Host.Length == 0
            ? "未配置" : $"{peripherals.Camera.Host}:{peripherals.Camera.Port}";
        RailPlcEndpoint.Text = plc.Host.Length == 0 ? "未配置" : $"{plc.Host}:{plc.Port}";
        RailScannerEndpoint.Text = peripherals.Scanner.Port.Length == 0
            ? "未配置" : $"{peripherals.Scanner.Port} · {peripherals.Scanner.BaudRate}";
        PlcInfo.Text = $"站点标识 {plc.StationId} · 轮询 {plc.PollMilliseconds} ms · 超时 {plc.TimeoutMilliseconds} ms。" +
            "站点标识、轮询与超时在 plc.json 中维护。";
    }

    private void RefreshStatus()
    {
        var camera = StationDevices.Camera.Status;
        var scanner = StationDevices.Scanner.Status;
        CameraState.Text = camera.State;
        ScannerState.Text = scanner.State;
        RailCameraState.Text = camera.State;
        RailScannerState.Text = scanner.State;
        CameraDot.Fill = StateBrush(camera.State);
        RailCameraDot.Fill = StateBrush(camera.State);
        ScannerDot.Fill = StateBrush(scanner.State);
        RailScannerDot.Fill = StateBrush(scanner.State);
        ObserveState("camera", ref lastCameraState, camera.State);
        ObserveState("scanner", ref lastScannerState, scanner.State);
        if (DataContext is StationView view)
            ObserveState("plc", ref lastPlcState, view.ConnectionLabel);
        CameraState.ToolTip = camera.Detail.Length == 0 ? null : camera.Detail;
        ScannerState.ToolTip = scanner.Detail.Length == 0 ? null : scanner.Detail;
        CameraDetail.Text = camera.State == "已连接" ? "" : camera.Detail;
        ScannerDetail.Text = scanner.State == "已连接" ? "" : scanner.Detail;
        CameraSendButton.IsEnabled = camera.State == "已连接";
        ScannerSendButton.IsEnabled = scanner.State == "已连接";
        ScannerTriggerButton.IsEnabled = scanner.State == "已连接";
        ScannerStopButton.IsEnabled = scanner.State == "已连接";
        string cameraData = OrFallback(camera.LastReceived, pageState.CameraReceived);
        if (cameraData.Length == 0)
            cameraData = "—";
        string scannerBarcode = OrFallback(scanner.LastReceived, pageState.ScannerBarcode);
        string scannerRaw = OrFallback(scanner.LastRaw, pageState.ScannerRaw);
        string scannerRawText = OrFallback(scanner.LastRawText, pageState.ScannerRawText);
        string barcode = scannerBarcode.Length == 0 ? "—" : scannerBarcode;
        string composite = barcode;
        if (scannerRaw.Length > 0)
            composite += $"{Environment.NewLine}原始字节：{scannerRaw}";
        if (scannerRawText.Length > 0)
            composite += $"{Environment.NewLine}ASCII：{scannerRawText}";
        if (CameraReceived.Text != cameraData)
            CameraReceived.Text = cameraData;
        if (ScannerReceived.Text != barcode)
            ScannerReceived.Text = barcode;
        ScannerRawLine.Text = scannerRaw.Length > 0 ? $"原始字节：{scannerRaw}" : "";
        ScannerRawLine.Visibility = scannerRaw.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScannerAsciiLine.Text = scannerRawText.Length > 0 ? $"ASCII：{scannerRawText}" : "";
        ScannerAsciiLine.Visibility = scannerRawText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScannerCopyButton.IsEnabled = scannerBarcode.Length > 0;
        if (lastCameraRx is null)
            lastCameraRx = cameraData;
        else if (cameraData != lastCameraRx)
        {
            lastCameraRx = cameraData;
            if (cameraData != "—")
                AppendLog("camera", "RX", cameraData);
        }
        if (lastScannerRx is null)
            lastScannerRx = composite;
        else if (composite != lastScannerRx)
        {
            lastScannerRx = composite;
            if (composite != "—")
                AppendLog("scanner", "RX", composite);
        }
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (busy || store is null)
            return;
        busy = true;
        IsEnabled = false;
        try { await work(); }
        catch (Exception ex)
        {
            AppendLog(selectedDevice, "ERR", ex.Message);
            MessageBox.Show(Window.GetWindow(this), ex.Message, "设备管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            busy = false;
            IsEnabled = true;
            RefreshStatus();
        }
    }

    private CameraSettings ReadCamera() => new(Host(CameraHost.Text, "相机"), Number(CameraPort.Text, 1, 65535, "相机端口"),
        CameraMessage.Text, CameraEnding.SelectedIndex);

    private ScannerSettings ReadScanner()
    {
        string port = ScannerPort.Text.Trim().ToUpperInvariant();
        if (!port.StartsWith("COM", StringComparison.Ordinal) || !int.TryParse(port.AsSpan(3), out int number) || number <= 0)
            throw new InvalidOperationException("请选择串口或输入有效的 COM 端口。");
        return new ScannerSettings(port, Number(ScannerBaud.Text, 1, 4_000_000, "波特率"),
            (int)ScannerDataBits.SelectedItem, (Parity)ScannerParity.SelectedItem,
            (StopBits)ScannerStopBits.SelectedItem, (Handshake)ScannerHandshake.SelectedItem,
            ScannerSendText.Text, ScannerSendFormat.SelectedIndex == 1, ScannerSendEnding.SelectedIndex);
    }

    private PlcSettings ReadPlc()
    {
        var existing = File.Exists(PlcPath) ? PlcSettings.Load(PlcPath) : plc;
        return existing with
        {
            Host = Host(PlcHost.Text, "PLC"),
            Port = Number(PlcPort.Text, 1, 65535, "PLC 端口"),
            UnitId = (byte)Number(PlcUnit.Text, 0, 255, "PLC 站号")
        };
    }

    private void SavePeripherals(PeripheralSettings settings)
    {
        store.SavePeripherals(settings);
        peripherals = settings;
    }

    private void SavePlc(PlcSettings settings)
    {
        settings.Save(PlcPath);
        plc = settings;
    }

    private async void CameraConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var settings = ReadCamera();
        AppendLog("camera", "SYS", $"开始连接 {settings.Host}:{settings.Port} …");
        await StationDevices.Camera.ConnectAsync(settings);
        RailCameraEndpoint.Text = $"{settings.Host}:{settings.Port}";
    });

    private async void CameraDisconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await StationDevices.Camera.DisconnectAsync();
        AppendLog("camera", "SYS", "已断开连接。");
    });

    private async void SaveAll_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        SetNotice(SaveAllSettings());
        return Task.CompletedTask;
    });

    internal string SaveAllSettings()
    {
        var camera = string.IsNullOrWhiteSpace(CameraHost.Text) ? null : ReadCamera();
        var scanner = string.IsNullOrWhiteSpace(ScannerPort.Text) ? null : ReadScanner();
        var plcSettings = string.IsNullOrWhiteSpace(PlcHost.Text) ? null : ReadPlc();
        plcSettings?.Validate();
        if (camera is null && scanner is null && plcSettings is null)
            throw new InvalidOperationException("请至少填写一个设备的主机或 COM 口；未保存任何配置。");
        if (plcSettings is not null && DataContext is StationView { CanSwitchMode: false })
            throw new InvalidOperationException("请等待当前操作或测试结束后再保存设备配置。");

        var saved = new List<string>();
        var skipped = new List<string>();
        if (camera is null) skipped.Add("相机");
        if (scanner is null) skipped.Add("扫码枪");
        if (plcSettings is null) skipped.Add("PLC");
        try
        {
            if (camera is not null || scanner is not null)
            {
                SavePeripherals(peripherals with
                {
                    Camera = camera ?? peripherals.Camera,
                    Scanner = scanner ?? peripherals.Scanner
                });
                if (camera is not null) saved.Add("相机");
                if (scanner is not null) saved.Add("扫码枪");
            }
            if (plcSettings is not null)
            {
                SavePlc(plcSettings);
                saved.Add("PLC");
                PlcDetail.Text = "PLC 配置已保存，下次连接时生效。";
            }
        }
        catch (Exception ex)
        {
            string completed = saved.Count == 0 ? "无" : string.Join("、", saved);
            throw new InvalidOperationException($"设备配置保存失败。已保存：{completed}；其他填写的设备未保存，请重试。{ex.Message}", ex);
        }

        pageState = CapturePageState();
        store.SaveDevicePageState(pageState);

        string notice = $"已保存：{string.Join("、", saved)}。配置在下次连接或启动时生效，当前连接不变。";
        if (skipped.Count > 0)
            notice += $"未填写的设备已跳过：{string.Join("、", skipped)}（保留原配置）。";
        UpdateEndpoints();
        AppendLog(selectedDevice, "SYS", notice);
        return notice;
    }

    private DevicePageState CapturePageState()
    {
        var camera = StationDevices.Camera.Status;
        var scanner = StationDevices.Scanner.Status;
        return new DevicePageState(
            camera.LastReceived,
            scanner.LastReceived,
            scanner.LastRaw,
            scanner.LastRawText,
            PlcTestAreaPicker.SelectedValue as string ?? "HoldingRegisters",
            PlcTestAddress.Text,
            PlcTestCount.Text,
            PlcTestValue.Text,
            PlcTestResult.Text == "—" ? "" : PlcTestResult.Text);
    }

    private static string OrFallback(string live, string saved) => live.Length > 0 ? live : saved;

    private async void CameraSend_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (CameraMessage.Text.Length == 0)
            throw new InvalidOperationException("请输入相机命令。");
        string ending = CameraEnding.SelectedIndex switch { 1 => "\r", 2 => "\n", 3 => "\r\n", _ => "" };
        string text = CameraMessage.Text + ending;
        AppendLog("camera", "TX", $"{text}（{Hex(Encoding.UTF8.GetBytes(text))}）");
        return StationDevices.Camera.SendAsync(text);
    });

    private async void PlcConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var settings = ReadPlc();
        AppendLog("plc", "SYS", $"开始连接 {settings.Host}:{settings.Port}，站号 {settings.UnitId} …");
        await connectPlc(settings);
        PlcDetail.Text = $"已应用 {settings.Host}:{settings.Port}，站号 {settings.UnitId}。";
        RailPlcEndpoint.Text = $"{settings.Host}:{settings.Port}";
    });

    private async void PlcDisconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await disconnectPlc();
        PlcDetail.Text = "已切回模拟模式。";
        AppendLog("plc", "SYS", "已切回模拟模式。");
    });

    private PlcTestArea SelectedTestArea =>
        Enum.TryParse<PlcTestArea>(PlcTestAreaPicker.SelectedValue as string, out var area)
            ? area : PlcTestArea.HoldingRegisters;

    private void PlcTestArea_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PlcTestHint is null)
            return;
        UpdatePlcTestArea();
    }

    private void UpdatePlcTestArea()
    {
        var area = SelectedTestArea;
        PlcTestWriteControls.Visibility = area is PlcTestArea.HoldingRegisters or PlcTestArea.Coils
            ? Visibility.Visible : Visibility.Collapsed;
        PlcTestHint.Text = area switch
        {
            PlcTestArea.HoldingRegisters => "写入范围 0–65535",
            PlcTestArea.Coils => "0 关 / 1 开",
            _ => "该数据区只读。"
        };
        PlcTestValue.Clear();
        PlcTestResult.Text = "—";
    }

    private async void PlcTestRead_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (DataContext is not StationView { CanReadPlcTest: true, Station: ModbusStation station })
            return;
        await ShowPlcTestResultAsync(async () =>
        {
            var area = SelectedTestArea;
            ushort address = (ushort)Number(PlcTestAddress.Text, 0, 65535, "起始地址");
            ushort count = (ushort)Number(PlcTestCount.Text, 1, 125, "读取数量");
            AppendLog("plc", "TX", $"{TestAreaLabel(area)} 读取 地址={address} 数量={count}");
            var values = await station.ReadTestAsync(area, address, count);
            bool bitArea = area is PlcTestArea.Coils or PlcTestArea.DiscreteInputs;
            string result = string.Join(Environment.NewLine, values.Select((value, i) => bitArea
                ? $"{address + i}: {value}" : $"{address + i}: {value} (0x{value:X4})"));
            AppendLog("plc", "RX", result.Replace(Environment.NewLine, "；"));
            return result;
        });
    });

    private async void PlcTestWrite_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (DataContext is not StationView { CanWritePlcTest: true, Station: ModbusStation station })
            return;
        await ShowPlcTestResultAsync(async () =>
        {
            var area = SelectedTestArea;
            ushort address = (ushort)Number(PlcTestAddress.Text, 0, 65535, "起始地址");
            ushort value = (ushort)Number(PlcTestValue.Text, 0, area == PlcTestArea.Coils ? 1 : 65535, "写入值");
            AppendLog("plc", "TX", $"{TestAreaLabel(area)} 写入 {address} = {value}");
            await station.WriteTestAsync(area, address, value);
            AppendLog("plc", "RX", $"写入确认 {address} = {value}");
            return $"PLC 已确认写入 {address} = {value}。";
        });
    });

    private static string TestAreaLabel(PlcTestArea area) => area switch
    {
        PlcTestArea.HoldingRegisters => "FC03/06 保持寄存器",
        PlcTestArea.InputRegisters => "FC04 输入寄存器",
        PlcTestArea.Coils => "FC01/05 线圈",
        _ => "FC02 离散输入"
    };

    private async Task ShowPlcTestResultAsync(Func<Task<string>> operation)
    {
        PlcTestResult.Text = "执行中…";
        try { PlcTestResult.Text = await operation(); }
        catch (Exception ex)
        {
            PlcTestResult.Text = $"测试失败。{ex.Message}";
            AppendLog("plc", "ERR", ex.Message);
        }
    }

    private async void ScannerConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var settings = ReadScanner();
        AppendLog("scanner", "SYS", $"开始连接 {settings.Port} · {settings.BaudRate} bps …");
        await StationDevices.Scanner.ConnectAsync(settings);
        RailScannerEndpoint.Text = $"{settings.Port} · {settings.BaudRate}";
    });

    private async void ScannerDisconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await StationDevices.Scanner.DisconnectAsync();
        AppendLog("scanner", "SYS", "已断开连接。");
    });

    private void ScannerSendFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ScannerSendHint is null)
            return;
        bool hex = ScannerSendFormat.SelectedIndex == 1;
        ScannerSendEnding.IsEnabled = !hex;
        ScannerSendHint.Text = hex ? "例如 02 41 03，按原始字节发送。" : "按 UTF-8 编码发送。";
    }

    private async void ScannerSend_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        bool hex = ScannerSendFormat.SelectedIndex == 1;
        string ending = ScannerSendEnding.SelectedIndex switch { 1 => "\r", 2 => "\n", 3 => "\r\n", _ => "" };
        byte[] bytes = ScannerCommand.Encode(ScannerSendText.Text, hex, ending);
        AppendLog("scanner", "TX", Hex(bytes));
        await StationDevices.Scanner.SendAsync(bytes);
        SetNotice($"扫码枪指令已发送，共 {bytes.Length} 字节。");
    });

    private async void ScannerTrigger_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        AppendLog("scanner", "TX", "触发扫码（01 54 04）");
        await StationDevices.Scanner.StartScanAsync();
        SetNotice("已发送触发扫码指令，等待条码回传。");
    });

    private async void ScannerStop_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        AppendLog("scanner", "TX", "停止扫码（01 50 04）");
        await StationDevices.Scanner.StopScanAsync();
        SetNotice("已发送停止扫码指令。");
    });

    private void ScannerCopy_Click(object sender, RoutedEventArgs e)
    {
        string text = ScannerReceived.Text;
        if (text.Length == 0 || text == "—")
            return;
        try
        {
            Clipboard.SetText(text);
            AppendLog("scanner", "SYS", $"已复制条码：{text}");
            SetNotice($"已复制条码：{text}");
        }
        catch (Exception ex)
        {
            SetNotice($"复制失败：{ex.Message}");
        }
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        string selected = ScannerPort.Text;
        try
        {
            var ports = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            ScannerPort.ItemsSource = ports;
            ScannerPort.Text = selected;
            AppendLog("scanner", "SYS", ports.Length == 0 ? "刷新串口：本机未发现 COM 端口。" : $"刷新串口：{string.Join("、", ports)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetNotice($"无法枚举串口，可手动输入 COM 端口。{ex.Message}");
            AppendLog("scanner", "ERR", $"无法枚举串口：{ex.Message}");
        }
    }

    private static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    private void SetNotice(string message)
    {
        if (DataContext is StationView view)
            view.Notice = message;
    }

    private static string Host(string value, string name) => Uri.CheckHostName(value.Trim()) != UriHostNameType.Unknown
        ? value.Trim() : throw new InvalidOperationException($"请输入有效的{name} IP 或主机名。");

    private static int Number(string text, int min, int max, string name) => int.TryParse(text, out int value) && value >= min && value <= max
        ? value : throw new InvalidOperationException($"{name}须为 {min}–{max} 之间的整数。");
}

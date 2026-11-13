using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DustLoadScada;

public partial class DeviceManagement : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private StationStore store = null!;
    private PeripheralSettings peripherals = new(new(), new());
    private string folder = "";
    private PlcSettings plc = new();
    private Func<PlcSettings, Task> connectPlc = null!;
    private Func<Task> disconnectPlc = null!;
    private bool busy;
    internal bool IsBusy => busy;

    public DeviceManagement()
    {
        InitializeComponent();
        ScannerDataBits.ItemsSource = new[] { 5, 6, 7, 8 };
        ScannerParity.ItemsSource = Enum.GetValues<Parity>();
        ScannerStopBits.ItemsSource = new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two };
        ScannerHandshake.ItemsSource = Enum.GetValues<Handshake>();
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
        ShowSettings();
        RefreshPorts();
    }

    private string PlcPath => Path.Combine(folder, "plc.json");

    private void ShowSettings()
    {
        CameraHost.Text = peripherals.Camera.Host;
        CameraPort.Text = peripherals.Camera.Port.ToString();
        PlcHost.Text = plc.Host;
        PlcPort.Text = plc.Port.ToString();
        PlcUnit.Text = plc.UnitId.ToString();
        PlcWrites.IsChecked = plc.EnableWrites;
        PlcManualWrites.IsChecked = plc.EnableManualWrites;
        ScannerPort.Text = peripherals.Scanner.Port;
        ScannerBaud.Text = peripherals.Scanner.BaudRate.ToString();
        ScannerDataBits.SelectedItem = peripherals.Scanner.DataBits;
        ScannerParity.SelectedItem = peripherals.Scanner.Parity;
        ScannerStopBits.SelectedItem = peripherals.Scanner.StopBits;
        ScannerHandshake.SelectedItem = peripherals.Scanner.Handshake;
    }

    private void RefreshStatus()
    {
        var camera = StationDevices.Camera.Status;
        var scanner = StationDevices.Scanner.Status;
        CameraState.Text = camera.State;
        ScannerState.Text = scanner.State;
        CameraState.ToolTip = camera.Detail.Length == 0 ? null : camera.Detail;
        ScannerState.ToolTip = scanner.Detail.Length == 0 ? null : scanner.Detail;
        CameraDetail.Text = camera.State == "已连接" ? "" : camera.Detail;
        ScannerDetail.Text = scanner.State == "已连接" ? "" : scanner.Detail;
        CameraSendButton.IsEnabled = camera.State == "已连接";
        ScannerSendButton.IsEnabled = scanner.State == "已连接";
        string cameraData = camera.LastReceived.Length == 0 ? "—" : camera.LastReceived;
        string barcode = scanner.LastReceived.Length == 0 ? "—" : scanner.LastReceived;
        if (CameraReceived.Text != cameraData)
            CameraReceived.Text = cameraData;
        if (ScannerReceived.Text != barcode)
            ScannerReceived.Text = barcode;
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
            MessageBox.Show(Window.GetWindow(this), ex.Message, "设备管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            busy = false;
            IsEnabled = true;
            RefreshStatus();
        }
    }

    private CameraSettings ReadCamera() => new(Host(CameraHost.Text, "相机"), Number(CameraPort.Text, 1, 65535, "相机端口"));

    private ScannerSettings ReadScanner()
    {
        string port = ScannerPort.Text.Trim().ToUpperInvariant();
        if (!port.StartsWith("COM", StringComparison.Ordinal) || !int.TryParse(port.AsSpan(3), out int number) || number <= 0)
            throw new InvalidOperationException("请选择串口或输入有效的 COM 端口。");
        return new ScannerSettings(port, Number(ScannerBaud.Text, 1, 4_000_000, "波特率"),
            (int)ScannerDataBits.SelectedItem, (Parity)ScannerParity.SelectedItem,
            (StopBits)ScannerStopBits.SelectedItem, (Handshake)ScannerHandshake.SelectedItem);
    }

    private PlcSettings ReadPlc()
    {
        var existing = File.Exists(PlcPath) ? PlcSettings.Load(PlcPath) : plc;
        return existing with
        {
            Host = Host(PlcHost.Text, "PLC"),
            Port = Number(PlcPort.Text, 1, 65535, "PLC 端口"),
            UnitId = (byte)Number(PlcUnit.Text, 0, 255, "PLC 站号"),
            EnableWrites = PlcWrites.IsChecked == true,
            EnableManualWrites = PlcWrites.IsChecked == true && PlcManualWrites.IsChecked == true
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

    private async void CameraConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
        StationDevices.Camera.ConnectAsync(ReadCamera()));

    private async void CameraDisconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(StationDevices.Camera.DisconnectAsync);

    private async void CameraSave_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        SavePeripherals(peripherals with { Camera = ReadCamera() });
        SetNotice("相机配置已保存。");
        return Task.CompletedTask;
    });

    private async void CameraSend_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        if (CameraMessage.Text.Length == 0)
            throw new InvalidOperationException("请输入相机命令。");
        string ending = CameraEnding.SelectedIndex switch { 1 => "\r", 2 => "\n", 3 => "\r\n", _ => "" };
        return StationDevices.Camera.SendAsync(CameraMessage.Text + ending);
    });

    private async void PlcConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var settings = ReadPlc();
        await connectPlc(settings);
        PlcDetail.Text = $"已应用 {settings.Host}:{settings.Port}，站号 {settings.UnitId}。";
    });

    private async void PlcDisconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        await disconnectPlc();
        PlcDetail.Text = "已切回模拟模式。";
    });

    private async void PlcSave_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        SavePlc(ReadPlc());
        PlcDetail.Text = "PLC 配置已保存，下次连接时生效。";
        return Task.CompletedTask;
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
            PlcTestArea.HoldingRegisters => "写入范围 0–65535 · 需负责人及写入权限",
            PlcTestArea.Coils => "0 关 / 1 开 · 需负责人及写入权限",
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
            var values = await station.ReadTestAsync(area, address, count);
            bool bitArea = area is PlcTestArea.Coils or PlcTestArea.DiscreteInputs;
            return string.Join(Environment.NewLine, values.Select((value, i) => bitArea
                ? $"{address + i}: {value}" : $"{address + i}: {value} (0x{value:X4})"));
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
            await station.WriteTestAsync(area, address, value);
            return $"PLC 已确认写入 {address} = {value}。";
        });
    });

    private async Task ShowPlcTestResultAsync(Func<Task<string>> operation)
    {
        PlcTestResult.Text = "执行中…";
        try { PlcTestResult.Text = await operation(); }
        catch (Exception ex) { PlcTestResult.Text = $"测试失败。{ex.Message}"; }
    }

    private async void ScannerConnect_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
        StationDevices.Scanner.ConnectAsync(ReadScanner()));

    private async void ScannerDisconnect_Click(object sender, RoutedEventArgs e) => await RunAsync(StationDevices.Scanner.DisconnectAsync);

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
        await StationDevices.Scanner.SendAsync(bytes);
        SetNotice($"扫码枪指令已发送，共 {bytes.Length} 字节。");
    });

    private async void ScannerSave_Click(object sender, RoutedEventArgs e) => await RunAsync(() =>
    {
        SavePeripherals(peripherals with { Scanner = ReadScanner() });
        SetNotice("扫码枪配置已保存。");
        return Task.CompletedTask;
    });

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        string selected = ScannerPort.Text;
        try
        {
            ScannerPort.ItemsSource = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            ScannerPort.Text = selected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SetNotice($"无法枚举串口，可手动输入 COM 端口。{ex.Message}");
        }
    }

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

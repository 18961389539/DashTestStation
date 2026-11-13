using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DustLoadScada;

public partial class MainWindow : Window
{
    private const string AdministratorPassword = "123456";
    private const string RecordTriggerLabel = "记录触发";
    private static readonly (MonitorPointId Id, string Label)[] HistoryFields =
    [
        (MonitorPointId.NormalVoltage, "正常电压"),
        (MonitorPointId.BrushLightVerdict, "地刷灯亮判定"),
        (MonitorPointId.NormalCurrent, "正常电流"),
        (MonitorPointId.NormalCurrentVerdict, "正常电流判定结果"),
        (MonitorPointId.NormalPower, "正常功率"),
        (MonitorPointId.NormalPowerVerdict, "正常功率判定结果"),
        (MonitorPointId.NormalSpeed, "正常转速"),
        (MonitorPointId.NormalSpeedVerdict, "正常转速判定结果"),
        (MonitorPointId.DirectionVerdict, "转速方向判定结果"),
        (MonitorPointId.BrushLightOffVerdict, "地刷灯灭判定"),
        (MonitorPointId.DustCurrent, "灰尘测试电流"),
        (MonitorPointId.DustCurrentVerdict, "灰尘电流判定结果"),
        (MonitorPointId.DustPower, "灰尘测试功率"),
        (MonitorPointId.DustPowerVerdict, "灰尘功率判定结果"),
        (MonitorPointId.DustSpeed, "灰尘测试转速"),
        (MonitorPointId.DustSpeedVerdict, "灰尘转速判定结果"),
        (MonitorPointId.Result, "总判定结果")
    ];

    private sealed record HistoryPointRow(
        MonitorPointId? Id, string Group, string Label, string Address, string Offset,
        string DataType, string Status, string StatusKind);
    private readonly StationView view = new();
    private readonly string dataFolder;
    private readonly FrameworkElement[] pages;
    private readonly Mutex appMutex;
    private StationStore store = null!;
    private Dictionary<MonitorPointId, MonitorPoint> monitorPoints = new();
    private BarcodePoint? barcodePoint;
    private ushort? recordTrigger;
    private ushort? timerTrigger;
    private ScannerTriggerSettings scannerTrigger = new();
    private Dictionary<ManualPointId, ManualPoint> manualPoints = new();
    private Dictionary<RecipePointId, ushort> recipePoints = new();
    private HistoryPointRow[] historyPointRows = [];
    private DispatcherTimer? historyPointHighlightTimer;
    private DataGridRow? highlightedHistoryPointRow;
    private Dictionary<string, InspectionPoint> inspectionPoints = new();
    private Button? heldManualButton;
    private Key heldManualKey;
    private Task manualPress = Task.CompletedTask;
    private Task manualRelease = Task.CompletedTask;
    private bool manualReleasePending;
    private CancellationTokenSource? pollCancellation;
    private Task polling = Task.CompletedTask;
    private Task recipeDispatch = Task.CompletedTask;
    private uint lastCompletedCycle;
    private bool selectingRecipe;
    private bool closing;
    private bool allowClose;
    private bool ownsMutex;
    private string? storageError;
    private int currentPage;
    private ushort? runtimePoint;
    private RuntimeCounter runtime = new(TimeSpan.Zero);
    private string? runtimeError;

    public MainWindow()
    {
        InitializeComponent();
        pages = [MonitorPage, ManualPage, RecipePage, HistoryPage, ContactPage, AccessPage, DevicesPage, SystemPage];
        string? overrideFolder = Environment.GetEnvironmentVariable("DUSTLOADSCADA_DATA_DIR");
        dataFolder = string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DustLoadScada")
            : Path.GetFullPath(overrideFolder);
        var folderHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(dataFolder.ToUpperInvariant())));
        appMutex = new Mutex(false, $"Local\\DustLoadScada-{folderHash}");
        Loaded += Start;
        Closing += CloseStation;
        Deactivated += (_, _) => ReleaseHeldManual();
        Closed += (_, _) =>
        {
            if (ownsMutex)
                appMutex.ReleaseMutex();
            appMutex.Dispose();
        };
    }

    private async void Start(object sender, RoutedEventArgs e)
    {
        Loaded -= Start;
        try
        {
            try { ownsMutex = appMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
                throw new InvalidOperationException("此数据目录已有一个测试台窗口在运行。");

            store = new StationStore(dataFolder);
            monitorPoints = store.LoadMonitorPoints();
            barcodePoint = store.LoadBarcodePoint();
            recordTrigger = store.LoadRecordTrigger();
            timerTrigger = store.LoadTimerTrigger();
            scannerTrigger = store.LoadScannerTrigger();
            manualPoints = store.LoadManualPoints();
            recipePoints = store.LoadRecipePoints();
            inspectionPoints = store.LoadInspectionPoints();
            runtimePoint = store.LoadRuntimePoint();
            runtime = new RuntimeCounter(store.LoadRuntimeTotal());
            ShowRuntimeSettings();
            ShowScannerSettings();
            UpdateRuntimeView();
            view.SetManualPoints(manualPoints);
            view.ActiveRecipe = store.ActiveRecipe() ?? store.Recipes()[0];
            await view.Station.ApplyAsync(view.ActiveRecipe);
            DataContext = view;
            DevicesPage.Initialize(store, dataFolder, settings => SwitchStationAsync(settings), () => SwitchStationAsync(null));
            LoadRecipes(view.ActiveRecipe.Id);
            view.Use(view.Station);
            StartPolling();
            await AutoConnectDevicesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法启动灰尘负载测试台", MessageBoxButton.OK, MessageBoxImage.Error);
            allowClose = true;
            Close();
        }
    }

    private async void CloseStation(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;
        e.Cancel = true;
        if (!view.NotBusy || DevicesPage.IsBusy || closing)
            return;
        closing = true;
        ReleaseHeldManual();
        await recipeDispatch;
        try { await FlushManualReleaseAsync(); }
        catch (Exception ex)
        {
            closing = false;
            view.Notice = $"手动命令未确认清零。{ex.Message}";
            return;
        }
        await StopPollingAsync();
        CloseStationConnection();
        await Task.WhenAll(StationDevices.Camera.DisconnectAsync(), StationDevices.Scanner.DisconnectAsync());
        allowClose = true;
        Close();
    }

    private void StartPolling()
    {
        pollCancellation = new CancellationTokenSource();
        polling = Task.WhenAll(PollAsync(view.Station, pollCancellation.Token),
            PollManualAsync(view.Station, pollCancellation.Token),
            PollScannerAsync(view.Station, pollCancellation.Token));
    }

    private async Task StopPollingAsync()
    {
        if (pollCancellation is null)
            return;
        pollCancellation.Cancel();
        await polling;
        pollCancellation.Dispose();
        pollCancellation = null;
        runtime.Lost();
        SaveRuntime();
        UpdateRuntimeView();
    }

    private async Task SampleRuntimeAsync(IStation station, CancellationToken cancellation)
    {
        if (station is not ModbusStation plc || runtimePoint is not ushort address)
        {
            runtime.Lost();
            UpdateRuntimeView();
            return;
        }
        try
        {
            bool on = await plc.ReadCoilAsync(address, cancellation);
            if (cancellation.IsCancellationRequested || address != runtimePoint)
                return;
            if (runtime.Sample(on, System.Diagnostics.Stopwatch.GetTimestamp()))
                SaveRuntime();
            if (runtimeError is not null && runtimeError.StartsWith("读取", StringComparison.Ordinal))
                runtimeError = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            runtime.Lost();
            runtimeError = $"读取运行线圈失败。{ex.Message}";
            if (runtime.Dirty)
                SaveRuntime();
        }
        UpdateRuntimeView();
    }

    private void SaveRuntime()
    {
        if (!runtime.Dirty)
            return;
        try
        {
            store.SaveRuntimeTotal(runtime.Total);
            runtime.MarkSaved();
            if (runtimeError is not null && runtimeError.StartsWith("保存", StringComparison.Ordinal))
                runtimeError = null;
        }
        catch (Exception ex) { runtimeError = $"保存运行时间失败，将重试。{ex.Message}"; }
    }

    private void UpdateRuntimeView()
    {
        string status = runtimeError ?? (runtimePoint is not ushort address ? "未配置运行地址"
            : view.IsSimulation ? $"{FormatCoil(address)} · 模拟模式不计时"
            : runtime.Running switch
            {
                true => $"{FormatCoil(address)} = 1 · 计时中",
                false => $"{FormatCoil(address)} = 0 · 暂停",
                null => $"{FormatCoil(address)} · 等待 PLC"
            });
        view.UpdateRuntime(runtime.Total, status);
    }

    private static string FormatCoil(ushort address) => PlcAddress.FormatCoil(address);

    private void ShowRuntimeSettings()
    {
        RuntimeAddress.Text = runtimePoint is ushort address ? FormatCoil(address) : "";
        RuntimeSettingsError.Text = "";
    }

    private void SaveRuntimeSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditSystemSettings || closing)
            return;
        string text = RuntimeAddress.Text.Trim();
        ushort? updated = null;
        if (text.Length != 0)
        {
            if (!PlcAddress.TryParseCoil(text, out ushort value))
            {
                RuntimeSettingsError.Text = "请输入 M10、Y10、X0、S0、HM0 或从 0 开始的十进制线圈地址。";
                RuntimeAddress.Focus();
                return;
            }
            updated = value;
        }
        try { store.SaveRuntimePoint(updated); }
        catch (Exception ex)
        {
            RuntimeSettingsError.Text = $"保存失败。{ex.Message}";
            return;
        }
        if (updated != runtimePoint)
        {
            runtime.Lost();
            SaveRuntime();
            runtimeError = null;
        }
        runtimePoint = updated;
        ShowRuntimeSettings();
        UpdateRuntimeView();
        view.Notice = updated is ushort address ? $"运行时间统计地址已设为 {FormatCoil(address)}。" : "已取消运行时间统计。";
    }

    private void ShowScannerSettings()
    {
        ScannerTriggerAddress.Text = scannerTrigger.TriggerAddress is ushort trigger ? FormatCoil(trigger) : "";
        ScannerBarcodeAddress.Text = PlcAddress.FormatRegister(scannerTrigger.BarcodeAddress);
        ScannerVerdictAddress.Text = PlcAddress.FormatRegister(scannerTrigger.VerdictAddress);
        ScannerSettingsError.Text = "";
    }

    private async void SaveScannerSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditSystemSettings || closing)
            return;

        string triggerText = ScannerTriggerAddress.Text.Trim();
        ushort? trigger = null;
        if (triggerText.Length != 0)
        {
            if (!PlcAddress.TryParseCoil(triggerText, out ushort address))
            {
                ScannerSettingsError.Text = "请输入 M、Y、X、S、HM 编号或从 0 开始的十进制线圈地址。";
                ScannerTriggerAddress.Focus();
                return;
            }
            trigger = address;
        }

        if (!ScannerTriggerSettings.TryParseRegister(ScannerBarcodeAddress.Text, out ushort barcodeAddress))
        {
            ScannerSettingsError.Text = "请输入有效的二维码 D 寄存器地址，例如 D4000。";
            ScannerBarcodeAddress.Focus();
            return;
        }
        if (!ScannerTriggerSettings.TryParseRegister(ScannerVerdictAddress.Text, out ushort verdictAddress))
        {
            ScannerSettingsError.Text = "请输入有效的比对结果 D 寄存器地址，例如 D379。";
            ScannerVerdictAddress.Focus();
            return;
        }

        var updated = new ScannerTriggerSettings(trigger, barcodeAddress, verdictAddress);
        try
        {
            updated.Validate();
            if (view.Station is ModbusStation plc)
                await plc.UpdateScannerSettingsAsync(updated, () => store.SaveScannerTrigger(updated));
            else
                store.SaveScannerTrigger(updated);
        }
        catch (Exception ex)
        {
            ScannerSettingsError.Text = $"保存失败。{ex.Message}";
            return;
        }

        scannerTrigger = updated;
        ShowScannerSettings();
        view.Notice = trigger is ushort coil
            ? $"扫码触发及写回地址已保存：{FormatCoil(coil)}、D{barcodeAddress}、D{verdictAddress}。"
            : "扫码触发已停用；二维码及比对结果地址已保存。";
    }

    private async void CameraCapture_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanCapture || closing)
            return;
        CameraCaptureResult.Text = "拍照中…";
        CameraCaptureResult.Foreground = (Brush)FindResource("Muted");
        string result = "拍照失败";
        await PerformAsync(async () =>
        {
            string answer;
            try { answer = await StationDevices.Camera.RequestAsync("start", TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { throw new InvalidOperationException($"相机拍照命令未成功。{ex.Message}", ex); }
            if (answer.Equals("fail", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("相机拍照失败（返回 fail）。");
            if (!answer.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"相机返回“{answer}”，无法确认拍照结果。");
            result = "拍照成功";
            view.Notice = "相机拍照成功。";
        });
        CameraCaptureResult.Text = $"{result} · {DateTime.Now:HH:mm:ss}";
        CameraCaptureResult.Foreground = (Brush)FindResource(result == "拍照成功" ? "Green" : "Red");
    }

    private void ResetRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditSystemSettings || closing)
            return;
        if (MessageBox.Show(this, $"确定将累计运行时间 {RuntimeCounter.Format(runtime.Total)} 清零吗？", "运行时间清零",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        try { store.SaveRuntimeTotal(TimeSpan.Zero); }
        catch (Exception ex)
        {
            RuntimeSettingsError.Text = $"清零失败。{ex.Message}";
            return;
        }
        runtime.Reset();
        UpdateRuntimeView();
        view.Notice = "累计运行时间已清零。";
    }

    private async Task PollAsync(IStation station, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await SampleRuntimeAsync(station, cancellation);
                if (station is ModbusStation plc && recordTrigger.HasValue)
                {
                    try
                    {
                        bool? saved = await plc.ProcessRecordTriggerAsync(test => store.Record(plc.Source, test), cancellation);
                        if (saved.HasValue || storageError is not null)
                        {
                            bool recovered = storageError is not null;
                            storageError = null;
                            if (saved == true || (saved == false && recovered))
                                NotifyArchived(station, "PLC 测试结果已保存，触发线圈已清零。");
                            else if (recovered)
                                NotifyArchived(station, "记录触发通讯已恢复。");
                        }

                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        storageError = $"记录触发处理失败，将重试。{ex.Message}";
                        view.Notice = storageError;
                    }
                }
                StationReading? reading = null;
                try { reading = await station.ReadAsync(cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!cancellation.IsCancellationRequested)
                        view.Offline(ex.Message);
                }

                if (reading is { } sample && !cancellation.IsCancellationRequested)
                {
                    view.Update(sample.Test, sample.Feedback, sample.UnknownFeedback);
                    if (sample.Test.Running || sample.Test.Stage == TestStage.Fault)
                        ReleaseHeldManual();
                    if (sample.Test.Stage == TestStage.Completed && UsesCompletionArchive(station))
                    {
                        try { Archive(station, sample.Test); }
                        catch (Exception ex)
                        {
                            storageError = $"测试结果存档失败，将重试。{ex.Message}";
                            view.Notice = storageError;
                        }
                    }
                }

                await Task.Delay(reading.HasValue ? station.PollMilliseconds : 1000, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task PollScannerAsync(IStation station, CancellationToken cancellation)
    {
        if (station is not ModbusStation plc)
            return;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (scannerTrigger.TriggerAddress.HasValue)
                {
                    try
                    {
                        Recipe recipe = view.ActiveRecipe with { };
                        ScannerResult? result = await plc.ProcessScannerTriggerAsync(async token =>
                        {
                            string barcode = await StationDevices.Scanner.ScanAsync(TimeSpan.FromSeconds(3), token);
                            return ScannerResult.Create(barcode, recipe);
                        }, cancellation);
                        if (result is not null)
                        {
                            view.UpdateScannerResult(result);
                            view.Notice = $"扫码完成：{result.Comparison}。";
                        }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        if (cancellation.IsCancellationRequested)
                            break;
                        if (plc.PendingScannerResult is { } pending)
                            view.UpdateScannerResult(pending);
                        view.UpdateScannerStatus("扫码或 PLC 写入失败，等待重试");
                        view.Notice = $"扫码触发处理失败，将重试。{ex.Message}";
                    }
                }

                await Task.Delay(station.PollMilliseconds, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task PollManualAsync(IStation station, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (currentPage == 1 || manualReleasePending)
                {
                    try
                    {
                        if (manualReleasePending && manualRelease.IsCompleted)
                        {
                            await station.ReleaseManualAsync();
                            manualReleasePending = false;
                        }
                        ushort feedback = await station.ReadManualAsync(cancellation);
                        if (cancellation.IsCancellationRequested)
                            break;
                        if (currentPage == 1)
                            view.UpdateManual(feedback);
                        view.ManualError = "";
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        if (cancellation.IsCancellationRequested)
                            break;
                        view.UpdateManual(null);
                        ReleaseHeldManual();
                        view.ManualError = $"手动反馈读取失败。{ex.Message}";
                    }
                }
                await Task.Delay(station.PollMilliseconds, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void Archive(IStation station, TestSnapshot test)
    {
        if (test.CycleId == lastCompletedCycle)
            return;
        bool saved = store.Record(station.Source, test);
        lastCompletedCycle = test.CycleId;
        storageError = null;
        if (saved)
            NotifyArchived(station);
    }

    private bool UsesCompletionArchive(IStation station) => station is not ModbusStation;

    private void NotifyArchived(IStation station, string? notice = null)
    {
        view.Notice = notice ?? $"{(station is SimulationStation ? "模拟" : "PLC")}测试结果已保存。";
        if (currentPage == 3)
            TryRefreshHistory();
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (!view.NotBusy || closing || DataContext is null)
            return;
        ReleaseHeldManual();
        view.SetBusy(true);
        try { await action(); }
        catch (Exception ex)
        {
            view.Notice = ex.Message;
            MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { view.SetBusy(false); }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string parameter } || !int.TryParse(parameter, out int index))
            return;
        Navigate(index);
    }

    private void Navigate(int index)
    {
        currentPage = index;
        if (index != 1)
        {
            ReleaseHeldManual();
            view.UpdateManual(null);
        }
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in Navigation.Children.OfType<Button>())
            button.Tag = (string)button.CommandParameter == index.ToString(CultureInfo.InvariantCulture) ? "active" : null;
        if (index == 3)
        {
            TryRefreshHistory();
            if (HistoryAddressTab.IsSelected)
                RefreshHistoryPoints();
        }
    }

    // Only devices that have been configured are connected; PLC/camera keep retrying after link loss.
    private async Task AutoConnectDevicesAsync()
    {
        var peripherals = store!.LoadPeripherals();
        if (!string.IsNullOrWhiteSpace(peripherals.Camera.Host))
            _ = StationDevices.Camera.StartAutoConnectAsync(peripherals.Camera);
        // Scanner opens once at startup, only when the saved COM port currently exists.
        if (!string.IsNullOrWhiteSpace(peripherals.Scanner.Port) &&
            System.IO.Ports.SerialPort.GetPortNames().Contains(peripherals.Scanner.Port, StringComparer.OrdinalIgnoreCase))
            _ = StationDevices.Scanner.ConnectAsync(peripherals.Scanner).ContinueWith(
                task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);

        string configPath = Path.Combine(dataFolder, "plc.json");
        if (!File.Exists(configPath))
            return;
        try { await SwitchStationAsync(PlcSettings.Load(configPath)); }
        catch (Exception ex) { view.Notice = $"PLC 配置无效，保持模拟模式。{ex.Message}"; }
    }

    private async Task SwitchStationAsync(PlcSettings? settings)
    {
        if (!view.CanSwitchMode || closing)
            throw new InvalidOperationException("请等待当前操作或测试结束。");
        if (settings is null && view.IsSimulation)
            return;

        view.SetBusy(true);
        try
        {
            await FlushManualReleaseAsync();
            await StopPollingAsync();
            try
            {
                if (settings is not null)
                {
                    settings.Validate();
                    StationDevices.Plc.Configure(settings, monitorPoints, manualPoints, recipePoints,
                        inspectionPoints, barcodePoint, recordTrigger, scannerTrigger, timerTrigger);
                }
            }
            catch
            {
                StartPolling();
                throw;
            }

            CloseStationConnection();
            IStation next = settings is null ? new SimulationStation() : StationDevices.Plc;
            if (next is SimulationStation)
                await next.ApplyAsync(view.ActiveRecipe);
            lastCompletedCycle = 0;
            storageError = null;
            view.Use(next);
            view.Notice = view.IsSimulation ? "模拟模式已就绪。" : "正在连接 PLC。";
            StartPolling();
        }
        finally { view.SetBusy(false); }
    }

    private void CloseStationConnection()
    {
        if (view.Station is ModbusStation plc)
            plc.CloseConnection();
        else
            view.Station.Dispose();
    }

    private async void EditManualPoint_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditMonitorPoints || closing || heldManualButton is not null
            || sender is not Button { CommandParameter: ManualAction action })
            return;

        await PerformAsync(async () =>
        {
            await FlushManualReleaseAsync();
            var dialog = new PointAddressDialog(action) { Owner = this };
            var station = view.Station as ModbusStation;
            dialog.SetCommunicationInfo("线圈区 · FC01 读取，FC05 写入；M/Y 地址会解析为零基线圈地址。读取测试只读，不会写线圈。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    var point = dialog.ManualResult ?? throw new InvalidOperationException("请填写有效的写入地址和反馈地址。");
                    var write = await station.ReadTestAsync(PlcTestArea.Coils, point.WriteAddress, 1);
                    var feedback = await station.ReadTestAsync(PlcTestArea.Coils, point.FeedbackAddress, 1);
                    return $"只读成功：写入线圈 {PlcAddress.FormatCoil(point.WriteAddress)}={write[0]}；"
                        + $"反馈线圈 {PlcAddress.FormatCoil(point.FeedbackAddress)}={feedback[0]}。";
                });
            dialog.SetSaveAction(async () =>
            {
                if (dialog.ManualResult is not { } point)
                    throw new InvalidOperationException("请输入有效的写入地址和反馈地址。");
                if (point == action.Point)
                    return;
                if (view.Snapshot.Running)
                    throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改点位。");

                var updated = new Dictionary<ManualPointId, ManualPoint>(manualPoints) { [action.Id] = point };
                if (station is not null)
                    await station.UpdateManualPointsAsync(updated, () => store.SaveManualPoints(updated));
                else
                {
                    string configPath = Path.Combine(dataFolder, "plc.json");
                    var settings = File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings();
                    _ = InspectionPoints.Validate(settings, inspectionPoints, updated, recordTrigger);
                    store.SaveManualPoints(updated);
                }
                manualPoints = updated;
                view.UpdateManual(null);
                view.SetManualPoints(updated);
                view.Notice = $"{action.Label}地址已保存。";
            });
            dialog.ShowDialog();
        });
    }

    private void Manual_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button && CanStartManual(button))
        {
            button.Focus();
            if (button.CaptureMouse())
                BeginManual(button, Key.None);
        }
    }

    private void Manual_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender == heldManualButton && heldManualKey == Key.None)
            ReleaseHeldManual();
    }

    private void Manual_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender == heldManualButton && heldManualKey == Key.None)
            ReleaseHeldManual();
    }

    private void Manual_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
            return;
        e.Handled = true;
        if (!e.IsRepeat && sender is Button button && CanStartManual(button))
            BeginManual(button, e.Key);
    }

    private void Manual_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
            return;
        e.Handled = true;
        if (sender == heldManualButton && heldManualKey == e.Key)
            ReleaseHeldManual();
    }

    private void Manual_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == heldManualButton && heldManualKey != Key.None)
            ReleaseHeldManual();
    }

    private bool CanStartManual(Button button) => !closing && currentPage == 1 && view.CanUseManual
        && button.CommandParameter is ManualPointId && heldManualButton is null
        && manualRelease.IsCompleted && !manualReleasePending;

    private async void BeginManual(Button button, Key key)
    {
        var station = view.Station;
        var id = (ManualPointId)button.CommandParameter;
        heldManualButton = button;
        heldManualKey = key;
        try
        {
            manualPress = station.SetManualAsync(id, true);
            await manualPress;
        }
        catch (Exception ex)
        {
            view.Notice = $"手动命令写入失败。{ex.Message}";
            if (heldManualButton == button)
                ReleaseHeldManual();
        }
    }

    private void ReleaseHeldManual()
    {
        if (heldManualButton is not { } button)
            return;
        heldManualButton = null;
        if (button.IsMouseCaptured)
            button.ReleaseMouseCapture();
        manualRelease = ReleaseManualButtonAsync(view.Station, (ManualPointId)button.CommandParameter, manualPress);
    }

    private async Task ReleaseManualButtonAsync(IStation station, ManualPointId id, Task press)
    {
        try { await press; }
        catch { /* A failed response does not prove the PLC missed the write. */ }
        try { await station.SetManualAsync(id, false); }
        catch (Exception ex)
        {
            manualReleasePending = true;
            view.UpdateManual(null);
            view.Notice = $"手动命令未确认清零，通讯恢复后重试。{ex.Message}";
        }
    }

    private async Task FlushManualReleaseAsync()
    {
        ReleaseHeldManual();
        await manualRelease;
        await view.Station.ReleaseManualAsync();
        manualReleasePending = false;
    }

    private async void EditMonitorPoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: MonitorPointId id })
            await EditMonitorPointAsync(id);
    }

    private PlcSettings MonitorSettings()
    {
        string configPath = Path.Combine(dataFolder, "plc.json");
        return (view.Station as ModbusStation)?.Settings
            ?? (File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings());
    }

    private async Task EditMonitorPointAsync(MonitorPointId id, string? historyLabel = null)
    {
        if (!view.CanEditMonitorPoints || closing)
            return;
        await PerformAsync(async () =>
        {
            var station = view.Station as ModbusStation;
            var settings = MonitorSettings();
            var definition = MonitorPointDefinition.For(id);
            if (historyLabel is not null)
                definition = definition with { Label = historyLabel };
            var defaultPoint = definition.DefaultAt(settings.TelemetryAddress);
            bool hasOverride = monitorPoints.TryGetValue(id, out var custom);
            var current = custom ?? defaultPoint;
            var dialog = new PointAddressDialog(definition, current, defaultPoint, hasOverride) { Owner = this };
            dialog.SetCommunicationInfo($"{(settings.UseInputRegisters ? "输入寄存器 · FC04" : "保持寄存器 · FC03")}；地址为零基偏移，"
                + $"{settings.LowWordFirst switch { true => "低字在前", false => "高字在前" }}。读取测试为只读操作。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    var point = dialog.MonitorResult
                        ?? throw new InvalidOperationException("该点位为空，当前没有可读取的地址。");
                    var words = await station.ReadTestAsync(
                        settings.UseInputRegisters ? PlcTestArea.InputRegisters : PlcTestArea.HoldingRegisters,
                        point.Address, (ushort)point.WordCount);
                    double decoded = definition.Decode(point.DataType, words, settings.LowWordFirst);
                    return $"只读成功：原始值 [{string.Join(", ", words)}]（{string.Join(", ", words.Select(word => $"0x{word:X4}"))}）；"
                        + $"解析值 {decoded.ToString("0.####", CultureInfo.InvariantCulture)}"
                        + (definition.ScaledUnit is { } unit ? $" {unit}" : "")
                        + (id == MonitorPointId.RunState && decoded is >= 0 and <= ushort.MaxValue
                            ? $"（{PlcLayout.RunStateLabel((ushort)decoded)}）" : "") + "。";
                });
            dialog.SetSaveAction(async () =>
            {
                var point = dialog.MonitorResult;
                if (point == current)
                    return;
                var updated = new Dictionary<MonitorPointId, MonitorPoint>(monitorPoints);
                if (point is not null)
                    updated[id] = point;
                else
                    updated.Remove(id);

                if (station is not null)
                    await station.UpdateMonitorPointsAsync(updated, () => store.SaveMonitorPoints(updated));
                else
                {
                    var monitoring = new MonitorReadPlan(settings, updated, barcodePoint);
                    _ = new RecipeWritePlan(settings, recipePoints, monitoring);
                    store.SaveMonitorPoints(updated);
                }

                monitorPoints = updated;
                if (HistoryAddressTab.IsSelected)
                {
                    RefreshHistoryPoints();
                    HighlightHistoryPoint(definition.Label);
                }
                view.Notice = point is null ? $"{definition.Label}已取消采集。"
                    : $"{definition.Label}已保存 · {PlcAddress.FormatRegisterRange(point.Address, point.WordCount)}"
                        + $"（零基偏移 {FormatOffset(point.Address, point.WordCount)}）· {point.DataType}";
            });
            dialog.ShowDialog();
        });
    }

    private async void EditRecipePoint_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe || sender is not Button { CommandParameter: RecipePointId id })
            return;

        await PerformAsync(async () =>
        {
            var station = view.Station as ModbusStation;
            string configPath = Path.Combine(dataFolder, "plc.json");
            var settings = station?.Settings ?? (File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings());
            var definition = RecipePoint.All[id];
            ushort? defaultAddress = definition.DefaultAddress(settings);
            bool hasOverride = recipePoints.TryGetValue(id, out ushort existing);
            var dialog = new PointAddressDialog(definition, hasOverride ? existing : defaultAddress, defaultAddress) { Owner = this };
            dialog.SetCommunicationInfo($"保持寄存器 · FC03 只读测试 / FC16 配方写入；地址为零基偏移，"
                + $"{(settings.LowWordFirst ? "低字在前" : "高字在前")}。读取测试不会写入配方。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    ushort address = dialog.RecipeAddress ?? defaultAddress
                        ?? throw new InvalidOperationException("该配方点位没有有效地址。");
                    var words = await station.ReadTestAsync(PlcTestArea.HoldingRegisters, address, (ushort)definition.WordCount);
                    return $"只读成功：寄存器 {address}–{address + definition.WordCount - 1}；"
                        + $"原始值 [{string.Join(", ", words)}]（{string.Join(", ", words.Select(word => $"0x{word:X4}"))}）。";
                });
            dialog.SetSaveAction(async () =>
            {
                if (view.Snapshot.Running)
                    throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改地址。");

                var updated = new Dictionary<RecipePointId, ushort>(recipePoints);
                if (dialog.RecipeAddress is ushort address && address != defaultAddress)
                    updated[id] = address;
                else
                    updated.Remove(id);

                if (station is not null)
                    await station.UpdateRecipePointsAsync(updated, () => store.SaveRecipePoints(updated));
                else
                {
                    _ = new RecipeWritePlan(settings, updated, new MonitorReadPlan(settings, monitorPoints, barcodePoint));
                    store.SaveRecipePoints(updated);
                }
                recipePoints = updated;
                view.Notice = (dialog.RecipeAddress ?? defaultAddress) is ushort saved
                    ? $"{definition.Label}写入地址已设为 {saved}。" : $"{definition.Label}写入地址已清除。";
            });
            dialog.ShowDialog();
        });
    }

    private void StartDemo_Click(object sender, RoutedEventArgs e) => StartDemo();
    private void DemoBarcode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            StartDemo();
        }
    }

    private async void StartDemo()
    {
        if (!view.CanSimulate)
            return;
        await PerformAsync(async () =>
        {
            if (storageError is not null)
                throw new InvalidOperationException(storageError);
            var simulator = (SimulationStation)view.Station;
            simulator.Start(DemoBarcode.Text.Trim(), DemoFailure.IsChecked == true);
            DemoBarcode.Clear();
            var reading = await simulator.ReadAsync(CancellationToken.None);
            view.Update(reading.Test, reading.Feedback);
            view.Notice = "模拟测试运行中。";
        });
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (view.CanReset)
            await PerformAsync(async () =>
            {
                if (storageError is not null)
                    throw new InvalidOperationException(storageError);
                var reading = await view.Station.ReadAsync(CancellationToken.None);
                if (reading.Test.Stage == TestStage.Completed && UsesCompletionArchive(view.Station))
                    Archive(view.Station, reading.Test);
                await view.Station.ResetAsync();
                view.Notice = view.IsSimulation ? "模拟命令已执行。" : "PLC 已接收命令，请查看设备反馈。";
            });
    }

    private async void EditResetAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditMonitorPoints || closing)
            return;

        await PerformAsync(async () =>
        {
            var settings = MonitorSettings();
            ushort? defaultAddress = settings.Commands.TryGetValue("reset", out ushort address) ? address : null;
            var current = inspectionPoints.GetValueOrDefault("reset")?.WriteAddress;
            var dialog = new PointAddressDialog(current, defaultAddress) { Owner = this };
            var station = view.Station as ModbusStation;
            dialog.SetCommunicationInfo("线圈区 · FC01 只读测试 / FC05 复位写入；M 地址为零基线圈地址。读取测试只读，不会触发复位。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    ushort selected = dialog.ResetAddress ?? defaultAddress
                        ?? throw new InvalidOperationException("复位地址未配置。");
                    var values = await station.ReadTestAsync(PlcTestArea.Coils, selected, 1);
                    return $"只读成功：线圈 {PlcAddress.FormatCoil(selected)}={values[0]}。";
                });
            dialog.SetSaveAction(async () =>
            {
                if (view.Snapshot.Running)
                    throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改复位地址。");

                var selected = dialog.ResetAddress;
                var overrideAddress = selected == defaultAddress ? null : selected;
                if (overrideAddress == current)
                    return;

                var updated = new Dictionary<string, InspectionPoint>(inspectionPoints);
                if (overrideAddress is ushort custom)
                    updated["reset"] = new InspectionPoint(custom, null);
                else
                    updated.Remove("reset");

                if (station is not null)
                    await station.UpdateInspectionPointsAsync(updated, () => store.SaveInspectionPoints(updated));
                else
                {
                    _ = InspectionPoints.Validate(settings, updated, manualPoints, recordTrigger);
                    store.SaveInspectionPoints(updated);
                }
                inspectionPoints = updated;
                view.Notice = (overrideAddress ?? defaultAddress) is ushort saved
                    ? $"复位地址已设为 {PlcAddress.FormatCoil(saved)}。" : "复位地址已清除。";
            });
            dialog.ShowDialog();
        });
    }

    private async void InspectionSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: InspectionSwitch item } toggle)
            return;

        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        if (!item.Available || item.Feedback is not bool current)
            return;

        await PerformAsync(async () =>
        {
            await view.Station.SetInspectionSwitchAsync(item.Key, !current);
            if (view.IsSimulation)
            {
                var reading = await view.Station.ReadAsync(CancellationToken.None);
                view.Update(reading.Test, reading.Feedback);
            }
            view.Notice = view.IsSimulation ? $"{item.Label}：{item.State}。" : $"{item.Label}已写入，请查看 PLC 反馈。";
        });
    }

    private async void EditInspectionPoint_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditMonitorPoints || sender is not Button { CommandParameter: InspectionSwitch item })
            return;

        await PerformAsync(async () =>
        {
            var station = view.Station as ModbusStation;
            string configPath = Path.Combine(dataFolder, "plc.json");
            var settings = station?.Settings ?? (File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings());
            var current = InspectionPoints.Resolve(settings, inspectionPoints, item.Key);
            ushort? defaultWrite = settings.Commands.TryGetValue(item.Key, out ushort configuredWrite)
                ? configuredWrite : null;
            var dialog = new PointAddressDialog(item.Label, current.WriteAddress, current.FeedbackAddress,
                true, defaultWrite) { Owner = this };
            dialog.SetCommunicationInfo("线圈区 · FC01 只读测试 / FC05 开关写入；地址为零基线圈地址。读取测试不会改变线圈状态。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    var point = dialog.InspectionResult
                        ?? throw new InvalidOperationException("请输入有效的点位地址。");
                    var resolved = InspectionPoints.Resolve(settings,
                        new Dictionary<string, InspectionPoint> { [item.Key] = point }, item.Key);
                    var values = new List<string>();
                    if (resolved.WriteAddress is ushort writeAddress)
                    {
                        var read = await station.ReadTestAsync(PlcTestArea.Coils, writeAddress, 1);
                        values.Add($"写入线圈 {PlcAddress.FormatCoil(writeAddress)}={read[0]}");
                    }
                    if (point.FeedbackAddress is ushort feedbackAddress && feedbackAddress != resolved.WriteAddress)
                    {
                        var read = await station.ReadTestAsync(PlcTestArea.Coils, feedbackAddress, 1);
                        values.Add($"反馈线圈 {PlcAddress.FormatCoil(feedbackAddress)}={read[0]}");
                    }
                    if (values.Count == 0)
                        throw new InvalidOperationException("写入和反馈地址均未配置。");
                    return $"只读成功：{string.Join("；", values)}。";
                });
            dialog.SetSaveAction(async () =>
            {
                if (dialog.InspectionResult is not { } point || point == current)
                    return;
                if (view.Snapshot.Running)
                    throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改点位。");

                if (settings.Commands.TryGetValue(item.Key, out ushort defaultWrite) && point.WriteAddress == defaultWrite)
                    point = point with { WriteAddress = null };
                var updated = new Dictionary<string, InspectionPoint>(inspectionPoints);
                if (point is { WriteAddress: null, FeedbackAddress: null })
                    updated.Remove(item.Key);
                else
                    updated[item.Key] = point;

                if (station is not null)
                    await station.UpdateInspectionPointsAsync(updated, () => store.SaveInspectionPoints(updated));
                else
                {
                    _ = InspectionPoints.Validate(settings, updated, manualPoints, recordTrigger);
                    store.SaveInspectionPoints(updated);
                }
                inspectionPoints = updated;
                if (station is not null)
                {
                    item.Feedback = null;
                    item.Available = false;
                }
                view.Notice = $"{item.Label}地址已保存。";
            });
            dialog.ShowDialog();
        });
    }

    private void LoadRecipes(string? id)
    {
        selectingRecipe = true;
        try
        {
            view.Recipes = store.Recipes();
            var selected = view.Recipes.FirstOrDefault(item => item.Id == id) ?? view.Recipes[0];
            RecipePicker.SelectedValue = selected.Id;
            view.Draft = selected with { };
        }
        finally { selectingRecipe = false; }
    }

    private void RecipePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!selectingRecipe && RecipePicker.SelectedItem is Recipe selected)
            view.Draft = selected with { };
    }

    private async void NewRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe)
            return;

        await PerformAsync(() =>
        {
            var template = RecipePicker.SelectedItem as Recipe ?? view.ActiveRecipe;
            Recipe? created = null;
            var dialog = new NewRecipeDialog(template, recipe =>
            {
                if (view.Snapshot.Running)
                    throw new InvalidOperationException("测试进行中，请在测试结束后创建配方。");
                store.SaveRecipe(recipe, false);
                created = recipe;
            }) { Owner = this };
            if (dialog.ShowDialog() == true && created is not null)
            {
                LoadRecipes(created.Id);
                view.Notice = $"已创建配方“{created.Name}”。";
            }
            return Task.CompletedTask;
        });
    }

    private Recipe ValidDraft()
    {
        if (Validation.GetHasError(RecipeName) || RecipeFields.Children.OfType<TextBox>()
            .Concat(RecipeFields.Children.OfType<DockPanel>().SelectMany(panel => panel.Children.OfType<TextBox>()))
            .Any(Validation.GetHasError))
            throw new InvalidOperationException("请修正标红的参数输入。");
        var recipe = view.Draft with { };
        recipe.Validate();
        return recipe;
    }

    private async void SaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe)
            return;
        await PerformAsync(() =>
        {
            var recipe = ValidDraft();
            store.SaveRecipe(recipe, false);
            LoadRecipes(recipe.Id);
            view.Notice = "配方已保存。";
            return Task.CompletedTask;
        });
    }

    private async void ApplyRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanApplyRecipe)
            return;
        await PerformAsync(async () =>
        {
            var recipe = ValidDraft();
            if (!view.Recipes.Contains(recipe))
                throw new InvalidOperationException("请先保存配方，再启用或下发。");
            var station = view.Station;
            string answer;
            try { answer = await StationDevices.Camera.RequestAsync(recipe.CameraCommand, TimeSpan.FromSeconds(3)); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"相机命令“{recipe.CameraCommand}”未成功，配方未启用。{ex.Message}", ex);
            }
            if (answer.Equals("fail", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"相机切换配方失败（返回 fail），配方未启用。");
            if (!answer.Equals("OK", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"相机返回“{answer}”，无法确认切换结果，配方未启用。");
            if (view.IsSimulation)
            {
                store.SaveRecipe(recipe, true);
                await station.ApplyAsync(recipe);
                view.ActiveRecipe = recipe;
                view.Notice = $"相机已切换（{recipe.CameraCommand} → OK），并启用模拟配方“{recipe.Name}”。";
            }
            else
            {
                store.SaveRecipe(recipe, true);
                view.ActiveRecipe = recipe;
                view.Notice = $"相机已切换（{recipe.CameraCommand} → OK），正在下发“{recipe.Name}”。";
                view.SetSendingRecipe(true);
                recipeDispatch = DispatchRecipeAsync(station, recipe);
            }
        });
    }

    private async Task DispatchRecipeAsync(IStation station, Recipe recipe)
    {
        try
        {
            await station.ApplyAsync(recipe);
            view.Notice = $"配方“{recipe.Name}”已下发。";
        }
        catch (Exception ex)
        {
            view.Notice = $"配方“{recipe.Name}”下发失败。{ex.Message}";
        }
        finally { view.SetSendingRecipe(false); }
    }

    private async void DeleteRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe || RecipePicker.SelectedItem is not Recipe recipe)
            return;
        if (!Confirm($"删除配方“{recipe.Name}”？"))
            return;
        await PerformAsync(() =>
        {
            store.DeleteRecipe(recipe.Id);
            LoadRecipes(view.ActiveRecipe.Id);
            view.Notice = "配方已删除。";
            return Task.CompletedTask;
        });
    }

    private void DateValidation_Error(object sender, DatePickerDateValidationErrorEventArgs e) =>
        view.Notice = "日期格式无效，请使用日历选择日期。";

    private static DateTime? ReadDate(DatePicker picker)
    {
        if (string.IsNullOrWhiteSpace(picker.Text))
            return null;
        if (DateTime.TryParse(picker.Text, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.None, out var date))
            return date.Date;
        throw new InvalidOperationException("日期格式无效，请使用日历选择日期。");
    }

    private IEnumerable<TestRecord> HistoryResults() => store.History(ReadDate(DateFrom), ReadDate(DateTo), BarcodeFilter.Text);

    private void RefreshHistory() => view.Records = HistoryResults().ToList();

    private void TryRefreshHistory()
    {
        try { RefreshHistory(); }
        catch (Exception ex) { view.Notice = ex.Message; }
    }

    private void HistoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == sender && store is not null && HistoryAddressTab.IsSelected)
            RefreshHistoryPoints();
    }

    private void RefreshHistoryPoints()
    {
        try
        {
            var settings = MonitorSettings();
            HistoryRegisterArea.Text = settings.UseInputRegisters ? "输入寄存器 · FC04" : "保持寄存器 · FC03";
            HistoryArchiveWarning.Visibility = recordTrigger.HasValue ? Visibility.Collapsed : Visibility.Visible;
            ClearHistoryPointHighlight();
            var selectedPoint = HistoryAddressGrid.SelectedValue;
            var barcode = barcodePoint ?? BarcodePoint.DefaultAt(settings.TelemetryAddress);
            var barcodeRow = new HistoryPointRow(null, "01 · 控制与存档", "二维码",
                PlcAddress.FormatRegisterRange(barcode.Address, barcode.WordCount),
                FormatOffset(barcode.Address, barcode.WordCount), $"UTF-8 · {barcode.WordCount} 个寄存器",
                barcodePoint is null ? "默认" : "自定义", barcodePoint is null ? "Default" : "Custom");
            var triggerRow = new HistoryPointRow(null, "01 · 控制与存档", RecordTriggerLabel,
                recordTrigger is ushort trigger ? PlcAddress.FormatCoil(trigger) : "—",
                recordTrigger is ushort offset ? offset.ToString(CultureInfo.InvariantCulture) : "—",
                "线圈 · Bool · FC01/FC05", recordTrigger.HasValue ? "自定义" : "未配置",
                recordTrigger.HasValue ? "Custom" : "Missing");
            historyPointRows = HistoryFields.Select(field =>
            {
                var custom = monitorPoints.GetValueOrDefault(field.Id);
                var point = custom ?? MonitorPointDefinition.For(field.Id).DefaultAt(settings.TelemetryAddress);
                string group = field.Id switch
                {
                    MonitorPointId.Result => "04 · 判定结果",
                    MonitorPointId.DustCurrent or MonitorPointId.DustCurrentVerdict
                        or MonitorPointId.DustPower or MonitorPointId.DustPowerVerdict
                        or MonitorPointId.DustSpeed or MonitorPointId.DustSpeedVerdict => "03 · 灰尘测试",
                    _ => "02 · 正常测试"
                };
                return new HistoryPointRow(field.Id, group, field.Label,
                    point is MonitorPoint configured ? PlcAddress.FormatRegisterRange(configured.Address, configured.WordCount) : "—",
                    point is MonitorPoint configuredOffset ? FormatOffset(configuredOffset.Address, configuredOffset.WordCount) : "—",
                    point?.DataType.ToString() ?? "—",
                    custom is not null ? "自定义" : point is not null ? "默认" : "未配置",
                    custom is not null ? "Custom" : point is not null ? "Default" : "Missing");
            }).Prepend(barcodeRow).Prepend(triggerRow).ToArray();
            var collectionView = CollectionViewSource.GetDefaultView(historyPointRows);
            collectionView.GroupDescriptions.Clear();
            collectionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HistoryPointRow.Group)));
            HistoryAddressGrid.ItemsSource = collectionView;
            if (selectedPoint is not null)
                HistoryAddressGrid.SelectedValue = selectedPoint;
        }
        catch (Exception ex)
        {
            HistoryRegisterArea.Text = "PLC 配置读取失败";
            HistoryAddressGrid.ItemsSource = null;
            view.Notice = ex.Message;
        }
    }

    private static string FormatOffset(int address, int wordCount) =>
        wordCount == 1
            ? address.ToString(CultureInfo.InvariantCulture)
            : $"{address.ToString(CultureInfo.InvariantCulture)}–{(address + wordCount - 1).ToString(CultureInfo.InvariantCulture)}";

    private void HighlightHistoryPoint(string label)
    {
        var point = historyPointRows.FirstOrDefault(row => row.Label == label);
        if (point is null)
            return;

        HistoryAddressGrid.SelectedValue = label;
        HistoryAddressGrid.ScrollIntoView(point);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (HistoryAddressGrid.ItemContainerGenerator.ContainerFromItem(point) is not DataGridRow row)
                return;

            ClearHistoryPointHighlight();
            highlightedHistoryPointRow = row;
            row.Background = (Brush)FindResource("Selected");
            row.BorderBrush = (Brush)FindResource("Accent");
            row.BorderThickness = new Thickness(1, 0, 1, 1);
            historyPointHighlightTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1.6)
            };
            historyPointHighlightTimer.Tick += HistoryPointHighlightTimer_Tick;
            historyPointHighlightTimer.Start();
        }), DispatcherPriority.Loaded);
    }

    private void HistoryPointHighlightTimer_Tick(object? sender, EventArgs e) => ClearHistoryPointHighlight();

    private void ClearHistoryPointHighlight()
    {
        historyPointHighlightTimer?.Stop();
        historyPointHighlightTimer = null;
        if (highlightedHistoryPointRow is not null)
        {
            highlightedHistoryPointRow.ClearValue(DataGridRow.BackgroundProperty);
            highlightedHistoryPointRow.ClearValue(DataGridRow.BorderBrushProperty);
            highlightedHistoryPointRow.ClearValue(DataGridRow.BorderThicknessProperty);
            highlightedHistoryPointRow = null;
        }
    }

    private async void EditHistoryPoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HistoryPointRow row })
            return;
        if (row.Id is MonitorPointId id)
            await EditMonitorPointAsync(id, row.Label);
        else if (row.Label == RecordTriggerLabel)
            await EditRecordTriggerAsync();
        else
            await EditBarcodePointAsync();
    }

    private async Task EditRecordTriggerAsync()
    {
        if (!view.CanEditMonitorPoints || closing)
            return;
        await PerformAsync(async () =>
        {
            var dialog = new PointAddressDialog(recordTrigger) { Owner = this };
            var station = view.Station as ModbusStation;
            dialog.SetCommunicationInfo("线圈区 · FC01 只读测试 / FC05 记录触发写入；读取测试只读，不会触发存档。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    ushort selected = dialog.RecordTriggerAddress
                        ?? throw new InvalidOperationException("记录触发地址未配置。");
                    var values = await station.ReadTestAsync(PlcTestArea.Coils, selected, 1);
                    return $"只读成功：线圈 {PlcAddress.FormatCoil(selected)}={values[0]}。";
                });
            dialog.SetSaveAction(async () =>
            {
                if (dialog.RecordTriggerAddress == recordTrigger)
                    return;

                var updated = dialog.RecordTriggerAddress;
                if (station is not null)
                    await station.UpdateRecordTriggerAsync(updated, () => store.SaveRecordTrigger(updated));
                else
                {
                    _ = InspectionPoints.Validate(MonitorSettings(), inspectionPoints, manualPoints, updated);
                    store.SaveRecordTrigger(updated);
                }

                recordTrigger = updated;
                storageError = null;
                RefreshHistoryPoints();
                HighlightHistoryPoint(RecordTriggerLabel);
                view.Notice = updated is ushort address ? $"记录触发已设为 {PlcAddress.FormatCoil(address)}。"
                    : "记录触发已清除，PLC 测试结果不再存档。";
            });
            dialog.ShowDialog();
        });
    }

    private async void EditTimerTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditMonitorPoints || closing)
            return;
        await PerformAsync(async () =>
        {
            var dialog = PointAddressDialog.ForTimerTrigger(timerTrigger);
            dialog.Owner = this;
            var station = view.Station as ModbusStation;
            dialog.SetCommunicationInfo("线圈区 · FC01 只读；上位机只读取该线圈，不会写入。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    ushort selected = dialog.TimerTriggerAddress
                        ?? throw new InvalidOperationException("计时触发地址未配置。");
                    var values = await station.ReadTestAsync(PlcTestArea.Coils, selected, 1);
                    return $"只读成功：线圈 {PlcAddress.FormatCoil(selected)}={values[0]}。";
                });
            dialog.SetSaveAction(async () =>
            {
                if (dialog.TimerTriggerAddress == timerTrigger)
                    return;

                var updated = dialog.TimerTriggerAddress;
                if (station is not null)
                    await station.UpdateTimerTriggerAsync(updated, () => store.SaveTimerTrigger(updated));
                else
                    store.SaveTimerTrigger(updated);

                timerTrigger = updated;
                view.Notice = updated is ushort address ? $"计时触发已设为 {PlcAddress.FormatCoil(address)}。"
                    : "计时触发已清除，运行时间不再计时。";
            });
            dialog.ShowDialog();
        });
    }

    private async Task EditBarcodePointAsync()
    {
        if (!view.CanEditMonitorPoints || closing)
            return;
        await PerformAsync(async () =>
        {
            var settings = MonitorSettings();
            var defaultPoint = BarcodePoint.DefaultAt(settings.TelemetryAddress);
            var dialog = new PointAddressDialog(barcodePoint ?? defaultPoint, defaultPoint) { Owner = this };
            var station = view.Station as ModbusStation;
            dialog.SetCommunicationInfo($"{(settings.UseInputRegisters ? "输入寄存器 · FC04" : "保持寄存器 · FC03")}；"
                + "地址为零基偏移，高字节在前，读取测试仅执行只读采集。");
            if (station is not null)
                dialog.SetReadTest(async () =>
                {
                    var point = dialog.BarcodeResult ?? throw new InvalidOperationException("二维码点位无效。");
                    var words = await station.ReadTestAsync(
                        settings.UseInputRegisters ? PlcTestArea.InputRegisters : PlcTestArea.HoldingRegisters,
                        point.Address, point.WordCount);
                    string text = PlcLayout.ReadString(words);
                    return $"只读成功：寄存器 {point.Address}–{point.End - 1}；"
                        + $"原始值 [{string.Join(", ", words.Select(word => $"0x{word:X4}"))}]；解码“{text}”。";
                });
            dialog.SetSaveAction(async () =>
            {
                if (dialog.BarcodeResult is not { } point)
                    throw new InvalidOperationException("二维码点位无效。");
                var updated = point == defaultPoint ? null : point;
                if (updated == barcodePoint)
                    return;

                if (station is not null)
                    await station.UpdateBarcodePointAsync(updated, () => store.SaveBarcodePoint(updated));
                else
                {
                    _ = new RecipeWritePlan(settings, recipePoints, new MonitorReadPlan(settings, monitorPoints, updated));
                    store.SaveBarcodePoint(updated);
                }

                barcodePoint = updated;
                RefreshHistoryPoints();
                HighlightHistoryPoint("二维码");
                view.Notice = $"二维码点位已保存 · {PlcAddress.FormatRegisterRange(point.Address, point.WordCount)}"
                    + $"（零基偏移 {FormatOffset(point.Address, point.WordCount)}）· {point.WordCount} 个寄存器";
            });
            dialog.ShowDialog();
        });
    }

    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();
    private async void BarcodeFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await QueryAsync();
        }
    }

    private Task QueryAsync() => PerformAsync(() =>
    {
        RefreshHistory();
        view.Notice = "历史数据查询完成。";
        return Task.CompletedTask;
    });

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(async () =>
        {
            var records = HistoryResults();
            var save = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"灰尘负载测试记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                AddExtension = true
            };
            if (save.ShowDialog(this) != true)
                return;
            await Task.Run(() => StationStore.Export(save.FileName, records));
            view.Notice = $"已导出至 {save.FileName}";
        });
    }

    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanDelete || HistoryGrid.SelectedItem is not TestRecord record || !Confirm($"删除测试记录 #{record.Id}？"))
            return;
        await PerformAsync(() =>
        {
            store.DeleteRecord(record.Id);
            RefreshHistory();
            view.Notice = "测试记录已删除。";
            return Task.CompletedTask;
        });
    }

    private async void CopyPhone_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(() =>
        {
            Clipboard.SetText("18550969605");
            view.Notice = "联系电话已复制。";
            return Task.CompletedTask;
        });
    }

    private void Login_Click(object sender, RoutedEventArgs e) => Login();

    private void LoginField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.IsRepeat)
            return;
        e.Handled = true;
        Login();
    }

    private void Login()
    {
        if (!view.NotBusy || view.IsAdministrator || closing)
            return;
        string password = LoginPassword.Password;
        LoginPassword.Clear();
        if (!string.Equals(password, AdministratorPassword, StringComparison.Ordinal))
        {
            SetLoginError(password.Length == 0 ? "请输入密码。" : "密码错误。");
            LoginPassword.Focus();
            return;
        }
        SetLoginError("");
        view.IsAdministrator = true;
        view.Notice = "管理员已登录。";
    }

    private void SetLoginError(string message)
    {
        LoginError.Text = message;
        LoginError.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanLogout)
            return;
        view.IsAdministrator = false;
        LoginPassword.Clear();
        SetLoginError("");
        view.Notice = "已退出登录。";
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "灰尘负载测试台", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

}

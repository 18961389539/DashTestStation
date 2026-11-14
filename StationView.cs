using System.Windows.Media;

namespace DustLoadScada;

public sealed class StationView : Bindable
{
    public const string AdministratorName = "admin";
    private IStation station = new SimulationStation();
    private TestSnapshot snapshot = new();
    private bool online;
    private bool busy;
    private bool sendingRecipe;
    private bool isAdministrator;
    private string notice = "模拟模式已就绪。";
    private string connectionError = "";
    private Recipe activeRecipe = new();
    private Recipe draft = new();
    private List<Recipe> recipes = [];
    private List<TestRecord> records = [];
    private bool manualOnline;
    private bool manualBlockedByStage;
    private string manualError = "";
    private string runtimeLabel = "0:00:00";
    private string runtimeStatus = "未配置运行地址";
    private string? scannerBarcode;
    private bool scannerMismatch;
    private string scannerComparisonLabel = "等待 PLC 扫码触发";

    public StationView()
    {
        InspectSwitches = StationActions.Devices.Select((item, bit) => (item, bit))
            .Where(entry => entry.item.Group == "检测开关")
            .Select(entry => new InspectionSwitch(entry.item.Key, entry.item.Label, entry.bit)).ToArray();
        ManualActions = Enum.GetValues<ManualPointId>()
            .Where(id => id != ManualPointId.CameraRecipe && id != ManualPointId.Mark)
            .Select(id => new ManualAction(id)).ToArray();
        FixtureActions = ManualActions[..6];
        LoadActions = ManualActions[6..];
    }

    public ManualAction[] ManualActions { get; }
    public ManualAction[] FixtureActions { get; }
    public ManualAction[] LoadActions { get; }
    public InspectionSwitch[] InspectSwitches { get; }
    public bool ShowIndicatorOffCameraVerdict => InspectSwitches.First(item => item.Key == "cameraOff").Feedback == true;
    public bool ShowHeadlightIndicatorCameraVerdict => InspectSwitches.First(item => item.Key == "cameraOn").Feedback == true;
    public IStation Station => station;
    public TestSnapshot Snapshot => snapshot;
    public bool IsSimulation => station is SimulationStation;
    public string Notice { get => notice; set => Set(ref notice, value); }
    public bool NotBusy => !busy;
    public bool IsAdministrator { get => isAdministrator; set { if (Set(ref isAdministrator, value)) Refresh(); } }
    public Recipe ActiveRecipe { get => activeRecipe; set { if (Set(ref activeRecipe, value)) Refresh(); } }
    public Recipe Draft { get => draft; set => Set(ref draft, value); }
    public List<Recipe> Recipes { get => recipes; set => Set(ref recipes, value); }
    public List<TestRecord> Records
    {
        get => records;
        set
        {
            if (Set(ref records, value))
            {
                Changed(nameof(HistoryEmpty));
                Changed(nameof(HistoryCount));
            }
        }
    }

    public string ConnectionLabel => IsSimulation ? "模拟模式" : online ? "PLC 已连接" : "PLC 未连接";
    public Brush ConnectionColor => (Brush)App.Current.FindResource(IsSimulation ? "Accent" : online ? "Green" : "Red");
    public string StageLabel => !IsSimulation && !online ? "等待 PLC 数据"
        : snapshot.RunState is ushort code ? PlcLayout.RunStateLabel(code) : snapshot.Stage switch
    {
        TestStage.Normal => "正常负载测试中",
        TestStage.Dust => "灰尘负载测试中",
        TestStage.Completed => "测试完成",
        TestStage.Fault => "PLC 故障",
        _ => "待机 · 等待测试"
    };
    public string BarcodeLabel => !string.IsNullOrEmpty(scannerBarcode) ? scannerBarcode
        : string.IsNullOrEmpty(snapshot.Barcode) ? "—" : snapshot.Barcode;
    public string ModelLabel => activeRecipe.Model;
    public bool ModelMismatch => scannerMismatch;
    public Brush ModelColor => (Brush)App.Current.FindResource(scannerMismatch ? "Red" : "Text");
    public string ScannerComparisonLabel => scannerComparisonLabel;
    public bool CanSwitchMode => !busy && !sendingRecipe && !snapshot.Running;
    public bool CanReadPlcTest => station is ModbusStation && !busy;
    public bool CanWritePlcTest => station is ModbusStation &&
        CanSwitchMode && online && snapshot.Stage != TestStage.Fault;
    public bool CanSimulate => IsSimulation && CanSwitchMode;
    private bool CanOperate => !busy && online && !snapshot.Running;
    public bool CanReset => !busy && online && station.Supports("reset");
    public bool CanEditMonitorPoints => !busy && !sendingRecipe && IsAdministrator;
    public string MonitorPointEditHint => !IsAdministrator ? "仅管理员可编辑地址配置，请先登录管理员账号。"
        : busy ? "系统正在处理操作，暂时无法编辑地址。"
        : sendingRecipe ? "配方正在下发，暂时无法编辑地址。"
        : "管理员可编辑地址配置。";
    public bool CanUseManual => !busy && manualOnline && !manualBlockedByStage;
    public bool CanEditRecipe => !busy;
    public bool CanApplyRecipe => CanEditRecipe && !sendingRecipe && online && station.CanApplyRecipe && snapshot.Stage != TestStage.Fault;
    public bool CanDelete => !busy;
    public bool CanLogout => !busy && IsAdministrator;
    public string OperatorLabel => IsAdministrator ? $"{AdministratorName} · 管理员" : "未登录";
    public string ActiveRecipeLabel => activeRecipe.Name;
    public string ApplyLabel => IsSimulation ? "启用配方" : "启用并下发 PLC";
    public string RecipeHint => IsSimulation ? "模拟配方" : station.CanApplyRecipe ? "PLC 配方" : "本地配方 · 未配置 PLC 配方地址";
    public string ManualHint => IsSimulation ? "模拟设备 · 按下写 1，松开写 0" : !manualOnline ? "Y 输出反馈不可用" :
        manualBlockedByStage ? "PLC 正在测试或处于故障状态" : "按下写 1，松开写 0";
    public string ManualError { get => manualError; set => Set(ref manualError, value); }
    public bool HistoryEmpty => records.Count == 0;
    public string HistoryCount => $"共 {records.Count} 条记录";

    public void SetBusy(bool value)
    {
        busy = value;
        Refresh();
    }

    public string RuntimeLabel { get => runtimeLabel; private set => Set(ref runtimeLabel, value); }
    public string RuntimeStatus { get => runtimeStatus; private set => Set(ref runtimeStatus, value); }
    public bool CanEditSystemSettings => !busy;
    public bool CanCapture => !busy && !snapshot.Running;

    public void UpdateRuntime(TimeSpan total, string status)
    {
        RuntimeLabel = RuntimeCounter.Format(total);
        RuntimeStatus = status;
    }

    internal void UpdateScannerResult(ScannerResult result)
    {
        scannerBarcode = result.Barcode;
        scannerMismatch = result.VerdictCode != 1;
        scannerComparisonLabel = result.Comparison;
        Changed(nameof(BarcodeLabel));
        Changed(nameof(ModelMismatch));
        Changed(nameof(ModelColor));
        Changed(nameof(ScannerComparisonLabel));
    }

    public void UpdateScannerStatus(string status)
    {
        if (scannerComparisonLabel == status)
            return;
        scannerComparisonLabel = status;
        Changed(nameof(ScannerComparisonLabel));
    }

    public void SetSendingRecipe(bool value)
    {
        sendingRecipe = value;
        Changed(nameof(CanSwitchMode));
        Changed(nameof(CanApplyRecipe));
        Changed(nameof(CanEditMonitorPoints));
        Changed(nameof(MonitorPointEditHint));
        Changed(nameof(CanWritePlcTest));
    }

    public void Use(IStation next)
    {
        station = next;
        snapshot = new TestSnapshot();
        connectionError = "";
        online = IsSimulation;
        manualBlockedByStage = false;
        UpdateManual(null);
        ManualError = "";
        UpdateInspectionSwitches(0);
        Refresh();
    }

    public void Update(TestSnapshot test, ushort feedback, ushort unknownFeedback = 0)
    {
        if (connectionError.Length != 0)
            Notice = "PLC 通讯已恢复。";
        connectionError = "";
        bool permissionsChanged = !online || snapshot.Running != test.Running || snapshot.Stage != test.Stage;
        manualBlockedByStage = test.Running || test.Stage == TestStage.Fault;
        online = true;
        if (snapshot != test)
        {
            snapshot = test;
            Changed(nameof(Snapshot));
            Changed(nameof(StageLabel));
            Changed(nameof(BarcodeLabel));
        }
        UpdateInspectionSwitches(feedback, unknownFeedback);
        if (permissionsChanged)
            Refresh();
    }

    public void Offline(string error)
    {
        online = false;
        snapshot = new TestSnapshot();
        UpdateInspectionSwitches(0);
        Refresh();
        if (connectionError == error)
            return;
        connectionError = error;
        Notice = $"PLC 通讯异常，正在重连。{error}";
    }

    public void SetManualPoints(IReadOnlyDictionary<ManualPointId, ManualPoint> points)
    {
        foreach (var action in ManualActions)
            action.Point = points.GetValueOrDefault(action.Id) ?? ManualPoints.Default(action.Id);
    }

    public void UpdateManual(ushort? feedback)
    {
        foreach (var action in ManualActions)
            action.Feedback = feedback is ushort bits ? (bits & (1 << (int)action.Id)) != 0 : null;
        bool changed = manualOnline != feedback.HasValue;
        manualOnline = feedback.HasValue;
        RefreshManualAvailability();
        if (!changed)
            return;
        Changed(nameof(CanUseManual));
        Changed(nameof(ManualHint));
    }

    private void UpdateInspectionSwitches(ushort feedback, ushort unknownFeedback = 0)
    {
        foreach (var item in InspectSwitches)
        {
            int mask = 1 << item.FeedbackBit;
            item.Feedback = online && (unknownFeedback & mask) == 0 ? (feedback & mask) != 0 : null;
            RefreshInspectionAvailability(item);
        }
        Changed(nameof(ShowIndicatorOffCameraVerdict));
        Changed(nameof(ShowHeadlightIndicatorCameraVerdict));
    }

    private void Refresh()
    {
        foreach (var item in InspectSwitches)
            RefreshInspectionAvailability(item);
        RefreshManualAvailability();
        Changed("");
    }

    // 相机拍照按钮不依赖 PLC，其余手动输出随 PLC 可用性启停。
    private void RefreshManualAvailability()
    {
        bool manual = CanUseManual;
        bool capture = CanCapture;
        foreach (var action in ManualActions)
            action.Available = action.Id == ManualPointId.Photo ? capture : manual;
    }

    private void RefreshInspectionAvailability(InspectionSwitch item) =>
        item.Available = CanOperate && snapshot.Stage != TestStage.Fault && item.Feedback.HasValue && station.Supports(item.Key);
}

using System.Windows.Media;

namespace DustLoadScada;

public sealed class StationView : Bindable
{
    private IStation station = new SimulationStation();
    private TestSnapshot snapshot = new();
    private bool online;
    private bool busy;
    private bool sendingRecipe;
    private UserAccount? user;
    private string notice = "模拟模式已就绪。";
    private string connectionError = "";
    private Recipe activeRecipe = new();
    private Recipe draft = new();
    private List<Recipe> recipes = [];
    private List<TestRecord> records = [];
    private bool needsAccount;
    private bool manualOnline;
    private bool manualBlockedByStage;
    private string manualError = "";

    public StationView()
    {
        InspectSwitches = StationActions.Devices.Select((item, bit) => (item, bit))
            .Where(entry => entry.item.Group == "检测开关")
            .Select(entry => new InspectionSwitch(entry.item.Key, entry.item.Label, entry.bit)).ToArray();
        ManualActions = Enum.GetValues<ManualPointId>().Select(id => new ManualAction(id)).ToArray();
        FixtureActions = ManualActions[..5];
        LoadActions = ManualActions[5..10];
    }

    public ManualAction[] ManualActions { get; }
    public ManualAction[] FixtureActions { get; }
    public ManualAction[] LoadActions { get; }
    public InspectionSwitch[] InspectSwitches { get; }
    public IStation Station => station;
    public TestSnapshot Snapshot => snapshot;
    public bool IsSimulation => station is SimulationStation;
    public string Notice { get => notice; set => Set(ref notice, value); }
    public bool NotBusy => !busy;
    public bool NeedsAccount { get => needsAccount; set { if (Set(ref needsAccount, value)) Refresh(); } }
    public UserAccount? User { get => user; set { if (Set(ref user, value)) Refresh(); } }
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
    public string StageLabel => !IsSimulation && !online ? "等待 PLC 数据" : snapshot.Stage switch
    {
        TestStage.Normal => "正常负载测试中",
        TestStage.Dust => "灰尘负载测试中",
        TestStage.Completed => "测试完成",
        TestStage.Fault => "PLC 故障",
        _ => "待机 · 等待测试"
    };
    public string BarcodeLabel => string.IsNullOrEmpty(snapshot.Barcode) ? "—" : snapshot.Barcode;
    public string ModelLabel => string.IsNullOrEmpty(snapshot.Model) ? IsSimulation ? activeRecipe.Model : "—" : snapshot.Model;
    public bool CanSwitchMode => !busy && !sendingRecipe && !snapshot.Running;
    public bool CanReadPlcTest => station is ModbusStation && !busy;
    public bool CanWritePlcTest => station is ModbusStation plc && plc.Settings.EnableWrites &&
        CanSwitchMode && online && snapshot.Stage != TestStage.Fault && user?.Role == UserRole.Supervisor;
    public bool CanSimulate => IsSimulation && CanSwitchMode;
    private bool CanOperate => !busy && online && !snapshot.Running && (IsSimulation || user is not null);
    public bool CanReset => CanOperate && station.Supports("reset");
    private bool HasRecipeAccess => user?.Role == UserRole.Supervisor || (IsSimulation && needsAccount);
    public bool CanEditMonitorPoints => CanSwitchMode && HasRecipeAccess;
    public bool CanUseManual => !busy && manualOnline && !manualBlockedByStage
        && (IsSimulation || user is not null) && station.CanWriteManual;
    public bool CanEditRecipe => !busy && !snapshot.Running && HasRecipeAccess;
    public bool CanApplyRecipe => CanEditRecipe && !sendingRecipe && online && station.CanApplyRecipe && snapshot.Stage != TestStage.Fault;
    public bool CanDelete => !busy && user?.Role == UserRole.Supervisor;
    public bool CanManageUsers => CanDelete;
    public bool CanLogout => !busy && user is not null;
    public string OperatorLabel => user is null ? "未登录" : $"{user.Name} · {user.RoleText}";
    public string AccessHint => needsAccount ? "首次使用，请创建负责人账号。" : "负责人可管理用户、PLC 配方及历史记录。";
    public string LoginLabel => needsAccount ? "创建负责人" : user is null ? "登录" : "切换用户";
    public string ActiveRecipeLabel => activeRecipe.Name;
    public string ApplyLabel => IsSimulation ? "启用配方" : "启用并下发 PLC";
    public string RecipeHint => !HasRecipeAccess ? "请登录负责人账号编辑" :
        IsSimulation ? "模拟配方" : station.CanApplyRecipe ? "PLC 配方" : "本地配方 · PLC 写入未启用";
    public string ManualHint => IsSimulation ? "模拟设备 · 按下写 1，松开写 0" : !manualOnline ? "Y 输出反馈不可用" :
        manualBlockedByStage ? "PLC 正在测试或处于故障状态" :
        !station.CanWriteManual ? "手动写入未启用" : user is null ? "请先登录" : "按下写 1，松开写 0";
    public string ManualError { get => manualError; set => Set(ref manualError, value); }
    public bool HistoryEmpty => records.Count == 0;
    public string HistoryCount => $"共 {records.Count} 条记录";

    public void SetBusy(bool value)
    {
        busy = value;
        Refresh();
    }

    public void SetSendingRecipe(bool value)
    {
        sendingRecipe = value;
        Changed(nameof(CanSwitchMode));
        Changed(nameof(CanApplyRecipe));
        Changed(nameof(CanEditMonitorPoints));
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
            Changed(nameof(ModelLabel));
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
        if (manualOnline == feedback.HasValue)
            return;
        manualOnline = feedback.HasValue;
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
    }

    private void Refresh()
    {
        foreach (var item in InspectSwitches)
            RefreshInspectionAvailability(item);
        Changed("");
    }

    private void RefreshInspectionAvailability(InspectionSwitch item) =>
        item.Available = CanOperate && snapshot.Stage != TestStage.Fault && item.Feedback.HasValue && station.Supports(item.Key);
}

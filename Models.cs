using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DustLoadScada;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        Changed(property);
        return true;
    }
}

public enum TestStage { Idle, Normal, Dust, Completed, Fault }
public enum Verdict { Pending, Pass, Fail }
public enum CameraRecipeId { HeadlightAndIndicator = 1, Headlight = 2, Indicator = 3 }

public sealed record Recipe
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "标准测试";
    public string Model { get; set; } = "DEMO-A01";
    public CameraRecipeId CameraRecipe { get; set; } = CameraRecipeId.HeadlightAndIndicator;
    [System.Text.Json.Serialization.JsonIgnore]
    public string CameraCommand => CameraRecipe switch
    {
        CameraRecipeId.HeadlightAndIndicator => "switch 1",
        CameraRecipeId.Headlight => "switch 2",
        CameraRecipeId.Indicator => "switch 3",
        _ => throw new InvalidOperationException("请选择有效的相机配方。")
    };
    public double VoltageMin { get; set; } = 21;
    public double VoltageMax { get; set; } = 28;
    public double PowerDeltaMin { get; set; } = 0;
    public double PowerDeltaMax { get; set; } = 30;
    public double SpeedDeltaMin { get; set; } = 0;
    public double SpeedDeltaMax { get; set; } = 500;
    public int BarcodeStartIndex { get; set; } = 1;
    public int BarcodeCompareLength { get; set; }
    public double NormalSeconds { get; set; } = 3;
    public double DustSeconds { get; set; } = 4;
    public double PowerActionSeconds { get; set; } = 0.5;
    public double GearActionSeconds { get; set; } = 0.5;
    public double PowerIntervalSeconds { get; set; } = 1;
    public double GearIntervalSeconds { get; set; } = 1;
    public int PowerCycles { get; set; } = 1;
    public int GearCycles { get; set; } = 1;
    public double AlarmSeconds { get; set; } = 2;
    public double IndicatorOffSeconds { get; set; } = 2;

    public void Validate()
    {
        Name = Name.Trim();
        Model = Model.Trim();
        if (!Enum.IsDefined(CameraRecipe))
            throw new InvalidOperationException("请选择有效的相机配方。");
        if (Name.Length is < 1 or > 80 || Model.Length is < 1 or > 80)
            throw new InvalidOperationException("配方名称和产品型号须为 1–80 个字符。");

        ValidateRange(VoltageMin, VoltageMax, "电压");
        ValidateRange(PowerDeltaMin, PowerDeltaMax, "功率差");
        ValidateRange(SpeedDeltaMin, SpeedDeltaMax, "转速差");
        if (new[] { SpeedDeltaMin, SpeedDeltaMax }.Any(value =>
                !double.IsFinite(value) || value != Math.Truncate(value)))
            throw new InvalidOperationException("转速差上下限须为整数。");
        if (BarcodeStartIndex is < 1 or > 512 || BarcodeCompareLength is < 0 or > 512)
            throw new InvalidOperationException("开始序号须在 1–512 之间，对比长度须在 0–512 之间。");

        if (new[] { NormalSeconds, DustSeconds }.Any(value =>
                !double.IsFinite(value) || value <= 0 || value > 3600 || value != Math.Truncate(value)))
            throw new InvalidOperationException("正常及灰尘测试时间须为 1–3600 秒的整数。");

        if (new[] { PowerActionSeconds, GearActionSeconds, PowerIntervalSeconds, GearIntervalSeconds,
                    AlarmSeconds, IndicatorOffSeconds }.Any(value => !double.IsFinite(value) || value < 0 || value > 3600))
            throw new InvalidOperationException("动作、间隔、报警和跑马灯时间须在 0–3600 秒之间。");

        if (PowerCycles is < 0 or > 1000 || GearCycles is < 0 or > 1000)
            throw new InvalidOperationException("开机及档位次数须在 0–1000 之间。");
    }

    private static void ValidateRange(double min, double max, string field)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min > max)
            throw new InvalidOperationException($"{field}上下限无效，下限须小于或等于上限。");
    }
}

public sealed record TestSnapshot
{
    public TestStage Stage { get; init; }
    /// <summary>PLC 原始运行状态码；模拟站为空。</summary>
    public ushort? RunState { get; init; }
    public uint CycleId { get; init; }
    public double Elapsed { get; init; }
    public string Barcode { get; init; } = "";
    public string Model { get; init; } = "";
    public double? Voltage { get; init; }
    public double? NormalVoltage { get; init; }
    public double? NormalCurrent { get; init; }
    public double? DustCurrent { get; init; }
    public double? NormalPower { get; init; }
    public double? DustPower { get; init; }
    public double? PowerDelta { get; init; }
    public double? NormalSpeed { get; init; }
    public double? DustSpeed { get; init; }
    public double? SpeedDelta { get; init; }
    public Verdict PowerVerdict { get; init; }
    public Verdict SpeedVerdict { get; init; }
    public Verdict DirectionVerdict { get; init; }
    public Verdict BrushLightVerdict { get; init; }
    public Verdict? BrushLightOffVerdict { get; init; }
    public Verdict? NormalCurrentVerdict { get; init; }
    public Verdict? NormalPowerVerdict { get; init; }
    public Verdict? NormalSpeedVerdict { get; init; }
    public Verdict? DustCurrentVerdict { get; init; }
    public Verdict? DustPowerVerdict { get; init; }
    public Verdict? DustSpeedVerdict { get; init; }
    public Verdict Result { get; init; }
    public bool Running => Stage is TestStage.Normal or TestStage.Dust;
}

public sealed record TestRecord(long Id, string Source, DateTimeOffset Time, TestSnapshot Test)
{
    public string DisplayTime => Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SourceText => Source == "simulation" ? "模拟" : Source[4..];
}

public sealed class InspectionSwitch(string key, string label, int feedbackBit) : Bindable
{
    private bool? feedback;
    private bool available;

    public string Key { get; } = key;
    public string Label { get; } = label;
    public int FeedbackBit { get; } = feedbackBit;
    public bool? Feedback { get => feedback; set { if (Set(ref feedback, value)) Changed(nameof(State)); } }
    public string State => Feedback switch { true => "开", false => "关", _ => "未知" };
    public bool Available { get => available; set => Set(ref available, value); }
}

public readonly record struct StationReading(TestSnapshot Test, ushort Feedback, ushort UnknownFeedback = 0);

public sealed class ManualAction(ManualPointId id) : Bindable
{
    private ManualPoint point = ManualPoints.Default(id);
    private bool? feedback;
    public ManualPointId Id { get; } = id;
    public string Label => ManualPoints.Labels[(int)Id];
    public string Addresses => $"{PlcAddress.FormatCoil(point.WriteAddress)} → {PlcAddress.FormatCoil(point.FeedbackAddress)}";
    public bool? Feedback { get => feedback; set { if (Set(ref feedback, value)) Changed(nameof(State)); } }
    public string State => Feedback switch { true => "输出 ON", false => "输出 OFF", _ => "反馈未知" };
    public ManualPoint Point { get => point; set { if (Set(ref point, value)) Changed(nameof(Addresses)); } }
}

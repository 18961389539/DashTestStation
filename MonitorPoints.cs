using System.IO;

namespace DustLoadScada;

public enum MonitorPointId
{
    NormalPower, DustPower, PowerDelta, PowerVerdict,
    NormalSpeed, DustSpeed, SpeedDelta, SpeedVerdict, DirectionVerdict,
    Voltage, Result, BrushLightVerdict,
    NormalVoltage, NormalCurrent, NormalCurrentVerdict, NormalPowerVerdict, NormalSpeedVerdict,
    BrushLightOffVerdict, DustCurrent, DustCurrentVerdict, DustPowerVerdict, DustSpeedVerdict,
    IndicatorOffCameraVerdict, HeadlightIndicatorCameraVerdict,
    RunState,
    RealtimeCurrent, RealtimePower, DustVoltage
}

public enum RegisterDataType { Int16, UInt16, Int32, UInt32, Float32 }

public sealed record MonitorPoint(ushort Address, RegisterDataType DataType, double? Scale = null)
{
    internal int WordCount => DataType is RegisterDataType.Int16 or RegisterDataType.UInt16 ? 1 : 2;
    internal int End => Address + WordCount;
}

internal sealed record BarcodePoint(ushort Address, ushort WordCount)
{
    public const ushort MaxWords = 125;
    internal int End => Address + WordCount;

    public static BarcodePoint DefaultAt(ushort baseAddress) => new(checked((ushort)(baseAddress + 26)), 16);

    public void Validate()
    {
        if (WordCount is < 1 or > MaxWords || End > 65536)
            throw new InvalidOperationException($"二维码须占用 1–{MaxWords} 个连续寄存器，且不能超出地址范围。");
    }
}

internal sealed record MonitorPointDefinition(
    string Label, int? Offset, RegisterDataType DefaultType, string? ScaledUnit = null, ushort? Absolute = null)
{
    private static readonly Dictionary<MonitorPointId, MonitorPointDefinition> Definitions = new()
    {
        [MonitorPointId.NormalPower] = new("正常功率", 8, RegisterDataType.Int32, "W"),
        [MonitorPointId.DustPower] = new("灰尘功率", 10, RegisterDataType.Int32, "W"),
        [MonitorPointId.PowerDelta] = new("功率差值", 12, RegisterDataType.Int32, "W"),
        [MonitorPointId.PowerVerdict] = new("功率判定", 20, RegisterDataType.UInt16),
        [MonitorPointId.NormalSpeed] = new("正常转速", 14, RegisterDataType.Int32),
        [MonitorPointId.DustSpeed] = new("灰尘转速", 16, RegisterDataType.Int32),
        [MonitorPointId.SpeedDelta] = new("转速差值", 18, RegisterDataType.Int32),
        [MonitorPointId.SpeedVerdict] = new("转速判定", 21, RegisterDataType.UInt16),
        [MonitorPointId.DirectionVerdict] = new("转速方向判定", 23, RegisterDataType.UInt16),
        [MonitorPointId.Voltage] = new("实时电压", 6, RegisterDataType.Int32, "V"),
        [MonitorPointId.Result] = new("总判定", 22, RegisterDataType.UInt16),
        [MonitorPointId.BrushLightVerdict] = new("地刷灯亮判定", 25, RegisterDataType.UInt16),
        [MonitorPointId.NormalVoltage] = new("正常电压", null, RegisterDataType.Float32, "V"),
        [MonitorPointId.NormalCurrent] = new("正常电流", null, RegisterDataType.Float32, "A"),
        [MonitorPointId.NormalCurrentVerdict] = new("正常电流判定结果", null, RegisterDataType.UInt16),
        [MonitorPointId.NormalPowerVerdict] = new("正常功率判定结果", null, RegisterDataType.UInt16),
        [MonitorPointId.NormalSpeedVerdict] = new("正常转速判定结果", null, RegisterDataType.UInt16),
        [MonitorPointId.BrushLightOffVerdict] = new("地刷灯灭判定", null, RegisterDataType.UInt16),
        [MonitorPointId.DustCurrent] = new("灰尘测试电流", null, RegisterDataType.Float32, "A"),
        [MonitorPointId.DustCurrentVerdict] = new("灰尘电流判定结果", null, RegisterDataType.UInt16),
        [MonitorPointId.DustPowerVerdict] = new("灰尘功率判定结果", null, RegisterDataType.UInt16),
        [MonitorPointId.DustSpeedVerdict] = new("灰尘转速判定结果", null, RegisterDataType.UInt16),
        [MonitorPointId.IndicatorOffCameraVerdict] = new("跑马灯灭相机判定", null, RegisterDataType.UInt16, Absolute: PlcAddress.HMBase + 2),
        [MonitorPointId.HeadlightIndicatorCameraVerdict] = new("头灯跑马灯相机判定", null, RegisterDataType.UInt16, Absolute: PlcAddress.HMBase + 1),
        [MonitorPointId.RunState] = new("运行状态", 0, RegisterDataType.Int16),
        [MonitorPointId.RealtimeCurrent] = new("实时电流", null, RegisterDataType.Float32, "A", Absolute: 302),
        [MonitorPointId.RealtimePower] = new("实时功率", null, RegisterDataType.Float32, "W", Absolute: 304),
        [MonitorPointId.DustVoltage] = new("灰尘感应电压", null, RegisterDataType.Float32, "V", Absolute: 402)
    };

    public static MonitorPointDefinition For(MonitorPointId id) => Definitions[id];

    public MonitorPoint? DefaultAt(ushort baseAddress) => Absolute is ushort absolute
        ? new(absolute, DefaultType)
        : Offset is int offset ? new(checked((ushort)(baseAddress + offset)), DefaultType) : null;

    public double DefaultScale(RegisterDataType type) =>
        ScaledUnit is not null && type != RegisterDataType.Float32 ? 0.01 : 1;

    public double ScaleFor(MonitorPoint point) => point.Scale ?? DefaultScale(point.DataType);

    public double Decode(RegisterDataType type, ReadOnlySpan<ushort> words, bool lowWordFirst, double? scale = null)
    {
        uint bits = words[0];
        if (words.Length == 2)
            bits = lowWordFirst ? ((uint)words[1] << 16) | words[0] : ((uint)words[0] << 16) | words[1];

        double raw = type switch
        {
            RegisterDataType.Int16 => unchecked((short)bits),
            RegisterDataType.UInt16 => bits,
            RegisterDataType.Int32 => unchecked((int)bits),
            RegisterDataType.UInt32 => bits,
            RegisterDataType.Float32 => BitConverter.UInt32BitsToSingle(bits),
            _ => throw new InvalidDataException($"{Label}的数据类型无效。")
        };
        if (!double.IsFinite(raw))
            throw new InvalidDataException($"{Label}返回了无效的浮点数。");
        return raw * (scale ?? DefaultScale(type));
    }
}

internal sealed class MonitorReadPlan
{
    private readonly PlcSettings settings;
    private readonly Dictionary<MonitorPointId, MonitorPoint> points = new();
    private readonly BarcodePoint? barcodeOverride;
    private readonly BarcodePoint barcode;
    private readonly List<(int Start, int End)> additionalBlocks;

    public MonitorReadPlan(PlcSettings settings, IReadOnlyDictionary<MonitorPointId, MonitorPoint> overrides,
        BarcodePoint? barcodeOverride = null)
    {
        this.settings = settings;
        this.barcodeOverride = barcodeOverride;
        barcode = barcodeOverride ?? BarcodePoint.DefaultAt(settings.TelemetryAddress);
        barcode.Validate();
        foreach (var id in Enum.GetValues<MonitorPointId>())
        {
            var definition = MonitorPointDefinition.For(id);
            var point = overrides.TryGetValue(id, out var configured) ? configured : definition.DefaultAt(settings.TelemetryAddress);
            if (point is null)
                continue;
            if (!Enum.IsDefined(point.DataType) || point.End > 65536)
                throw new InvalidOperationException($"{definition.Label}的数据类型或寄存器地址超出范围。");

            // 同页查重：监控点之间、监控点与二维码之间不得共用寄存器；跨页面（配方/扫码写回等）允许重复。
            if (Overlaps(point.Address, point.End, barcode.Address, barcode.End))
                throw new InvalidOperationException($"{definition.Label}的地址与二维码点位重叠。");
            foreach (var (otherId, other) in points)
                if (Overlaps(point.Address, point.End, other.Address, other.End))
                    throw new InvalidOperationException($"{definition.Label}的地址与监控点{MonitorPointDefinition.For(otherId).Label}重叠。");

            points.Add(id, point);
        }

        additionalBlocks = MergeAdditionalBlocks(points.Values);
    }

    private static bool Overlaps(int start, int end, int otherStart, int otherEnd) =>
        start < otherEnd && otherStart < end;

    public MonitorReadPlan WithPoints(IReadOnlyDictionary<MonitorPointId, MonitorPoint> overrides) =>
        new(settings, overrides, barcodeOverride);

    public MonitorReadPlan WithBarcode(BarcodePoint? point) => new(settings, points, point);

    private List<(int Start, int End)> MergeAdditionalBlocks(IEnumerable<MonitorPoint> candidates)
    {
        var blocks = new List<(int Start, int End)>();
        foreach (var point in candidates.Where(point => !InTelemetry(point.Address, point.End)).OrderBy(point => point.Address))
        {
            if (blocks.Count > 0 && point.Address <= blocks[^1].End)
            {
                var previous = blocks[^1];
                blocks[^1] = (previous.Start, Math.Max(previous.End, point.End));
            }
            else
                blocks.Add((point.Address, point.End));
        }
        return blocks;
    }

    private bool InTelemetry(int start, int end) => start >= settings.TelemetryAddress
        && end <= settings.TelemetryAddress + PlcLayout.TelemetryWords;

    public (TestSnapshot Test, ushort Feedback) Read(Func<ushort, ushort, ushort[]> read, bool triggered = false)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var telemetry = read(settings.TelemetryAddress, PlcLayout.TelemetryWords);
            ushort runState = ReadRunState(read, telemetry);
            var extra = additionalBlocks.Select(block =>
                read((ushort)block.Start, (ushort)(block.End - block.Start))).ToArray();
            bool barcodeInTelemetry = InTelemetry(barcode.Address, barcode.End);
            var barcodeWords = barcodeInTelemetry
                ? telemetry.AsSpan(barcode.Address - settings.TelemetryAddress, barcode.WordCount)
                : read(barcode.Address, barcode.WordCount).AsSpan();

            if (extra.Length > 0 || !barcodeInTelemetry)
            {
                var latest = read(settings.TelemetryAddress, PlcLayout.TelemetryWords);
                if (!telemetry.AsSpan().SequenceEqual(latest) || runState != ReadRunState(read, latest))
                    continue;
            }

            double? Value(MonitorPointId id)
            {
                var definition = MonitorPointDefinition.For(id);
                if (!points.TryGetValue(id, out var point))
                    return null;
                if (InTelemetry(point.Address, point.End))
                    return definition.Decode(point.DataType,
                        telemetry.AsSpan(point.Address - settings.TelemetryAddress, point.WordCount), settings.LowWordFirst,
                        definition.ScaleFor(point));

                int block = additionalBlocks.FindIndex(block => point.Address >= block.Start && point.End <= block.End);
                return definition.Decode(point.DataType,
                    extra[block].AsSpan(point.Address - additionalBlocks[block].Start, point.WordCount), settings.LowWordFirst,
                    definition.ScaleFor(point));
            }

            return PlcLayout.Decode(telemetry, settings.LowWordFirst, runState, Value,
                PlcLayout.ReadString(barcodeWords), triggered);
        }

        throw new InvalidDataException("读取测试点位期间 PLC 状态发生变化，请等待下一次轮询。");
    }

    public TestStage ReadStage(Func<ushort, ushort, ushort[]> read)
    {
        var point = RunStatePoint;
        return PlcLayout.StageOf(DecodeRunState(point, read(point.Address, (ushort)point.WordCount)));
    }

    private MonitorPoint RunStatePoint => points.TryGetValue(MonitorPointId.RunState, out var point)
        ? point : throw new InvalidOperationException("未配置运行状态点位。");

    private ushort ReadRunState(Func<ushort, ushort, ushort[]> read, ushort[] telemetry)
    {
        var point = RunStatePoint;
        return DecodeRunState(point, InTelemetry(point.Address, point.End)
            ? telemetry.AsSpan(point.Address - settings.TelemetryAddress, point.WordCount)
            : read(point.Address, (ushort)point.WordCount));
    }

    private ushort DecodeRunState(MonitorPoint point, ReadOnlySpan<ushort> words)
    {
        double value = MonitorPointDefinition.For(MonitorPointId.RunState).Decode(point.DataType, words, settings.LowWordFirst);
        return value is >= 0 and <= ushort.MaxValue && value == Math.Floor(value) ? (ushort)value : ushort.MaxValue;
    }
}

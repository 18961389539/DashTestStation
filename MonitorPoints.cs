using System.IO;

namespace DustLoadScada;

public enum MonitorPointId
{
    NormalPower, DustPower, PowerDelta, PowerVerdict,
    NormalSpeed, DustSpeed, SpeedDelta, SpeedVerdict, DirectionVerdict,
    Voltage, Result
}

public enum RegisterDataType { Int16, UInt16, Int32, UInt32, Float32 }

public sealed record MonitorPoint(ushort Address, RegisterDataType DataType)
{
    internal int WordCount => DataType is RegisterDataType.Int16 or RegisterDataType.UInt16 ? 1 : 2;
    internal int End => Address + WordCount;
}

internal sealed record MonitorPointDefinition(
    string Label, int Offset, RegisterDataType DefaultType, string? ScaledUnit = null)
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
        [MonitorPointId.Result] = new("总判定", 22, RegisterDataType.UInt16)
    };

    public static MonitorPointDefinition For(MonitorPointId id) => Definitions[id];

    public MonitorPoint DefaultAt(ushort baseAddress) => new(checked((ushort)(baseAddress + Offset)), DefaultType);

    public double Decode(RegisterDataType type, ReadOnlySpan<ushort> words, bool lowWordFirst)
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
        return ScaledUnit is not null && type != RegisterDataType.Float32 ? raw / 100 : raw;
    }
}

internal sealed class MonitorReadPlan
{
    private readonly PlcSettings settings;
    private readonly Dictionary<MonitorPointId, MonitorPoint> points = new();
    private readonly List<(int Start, int End)> additionalBlocks = [];

    public MonitorReadPlan(PlcSettings settings, IReadOnlyDictionary<MonitorPointId, MonitorPoint> overrides)
    {
        this.settings = settings;
        foreach (var id in Enum.GetValues<MonitorPointId>())
        {
            var definition = MonitorPointDefinition.For(id);
            var point = overrides.TryGetValue(id, out var configured) ? configured : definition.DefaultAt(settings.TelemetryAddress);
            if (point is null || !Enum.IsDefined(point.DataType) || point.End > 65536)
                throw new InvalidOperationException($"{definition.Label}的数据类型或寄存器地址超出范围。");

            if (!settings.UseInputRegisters && settings.RecipeAddress is ushort recipeAddress
                && point.Address < recipeAddress + PlcLayout.RecipeWords && recipeAddress < point.End)
                throw new InvalidOperationException($"{definition.Label}的地址与配方保持寄存器重叠。");

            points.Add(id, point);
        }

        foreach (var point in points.Values.Where(point => !InTelemetry(point)).OrderBy(point => point.Address))
        {
            if (additionalBlocks.Count > 0 && point.Address <= additionalBlocks[^1].End)
            {
                var previous = additionalBlocks[^1];
                additionalBlocks[^1] = (previous.Start, Math.Max(previous.End, point.End));
            }
            else
                additionalBlocks.Add((point.Address, point.End));
        }
    }

    private bool InTelemetry(MonitorPoint point) => point.Address >= settings.TelemetryAddress
        && point.End <= settings.TelemetryAddress + PlcLayout.TelemetryWords;

    public bool OverlapsHoldingRegisters(int start, int end) => !settings.UseInputRegisters &&
        (start < settings.TelemetryAddress + PlcLayout.TelemetryWords && settings.TelemetryAddress < end
            || points.Values.Any(point => start < point.End && point.Address < end));

    public (TestSnapshot Test, ushort Feedback) Read(Func<ushort, ushort, ushort[]> read)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var telemetry = read(settings.TelemetryAddress, PlcLayout.TelemetryWords);
            var identity = PlcLayout.ReadIdentity(telemetry, settings.LowWordFirst);
            var extra = additionalBlocks.Select(block =>
                read((ushort)block.Start, (ushort)(block.End - block.Start))).ToArray();

            if (extra.Length > 0)
            {
                var latest = read(settings.TelemetryAddress, PlcLayout.TelemetryWords);
                if (identity != PlcLayout.ReadIdentity(latest, settings.LowWordFirst))
                    continue;
            }

            double Value(MonitorPointId id)
            {
                var point = points[id];
                var definition = MonitorPointDefinition.For(id);
                if (InTelemetry(point))
                    return definition.Decode(point.DataType,
                        telemetry.AsSpan(point.Address - settings.TelemetryAddress, point.WordCount), settings.LowWordFirst);

                int block = additionalBlocks.FindIndex(block => point.Address >= block.Start && point.End <= block.End);
                return definition.Decode(point.DataType,
                    extra[block].AsSpan(point.Address - additionalBlocks[block].Start, point.WordCount), settings.LowWordFirst);
            }

            return PlcLayout.Decode(telemetry, settings.LowWordFirst, Value);
        }

        throw new InvalidDataException("读取测试点位期间 PLC 状态发生变化，请等待下一次轮询。");
    }
}

namespace DustLoadScada;

public sealed record InspectionPoint(ushort? WriteAddress, ushort? FeedbackAddress);

internal static class InspectionPoints
{
    public static InspectionPoint Resolve(PlcSettings settings, IReadOnlyDictionary<string, InspectionPoint> overrides, string key)
    {
        var point = overrides.GetValueOrDefault(key);
        ushort? write = settings.Commands.TryGetValue(key, out ushort configured) ? configured : null;
        return new InspectionPoint(point?.WriteAddress ?? write, point?.FeedbackAddress);
    }

    public static Dictionary<ManualPointId, ManualPoint> Validate(PlcSettings settings,
        IReadOnlyDictionary<string, InspectionPoint> overrides, IReadOnlyDictionary<ManualPointId, ManualPoint>? manual,
        ushort? recordTrigger = null)
    {
        var switches = StationActions.Devices.Where(item => StationActions.IsInspectionSwitch(item.Key))
            .Select(item => Resolve(settings, overrides, item.Key)).ToArray();
        var writes = switches.Where(point => point.WriteAddress.HasValue).Select(point => point.WriteAddress!.Value).ToList();
        if (Resolve(settings, overrides, "reset").WriteAddress is ushort reset)
            writes.Add(reset);
        if (overrides.GetValueOrDefault("reset")?.WriteAddress is ushort resetWrite && PlcAddress.IsPhysicalIo(resetWrite))
            throw new InvalidOperationException("复位写入地址不能位于 X 输入区或 Y 输出区。");
        if (writes.Count != writes.Distinct().Count())
            throw new InvalidOperationException("检测开关的写入地址与其他检测开关或复位地址重叠。");
        if (switches.Any(point => point.WriteAddress is ushort write && PlcAddress.IsPhysicalIo(write)))
            throw new InvalidOperationException("检测开关的写入地址不能位于 X 输入区或 Y 输出区。");
        if (switches.Any(point => point.FeedbackAddress is ushort read && read != point.WriteAddress && writes.Contains(read)))
            throw new InvalidOperationException("检测开关的反馈地址与其他命令线圈重叠。");

        var resolvedManual = ManualPoints.Resolve(manual, writes);
        var manualWrites = resolvedManual.Values.Select(point => point.WriteAddress).ToHashSet();
        if (switches.Any(point => point.FeedbackAddress is ushort read && manualWrites.Contains(read)))
            throw new InvalidOperationException("检测开关的反馈地址与手动写入地址重叠。");
        if (recordTrigger is ushort trigger && (PlcAddress.IsPhysicalIo(trigger) || writes.Contains(trigger)
            || switches.Any(point => point.FeedbackAddress == trigger)
            || resolvedManual.Values.Any(point => point.WriteAddress == trigger || point.FeedbackAddress == trigger)))
            throw new InvalidOperationException("记录触发须使用独立的可写线圈，不能与命令、反馈地址重叠，也不能位于 X/Y 区。");
        return resolvedManual;
    }
}

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
        IReadOnlyDictionary<string, InspectionPoint> overrides, IReadOnlyDictionary<ManualPointId, ManualPoint>? manual)
    {
        var switches = StationActions.Devices.Where(item => StationActions.IsInspectionSwitch(item.Key))
            .Select(item => Resolve(settings, overrides, item.Key)).ToArray();
        var writes = switches.Where(point => point.WriteAddress.HasValue).Select(point => point.WriteAddress!.Value).ToList();
        if (settings.Commands.TryGetValue("reset", out ushort reset))
            writes.Add(reset);
        if (writes.Count != writes.Distinct().Count())
            throw new InvalidOperationException("检测开关的写入地址与其他检测开关或复位地址重叠。");
        if (switches.Any(point => point.WriteAddress >= ManualPoints.OutputBase))
            throw new InvalidOperationException("检测开关的写入地址不能位于汇川 Y 输出区。");
        if (switches.Any(point => point.FeedbackAddress is ushort read && read != point.WriteAddress && writes.Contains(read)))
            throw new InvalidOperationException("检测开关的反馈地址与其他命令线圈重叠。");

        var resolvedManual = ManualPoints.Resolve(manual, writes);
        var manualWrites = resolvedManual.Values.Select(point => point.WriteAddress).ToHashSet();
        if (switches.Any(point => point.FeedbackAddress is ushort read && manualWrites.Contains(read)))
            throw new InvalidOperationException("检测开关的反馈地址与手动写入地址重叠。");
        return resolvedManual;
    }
}

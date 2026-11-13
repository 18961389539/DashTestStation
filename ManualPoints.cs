namespace DustLoadScada;

public enum ManualPointId
{
    Power, Head, Lower, Slide, Start, Mode, Dust, Brush, Encoder, Scan, Mark, Photo, CameraRecipe
}

public sealed record ManualPoint(ushort WriteAddress, ushort FeedbackAddress);

internal static class ManualPoints
{
    public static readonly string[] Labels =
    [
        "通电", "压头气缸", "上压气缸", "通电平移", "开关气缸", "模式气缸",
        "灰尘模拟气缸", "地刷通电气缸", "编码器气缸", "扫码启动", "打点气缸", "相机拍照", "相机方案切换"
    ];

    public static ManualPoint Default(ManualPointId id) =>
        new((ushort)(50 + (int)id), (ushort)(PlcAddress.YBase + (int)id));

    public static Dictionary<ManualPointId, ManualPoint> Resolve(
        IReadOnlyDictionary<ManualPointId, ManualPoint>? overrides, IEnumerable<ushort> otherCommands)
    {
        var points = Enum.GetValues<ManualPointId>().ToDictionary(id => id,
            id => overrides?.GetValueOrDefault(id) ?? Default(id));
        var writes = points.Values.Select(point => point.WriteAddress).ToArray();
        var feedback = points.Values.Select(point => point.FeedbackAddress).ToArray();
        if (writes.Any(PlcAddress.IsPhysicalIo))
            throw new InvalidOperationException("手动写入地址不能位于 X 输入区或 Y 输出区。");
        if (writes.Distinct().Count() != writes.Length || writes.Intersect(otherCommands).Any())
            throw new InvalidOperationException("手动写入地址重复，或与其他命令线圈重叠。");
        if (writes.Intersect(feedback).Any() || otherCommands.Intersect(feedback).Any())
            throw new InvalidOperationException("命令线圈地址与只读反馈地址重叠。");
        return points;
    }
}

using System.Text;

namespace Newland.Fm430;

internal sealed class BarcodeFrames
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly List<byte> buffer = [];

    internal void Append(ReadOnlySpan<byte> bytes, Action<string> receive)
    {
        foreach (byte value in bytes)
        {
            if (value is 10 or 13)
            {
                if (buffer.Count > 0)
                {
                    try { receive(Utf8.GetString(buffer.ToArray())); }
                    catch (DecoderFallbackException ex)
                    {
                        throw new InvalidDataException("条码不是有效的 UTF-8 数据，请检查扫码枪字符编码设置。", ex);
                    }
                }
                buffer.Clear();
            }
            else
            {
                if (buffer.Count == 4096)
                    throw new InvalidDataException("扫码枪条码超过 4096 字节，已停止接收。");
                buffer.Add(value);
            }
        }
    }

    internal void Clear() => buffer.Clear();
}

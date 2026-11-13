using System.IO.Ports;

namespace Newland.Fm430;

public sealed record Fm430SerialOptions(string PortName, int BaudRate = 9600, int DataBits = 8,
    Parity Parity = Parity.None, StopBits StopBits = StopBits.One, Handshake Handshake = Handshake.None)
{
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PortName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BaudRate);
        if (DataBits is < 5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(DataBits), "数据位须为 5–8。");
        if (!Enum.IsDefined(Parity))
            throw new ArgumentOutOfRangeException(nameof(Parity));
        if (!Enum.IsDefined(StopBits) || StopBits == StopBits.None)
            throw new ArgumentOutOfRangeException(nameof(StopBits));
        if (!Enum.IsDefined(Handshake))
            throw new ArgumentOutOfRangeException(nameof(Handshake));
    }
}

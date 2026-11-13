using System.IO.Ports;

namespace Newland.Fm430;

internal interface ISerialTransport : IDisposable
{
    int Read(byte[] buffer, int timeoutMilliseconds);
    void Write(byte[] bytes);
    void DiscardInput();
}

internal sealed class SerialTransport(SerialPort port) : ISerialTransport
{
    internal static ISerialTransport Open(Fm430SerialOptions options)
    {
        var port = new SerialPort(options.PortName, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
        {
            ReadTimeout = 250,
            WriteTimeout = 3000,
            Handshake = options.Handshake
        };
        bool opened = false;
        try
        {
            port.Open();
            opened = true;
            return new SerialTransport(port);
        }
        finally
        {
            if (!opened)
                port.Dispose();
        }
    }

    public int Read(byte[] buffer, int timeoutMilliseconds)
    {
        port.ReadTimeout = timeoutMilliseconds;
        return port.Read(buffer, 0, buffer.Length);
    }

    public void Write(byte[] bytes) => port.Write(bytes, 0, bytes.Length);
    public void DiscardInput() => port.DiscardInBuffer();
    public void Dispose() => port.Dispose();
}

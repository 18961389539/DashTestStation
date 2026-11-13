using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;

namespace DustLoadScada;

public sealed record CameraSettings(string Host = "", int Port = 5000);
public sealed record ScannerSettings(string Port = "", int BaudRate = 9600, int DataBits = 8,
    Parity Parity = Parity.None, StopBits StopBits = StopBits.One, Handshake Handshake = Handshake.None);
public sealed record PeripheralSettings(CameraSettings Camera, ScannerSettings Scanner);

internal static class StationDevices
{
    internal static ModbusStation Plc { get; } = new();
    internal static CameraConnection Camera { get; } = new();
    internal static ScannerConnection Scanner { get; } = new();
}

internal sealed record DeviceStatus(string State, string Detail = "", string LastReceived = "");

internal sealed class CameraConnection
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private TcpClient? client;
    private CancellationTokenSource? cancellation;
    private Task receiving = Task.CompletedTask;
    private volatile DeviceStatus status = new("未连接");
    internal DeviceStatus Status => status;

    internal async Task ConnectAsync(CameraSettings settings)
    {
        await gate.WaitAsync();
        try
        {
            await CloseAsync();
            status = new("连接中", $"{settings.Host}:{settings.Port}");
            var socket = new TcpClient { NoDelay = true };
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);
                client = socket;
                cancellation = new CancellationTokenSource();
                status = new("已连接", $"{settings.Host}:{settings.Port}");
                receiving = ReceiveAsync(socket, cancellation.Token);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                status = new("连接失败", ex is OperationCanceledException ? "TCP 连接超时。" : ex.Message);
                throw new IOException(status.Detail, ex);
            }
        }
        finally { gate.Release(); }
    }

    internal async Task SendAsync(string text)
    {
        await gate.WaitAsync();
        try
        {
            if (client is null || status.State != "已连接")
                throw new InvalidOperationException("请先连接相机。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text), timeout.Token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { await CloseAsync(); }
        finally { gate.Release(); }
    }

    private async Task CloseAsync()
    {
        cancellation?.Cancel();
        client?.Dispose();
        await receiving.ConfigureAwait(false);
        cancellation?.Dispose();
        cancellation = null;
        client = null;
        status = new("未连接");
    }

    private async Task ReceiveAsync(TcpClient socket, CancellationToken token)
    {
        var buffer = new byte[256];
        try
        {
            var stream = socket.GetStream();
            int length;
            while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                var hex = Convert.ToHexString(buffer.AsSpan(0, length));
                status = status with { LastReceived = string.Join(" ", Enumerable.Range(0, length)
                    .Select(i => hex.Substring(i * 2, 2))) };
            }
            status = status with { State = "已断开", Detail = "相机关闭了 TCP 连接。" };
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested)
                status = status with { State = "连接异常", Detail = ex.Message };
        }
        finally { socket.Dispose(); }
    }
}

internal sealed class ScannerConnection
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private SerialPort? port;
    private CancellationTokenSource? cancellation;
    private Task receiving = Task.CompletedTask;
    private volatile DeviceStatus status = new("未连接");
    internal DeviceStatus Status => status;

    internal async Task ConnectAsync(ScannerSettings settings)
    {
        await gate.WaitAsync();
        try
        {
            await CloseAsync();
            status = new("连接中", settings.Port);
            try
            {
                var openedPort = await Task.Run(() => OpenPort(settings)).ConfigureAwait(false);
                port = openedPort;
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                status = new("已连接", $"{settings.Port} · {settings.BaudRate} bps");
                receiving = Task.Run(() => Receive(openedPort, token));
            }
            catch (Exception ex)
            {
                status = new("连接失败", ex.Message);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    internal async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { await CloseAsync(); }
        finally { gate.Release(); }
    }

    internal async Task SendAsync(byte[] command)
    {
        if (command.Length == 0)
            throw new ArgumentException("请输入要发送的扫码枪指令。", nameof(command));

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (port is not { IsOpen: true } activePort || status.State != "已连接")
                throw new InvalidOperationException("请先连接扫码枪。");

            await Task.Run(() => activePort.Write(command, 0, command.Length)).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task CloseAsync()
    {
        cancellation?.Cancel();
        await receiving.ConfigureAwait(false);
        cancellation?.Dispose();
        cancellation = null;
        port = null;
        status = new("未连接");
    }

    private static SerialPort OpenPort(ScannerSettings settings)
    {
        var port = new SerialPort(settings.Port, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            ReadTimeout = 250,
            WriteTimeout = 3000,
            Handshake = settings.Handshake
        };
        try { port.Open(); return port; }
        catch { port.Dispose(); throw; }
    }

    private void Receive(SerialPort port, CancellationToken token)
    {
        try
        {
            using (port)
            {
                var frame = new ScannerFrames();
                var buffer = new byte[256];
                while (!token.IsCancellationRequested)
                {
                    int length;
                    try { length = port.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    frame.Append(buffer.AsSpan(0, length), barcode => status = status with { LastReceived = barcode });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested)
                status = status with { State = "连接异常", Detail = ex.Message };
        }
    }
}

internal static class ScannerCommand
{
    internal static byte[] Encode(string text, bool hex, string ending)
    {
        if (!hex)
        {
            if (text.Length == 0)
                throw new InvalidOperationException("请输入要发送的扫码枪指令。");
            return Encoding.UTF8.GetBytes(text + ending);
        }

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException("请输入要发送的 HEX 指令。");

        if (parts.Any(part => part.Length % 2 != 0))
            throw new FormatException("HEX 指令每个字节须由两位十六进制字符组成，例如 02 41 03。");

        try { return Convert.FromHexString(string.Concat(parts)); }
        catch (FormatException ex)
        {
            throw new FormatException("HEX 指令仅支持 0–9、A–F 和空白分隔符，例如 02 41 03。", ex);
        }
    }
}

internal sealed class ScannerFrames
{
    private readonly List<byte> buffer = [];
    private bool overflow;

    internal void Append(ReadOnlySpan<byte> bytes, Action<string> receive)
    {
        foreach (byte value in bytes)
        {
            if (value is 10 or 13)
            {
                if (!overflow && buffer.Count > 0)
                    receive(Encoding.UTF8.GetString(buffer.ToArray()));
                buffer.Clear();
                overflow = false;
            }
            else if (buffer.Count < 4096)
                buffer.Add(value);
            else
                overflow = true;
        }
    }
}

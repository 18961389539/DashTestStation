using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using Newland.Fm430;

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
    private readonly object replyLock = new();
    private readonly StringBuilder replyText = new();
    private TaskCompletionSource<string>? reply;
    private long lastReceivedTicks;
    // Set after a successful connect; cleared by DisconnectAsync. Reconnects use it after a link loss.
    private CameraSettings? target;
    private int generation;
    internal TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);
    internal DeviceStatus Status => status;

    internal async Task ConnectAsync(CameraSettings settings)
    {
        await gate.WaitAsync();
        try
        {
            await CloseAsync();
            generation++;
            target = null;
            status = new("连接中", $"{settings.Host}:{settings.Port}");
            try { await OpenAsync(settings).ConfigureAwait(false); }
            catch (Exception ex)
            {
                status = new("连接失败", ex is OperationCanceledException ? "TCP 连接超时。" : ex.Message);
                throw new IOException(status.Detail, ex);
            }
            target = settings;
        }
        finally { gate.Release(); }
    }

    // Startup connect: on failure keep retrying in the background instead of throwing.
    internal async Task StartAutoConnectAsync(CameraSettings settings)
    {
        int started;
        await gate.WaitAsync();
        try
        {
            await CloseAsync();
            started = ++generation;
            target = settings;
            status = new("连接中", $"{settings.Host}:{settings.Port}");
            try
            {
                await OpenAsync(settings).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                status = new("重连中", $"{settings.Host}:{settings.Port} 连接失败，稍后重试。" +
                    (ex is OperationCanceledException ? "TCP 连接超时。" : ex.Message));
            }
        }
        finally { gate.Release(); }
        _ = ReconnectAsync(started);
    }

    private async Task OpenAsync(CameraSettings settings)
    {
        var socket = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        client = socket;
        cancellation = new CancellationTokenSource();
        status = new("已连接", $"{settings.Host}:{settings.Port}");
        receiving = ReceiveAsync(socket, cancellation.Token, generation);
    }

    private async Task ReconnectAsync(int lostGeneration)
    {
        while (true)
        {
            await Task.Delay(ReconnectDelay).ConfigureAwait(false);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (lostGeneration != generation || target is not { } settings)
                    return;
                await CloseAsync(status).ConfigureAwait(false);
                try
                {
                    await OpenAsync(settings).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    status = new("重连中", $"{settings.Host}:{settings.Port} 重连失败，稍后重试。" +
                        (ex is OperationCanceledException ? "TCP 连接超时。" : ex.Message));
                }
            }
            finally { gate.Release(); }
        }
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

    internal async Task<string> RequestAsync(string text, TimeSpan timeout)
    {
        await gate.WaitAsync();
        try
        {
            if (client is null || status.State != "已连接")
                throw new InvalidOperationException("请先连接相机。");
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await ArmAfterQuietAsync(client, pending).ConfigureAwait(false);
            try
            {
                using var expiry = new CancellationTokenSource(timeout);
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text), expiry.Token).ConfigureAwait(false);
                return await pending.Task.WaitAsync(expiry.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                string received;
                lock (replyLock)
                    received = replyText.ToString().Trim();
                throw new TimeoutException(received.Length == 0
                    ? $"相机未在 {timeout.TotalSeconds:0} 秒内回复。"
                    : $"相机回复“{received}”无法识别。");
            }
            finally
            {
                lock (replyLock)
                {
                    reply = null;
                    replyText.Clear();
                }
            }
        }
        finally { gate.Release(); }
    }

    // Data that arrived before the command (in flight, in the socket buffer, or being processed by
    // the receive loop) must not be taken as this command's reply. Wait until the link has been idle
    // for a short period measured from the request, then arm the reply before the command is written.
    private async Task ArmAfterQuietAsync(TcpClient socket, TaskCompletionSource<string> pending)
    {
        const long QuietMs = 50;
        long start = Environment.TickCount64;
        long deadline = start + 1000;
        while (true)
        {
            if (status.State != "已连接")
                throw new InvalidOperationException("请先连接相机。");
            lock (replyLock)
            {
                int available;
                try { available = socket.Available; }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                {
                    throw new IOException("相机连接已中断。", ex);
                }
                if (available == 0 && Environment.TickCount64 - Math.Max(start, lastReceivedTicks) >= QuietMs)
                {
                    replyText.Clear();
                    reply = pending;
                    return;
                }
            }
            if (Environment.TickCount64 > deadline)
                throw new IOException("相机持续发送数据，无法确认本次切换的回复。");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    internal async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try
        {
            generation++;
            target = null;
            await CloseAsync();
        }
        finally { gate.Release(); }
    }

    private void OnReceived(ReadOnlySpan<char> text)
    {
        lock (replyLock)
        {
            lastReceivedTicks = Environment.TickCount64;
            if (reply is null)
                return;
            replyText.Append(text);
            string received = replyText.ToString();
            string trimmed = received.Trim();
            if (trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("fail", StringComparison.OrdinalIgnoreCase))
                reply.TrySetResult(trimmed);
            else if (received.IndexOfAny(['\r', '\n']) >= 0 && trimmed.Length != 0)
                reply.TrySetResult(trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim());
        }
    }

    private void FailReply(string message)
    {
        lock (replyLock)
            reply?.TrySetException(new IOException(message));
    }

    private async Task CloseAsync(DeviceStatus? keep = null)
    {
        cancellation?.Cancel();
        client?.Dispose();
        await receiving.ConfigureAwait(false);
        cancellation?.Dispose();
        cancellation = null;
        client = null;
        status = keep ?? new("未连接");
    }

    private async Task ReceiveAsync(TcpClient socket, CancellationToken token, int linkGeneration)
    {
        try { await ReadUntilClosedAsync(socket, token).ConfigureAwait(false); }
        finally { socket.Dispose(); }
        if (!token.IsCancellationRequested)
        {
            status = status with { State = "重连中", Detail = $"{status.Detail} 正在自动重连。" };
            _ = ReconnectAsync(linkGeneration);
        }
    }

    private async Task ReadUntilClosedAsync(TcpClient socket, CancellationToken token)
    {
        var buffer = new byte[256];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            var stream = socket.GetStream();
            int length;
            while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                var hex = Convert.ToHexString(buffer.AsSpan(0, length));
                status = status with { LastReceived = string.Join(" ", Enumerable.Range(0, length)
                    .Select(i => hex.Substring(i * 2, 2))) };
                int count = decoder.GetChars(buffer, 0, length, chars, 0);
                OnReceived(chars.AsSpan(0, count));
            }
            status = status with { State = "已断开", Detail = "相机关闭了 TCP 连接。" };
            FailReply(status.Detail);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested)
                status = status with { State = "连接异常", Detail = ex.Message };
            FailReply(ex.Message);
        }
    }
}

internal sealed class ScannerConnection
{
    // 手册 NGR 读码未成功信息默认内容；仅用于设备管理页展示，测试流程仍统一按条码处理。
    private const string NoReadMessage = "NG";
    internal const string NoReadDisplay = "未读到条码（NG）";

    private readonly SemaphoreSlim gate = new(1, 1);
    private Fm430Scanner? scanner;
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
                var client = new Fm430Scanner(new Fm430SerialOptions(settings.Port, settings.BaudRate,
                    settings.DataBits, settings.Parity, settings.StopBits, settings.Handshake));
                bool owned = false;
                try
                {
                    await client.ConnectAsync().ConfigureAwait(false);
                    scanner = client;
                    owned = true;
                }
                finally
                {
                    if (!owned)
                        await client.DisposeAsync().ConfigureAwait(false);
                }
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                status = new("已连接", $"{settings.Port} · {settings.BaudRate} bps");
                receiving = ReceiveAsync(client, token);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException
                or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
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
            if (scanner is not { IsConnected: true } activeScanner || status.State != "已连接")
                throw new InvalidOperationException("请先连接扫码枪。");

            await activeScanner.SendAsync(command).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<string> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (scanner is not { IsConnected: true } activeScanner || status.State != "已连接")
                throw new InvalidOperationException("请先连接扫码枪。");

            await StopReceivingAsync().ConfigureAwait(false);
            try
            {
                string barcode = await activeScanner.ScanAsync(timeout, cancellationToken).ConfigureAwait(false);
                status = status with { LastReceived = barcode };
                return barcode;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                or AggregateException)
            {
                if (!activeScanner.IsConnected)
                    status = status with { State = "连接异常", Detail = ex.Message };
                throw;
            }
            finally
            {
                if (activeScanner.IsConnected)
                    StartReceiving(activeScanner);
            }
        }
        finally { gate.Release(); }
    }

    private void StartReceiving(Fm430Scanner client)
    {
        cancellation = new CancellationTokenSource();
        receiving = ReceiveAsync(client, cancellation.Token);
    }

    private async Task StopReceivingAsync()
    {
        cancellation?.Cancel();
        try { await receiving.ConfigureAwait(false); }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    private async Task CloseAsync()
    {
        await StopReceivingAsync().ConfigureAwait(false);
        if (scanner is not null)
            await scanner.DisposeAsync().ConfigureAwait(false);
        scanner = null;
        status = new("未连接");
    }

    private async Task ReceiveAsync(Fm430Scanner client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                string barcode = await client.ReadBarcodeAsync(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                status = status with { LastReceived = barcode == NoReadMessage ? NoReadDisplay : barcode };
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or AggregateException)
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

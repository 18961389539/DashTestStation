using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Newland.Fm430;

public sealed class Fm430Scanner : IAsyncDisposable
{
    private static readonly byte[] StartCommand = [0x01, 0x54, 0x04];
    private static readonly byte[] StopCommand = [0x01, 0x50, 0x04];
    private readonly Fm430SerialOptions options;
    private readonly Func<Fm430SerialOptions, ISerialTransport> openTransport;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim io = new(1, 1);
    private readonly SemaphoreSlim reading = new(1, 1);
    private readonly BarcodeFrames frames = new();
    private readonly Queue<string> barcodes = new();
    private ISerialTransport? transport;
    private CancellationTokenSource? connection;
    private Exception? failure;
    private volatile bool connected;
    private volatile bool disposed;

    public Fm430Scanner(Fm430SerialOptions options) : this(options, SerialTransport.Open) { }

    internal Fm430Scanner(Fm430SerialOptions options, Func<Fm430SerialOptions, ISerialTransport> openTransport)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openTransport);
        options.Validate();
        this.options = options;
        this.openTransport = openTransport;
    }

    public bool IsConnected => connected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await io.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (transport is not null)
                    throw new InvalidOperationException("扫码枪已经连接，请先断开后再连接。");

                var opened = await Task.Run(() => openTransport(options)).ConfigureAwait(false);
                bool owned = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    connection = new CancellationTokenSource();
                    transport = opened;
                    failure = null;
                    connected = true;
                    owned = true;
                }
                finally
                {
                    if (!owned)
                        opened.Dispose();
                }
            }
            finally { io.Release(); }
        }
        finally { lifecycle.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); }
        finally { lifecycle.Release(); }
    }

    public Task StartScanningAsync(CancellationToken cancellationToken = default) => SendAsync(StartCommand, cancellationToken);
    public Task StopScanningAsync(CancellationToken cancellationToken = default) => SendAsync(StopCommand, cancellationToken);

    public async Task SendAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        if (command.IsEmpty)
            throw new ArgumentException("请输入要发送的扫码枪指令。", nameof(command));
        byte[] bytes = command.ToArray();
        ISerialTransport? active = null;
        Exception? error = null;
        await io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            active = RequireConnection();
            cancellationToken.ThrowIfCancellationRequested();
            await WriteAsync(active, bytes).ConfigureAwait(false);
        }
        catch (Exception ex) when (active is not null && IsSerialFailure(ex))
        {
            error = ex;
            MarkFailed(ex);
            throw;
        }
        finally
        {
            io.Release();
            if (error is not null && active is not null)
                await CloseFailedAsync(active, error).ConfigureAwait(false);
        }
    }

    public Task<string> ReadBarcodeAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        ReceiveAsync(false, timeout, cancellationToken);

    public Task<string> ScanAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        ReceiveAsync(true, timeout, cancellationToken);

    private async Task<string> ReceiveAsync(bool trigger, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "扫码超时须大于 0，或使用 Timeout.InfiniteTimeSpan。");

        var (active, lifetime) = await BeginReadAsync(trigger, cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        Exception? error = null;
        try
        {
            try
            {
                return await Task.Run(() => Read(active, timeout, linked.Token)).ConfigureAwait(false);
            }
            catch (Exception ex) when (trigger && ex is TimeoutException or OperationCanceledException)
            {
                try { await StopCurrentAsync(active).ConfigureAwait(false); }
                catch (Exception stopError) when (IsSerialFailure(stopError))
                {
                    error = new AggregateException("扫码未完成，且停止扫码指令发送失败。", ex, stopError);
                    throw error;
                }
                throw;
            }
            catch (Exception ex) when (IsSerialFailure(ex))
            {
                error = ex;
                MarkFailed(ex);
                throw;
            }
        }
        finally
        {
            reading.Release();
            if (error is not null)
                await CloseFailedAsync(active, error).ConfigureAwait(false);
        }
    }

    private async Task<(ISerialTransport Active, CancellationToken Lifetime)> BeginReadAsync(
        bool trigger, CancellationToken cancellationToken)
    {
        ISerialTransport? active = null;
        Exception? error = null;
        bool reserved = false;
        bool handedOff = false;
        await io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            active = RequireConnection();
            cancellationToken.ThrowIfCancellationRequested();
            if (!reading.Wait(0))
                throw new InvalidOperationException("已有扫码或接收操作进行中，请先完成或取消该操作。");
            reserved = true;

            if (trigger)
            {
                frames.Clear();
                barcodes.Clear();
                active.DiscardInput();
                await WriteAsync(active, StartCommand).ConfigureAwait(false);
            }
            var lifetime = connection!.Token;
            handedOff = true;
            return (active, lifetime);
        }
        catch (Exception ex) when (reserved && IsSerialFailure(ex))
        {
            error = ex;
            MarkFailed(ex);
            throw;
        }
        finally
        {
            io.Release();
            if (reserved && !handedOff)
                reading.Release();
            if (error is not null && active is not null)
                await CloseFailedAsync(active, error).ConfigureAwait(false);
        }
    }

    private string Read(ISerialTransport active, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var buffer = new byte[256];
        while (true)
        {
            if (Volatile.Read(ref failure) is { } error)
                ExceptionDispatchInfo.Capture(error).Throw();
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("等待扫码枪条码超时。");
            if (barcodes.TryDequeue(out string? barcode))
                return barcode;

            int length;
            try { length = active.Read(buffer, (int)Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 1, 250)); }
            catch (TimeoutException) { continue; }
            if (length == 0)
                throw new IOException("扫码枪串口已停止接收数据。");
            frames.Append(buffer.AsSpan(0, length), barcodes.Enqueue);
        }
    }

    private async Task StopCurrentAsync(ISerialTransport active)
    {
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(transport, active) && Volatile.Read(ref failure) is null)
                await WriteAsync(active, StopCommand).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsSerialFailure(ex))
        {
            MarkFailed(ex);
            throw;
        }
        finally { io.Release(); }
    }

    private static async Task WriteAsync(ISerialTransport active, byte[] bytes)
    {
        try { await Task.Run(() => active.Write(bytes)).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new IOException("扫码枪串口写入超时。", ex); }
    }

    private ISerialTransport RequireConnection()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return connected && transport is { } active
            ? active : throw new InvalidOperationException("请先连接扫码枪。");
    }

    private void MarkFailed(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        connected = false;
        connection?.Cancel();
    }

    private static bool IsSerialFailure(Exception error) =>
        error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException;

    private async Task CloseFailedAsync(ISerialTransport active, Exception error)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(transport, active))
                await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception closeError) when (IsSerialFailure(closeError))
        {
            throw new AggregateException("扫码枪通讯失败，且关闭串口失败。", error, closeError);
        }
        finally { lifecycle.Release(); }
    }

    private async Task CloseAsync()
    {
        await io.WaitAsync().ConfigureAwait(false);
        try
        {
            connected = false;
            connection?.Cancel();
        }
        finally { io.Release(); }

        // Do not hold the I/O gate while waiting: a cancelled scan may still need to send its stop command.
        await reading.WaitAsync().ConfigureAwait(false);
        try
        {
            await io.WaitAsync().ConfigureAwait(false);
            try { transport?.Dispose(); }
            finally
            {
                transport = null;
                connection?.Dispose();
                connection = null;
                frames.Clear();
                barcodes.Clear();
                io.Release();
            }
        }
        finally { reading.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
            await CloseAsync().ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
}

using System.Collections.Concurrent;
using System.Text;
using Xunit;

namespace Newland.Fm430.Tests;

public sealed class Fm430ScannerTests
{
    [Fact]
    public async Task ScanDiscardsQueuedPartialAndUnreadOldData()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        port.Enqueue("previous\r\nqueued\r\npartial");
        Assert.Equal("previous", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
        port.Enqueue("unread-old\r\n");
        port.OnWrite = _ => port.Enqueue("fresh\r\n");

        Assert.Equal("fresh", await scanner.ScanAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(new byte[] { 0x01, 0x54, 0x04 }, Assert.Single(port.Writes));
    }

    [Fact]
    public async Task ReadReassemblesUtf8AndRetainsFollowingFrames()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        byte[] text = Encoding.UTF8.GetBytes("条码A");
        port.Enqueue(text[..2]);
        port.Enqueue([.. text[2..], 13]);
        port.Enqueue("\nsecond\nthird\r\n");

        Assert.Equal("条码A", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("second", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("third", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScanTimeoutOrCancellationSendsStopAndAllowsAnotherScan(bool cancel)
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        Task<string> scan = scanner.ScanAsync(cancel ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(100),
            cancellation.Token);
        await port.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        else
            await Assert.ThrowsAsync<TimeoutException>(() => scan.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(new byte[] { 0x01, 0x54, 0x04 }, port.Writes[0]);
        Assert.Equal(new byte[] { 0x01, 0x50, 0x04 }, port.Writes[1]);
        Assert.True(scanner.IsConnected);
        port.OnWrite = _ => port.Enqueue("retry\r\n");
        Assert.Equal("retry", await scanner.ScanAsync(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectOrDisposeCancelsPendingReadAndReleasesPort(bool dispose)
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        Task<string> read = scanner.ReadBarcodeAsync(Timeout.InfiniteTimeSpan);
        await port.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        if (dispose)
            await scanner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        else
            await scanner.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.False(scanner.IsConnected);
        Assert.True(port.IsDisposed);
        if (dispose)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => scanner.ConnectAsync());
    }

    [Fact]
    public async Task ConcurrentScanDoesNotInterruptAnExistingRead()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        Task<string> reading = scanner.ReadBarcodeAsync(Timeout.InfiniteTimeSpan, cancellation.Token);
        await port.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync(TimeSpan.FromSeconds(1)));
        Assert.True(scanner.IsConnected);
        Assert.Empty(port.Writes);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        port.Enqueue("still-connected\r\n");
        Assert.Equal("still-connected", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingAnActiveScanSendsStopBeforeReleasingPort(bool dispose)
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        Task<string> scan = scanner.ScanAsync(Timeout.InfiniteTimeSpan);
        await port.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        if (dispose)
            await scanner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        else
            await scanner.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal(new byte[] { 0x01, 0x50, 0x04 }, port.Writes[1]);
        Assert.True(port.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrOversizedFramesFailExplicitlyAndClosePort(bool oversized)
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        port.Enqueue(oversized ? [.. Enumerable.Repeat((byte)'A', 4097), 13, 10] : [0xFF, 13, 10]);

        await Assert.ThrowsAsync<InvalidDataException>(() => scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
        Assert.False(scanner.IsConnected);
        Assert.True(port.IsDisposed);
    }

    [Fact]
    public async Task A4096ByteBarcodeIsNotTruncated()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        string barcode = new('A', 4096);
        port.Enqueue(barcode + "\r\n");

        Assert.Equal(barcode, await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task SendFailureFailsPendingReadAndReleasesPort()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        Task<string> read = scanner.ReadBarcodeAsync(Timeout.InfiniteTimeSpan);
        await port.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var error = new IOException("Disconnected during write.");
        port.OnWrite = _ => throw error;

        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() =>
            scanner.SendAsync(new byte[] { 0x41 }).WaitAsync(TimeSpan.FromSeconds(3))));
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(3))));
        Assert.False(scanner.IsConnected);
        Assert.True(port.IsDisposed);
    }

    [Fact]
    public async Task StopFailurePreservesTheScanTimeout()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        port.OnWrite = bytes =>
        {
            if (bytes[1] == 0x50)
                throw new IOException("Stop command failed.");
        };

        var error = await Assert.ThrowsAsync<AggregateException>(() => scanner.ScanAsync(TimeSpan.FromMilliseconds(50)));
        Assert.IsType<TimeoutException>(error.InnerExceptions[0]);
        Assert.IsType<IOException>(error.InnerExceptions[1]);
        Assert.False(scanner.IsConnected);
        Assert.True(port.IsDisposed);
    }

    [Fact]
    public async Task CommandsAreRawBytesAndQueuedWritesCopyCallerMemory()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        port.OnWrite = bytes =>
        {
            if (bytes[0] == 0x01)
            {
                started.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(3)))
                    throw new TimeoutException("Test did not release the writer.");
            }
        };

        Task first = scanner.StartScanningAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        byte[] command = [0x02, 0x41, 0x03];
        Task second = scanner.SendAsync(command);
        command[1] = 0xFF;
        release.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        port.OnWrite = null;
        await scanner.StopScanningAsync();

        Assert.Equal(new byte[] { 0x01, 0x54, 0x04 }, port.Writes[0]);
        Assert.Equal(new byte[] { 0x02, 0x41, 0x03 }, port.Writes[1]);
        Assert.Equal(new byte[] { 0x01, 0x50, 0x04 }, port.Writes[2]);
    }

    [Fact]
    public async Task CancelledConnectClosesThePortOpenedInTheBackground()
    {
        using var port = new FakeSerialTransport();
        using var release = new ManualResetEventSlim();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scanner = new Fm430Scanner(new("COM-test"), _ =>
        {
            opened.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(3)))
                throw new TimeoutException("Test did not release the opener.");
            return port;
        });
        using var cancellation = new CancellationTokenSource();
        Task connecting = scanner.ConnectAsync(cancellation.Token);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(scanner.IsConnected);
        Assert.True(port.IsDisposed);
    }

    [Fact]
    public async Task ReconnectDoesNotReuseOldFrames()
    {
        using var first = new FakeSerialTransport();
        using var second = new FakeSerialTransport();
        var ports = new Queue<ISerialTransport>([first, second]);
        await using var scanner = new Fm430Scanner(new("COM-test"), _ => ports.Dequeue());
        await scanner.ConnectAsync();
        first.Enqueue("old\r\nqueued\r\npartial");
        Assert.Equal("old", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
        await scanner.DisconnectAsync();
        await scanner.ConnectAsync();
        second.Enqueue("new\r\n");

        Assert.Equal("new", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public async Task CustomTriggerCommandsAreUsedForScanAndStop()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        scanner.StartCommand = [0x02, 0x54, 0x03];
        scanner.StopCommand = [0x02, 0x50, 0x03];
        await scanner.ConnectAsync();
        port.OnWrite = _ => port.Enqueue("custom\r\n");

        Assert.Equal("custom", await scanner.ScanAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(new byte[] { 0x02, 0x54, 0x03 }, Assert.Single(port.Writes));

        port.OnWrite = null;
        await scanner.StopScanningAsync();
        Assert.Equal(new byte[] { 0x02, 0x50, 0x03 }, port.Writes[1]);
    }

    [Fact]
    public async Task TriggerCommandValidationRejectsInvalidBytes()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        byte[][] invalid = [[], new byte[11], [0x3F, 0x54], [0x01, 0x00, 0x04]];
        foreach (byte[] command in invalid)
        {
            Assert.Throws<ArgumentException>(() => scanner.StartCommand = command);
            Assert.Throws<ArgumentException>(() => scanner.StopCommand = command);
        }
        Assert.Throws<ArgumentNullException>(() => scanner.StartCommand = null!);
    }

    [Fact]
    public async Task TriggerCommandSetterCopiesCallerBytes()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        byte[] command = [0x02, 0x54, 0x03];
        scanner.StartCommand = command;
        command[1] = 0xFF;
        await scanner.ConnectAsync();
        await scanner.StartScanningAsync();

        Assert.Equal(new byte[] { 0x02, 0x54, 0x03 }, Assert.Single(port.Writes));
    }

    [Fact]
    public async Task SetupCommandsAreSentAsAscii()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        await scanner.SendSetupAsync("@ORTSET3000;");
        await scanner.SetReadTimeoutAsync(2000);

        Assert.Equal(Encoding.ASCII.GetBytes("@ORTSET3000;"), port.Writes[0]);
        Assert.Equal(Encoding.ASCII.GetBytes("@ORTSET2000;"), port.Writes[1]);
        await Assert.ThrowsAsync<ArgumentException>(() => scanner.SendSetupAsync("@ORTSET三千;"));
        await Assert.ThrowsAsync<ArgumentException>(() => scanner.SendSetupAsync(" "));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scanner.SetReadTimeoutAsync(0));
    }

    [Fact]
    public async Task NoReadMessageIsReturnedAsAnOrdinaryFrame()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        await scanner.ConnectAsync();
        port.Enqueue("NG\r\n");
        Assert.Equal("NG", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));

        port.OnWrite = bytes => { if (bytes[1] == 0x54) port.Enqueue("NG\r\n"); };
        Assert.Equal("NG", await scanner.ScanAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task DataReceivedReportsRawBytesEvenWithoutFrameTerminator()
    {
        using var port = new FakeSerialTransport();
        await using var scanner = Create(port);
        var received = new ConcurrentQueue<byte[]>();
        scanner.DataReceived += bytes => received.Enqueue(bytes.ToArray());
        await scanner.ConnectAsync();
        port.Enqueue([0x02, 0x41, 0x03]);

        await Assert.ThrowsAsync<TimeoutException>(() => scanner.ReadBarcodeAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(new byte[] { 0x02, 0x41, 0x03 }, Assert.Single(received));

        port.Enqueue("OK\r\n");
        Assert.Equal("\u0002A\u0003OK", await scanner.ReadBarcodeAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, received.Count);
    }

    private static Fm430Scanner Create(FakeSerialTransport port) => new(new("COM-test"), _ => port);

    private sealed class FakeSerialTransport : ISerialTransport
    {
        private readonly BlockingCollection<byte[]> input = new();
        private readonly ConcurrentQueue<byte[]> writes = new();
        private byte[] current = [];
        private int offset;
        private bool disposed;
        public bool IsDisposed => disposed;
        public byte[][] Writes => writes.ToArray();
        public Action<byte[]>? OnWrite { get; set; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enqueue(string text) => Enqueue(Encoding.UTF8.GetBytes(text));
        public void Enqueue(byte[] bytes) => input.Add(bytes);

        public int Read(byte[] buffer, int timeoutMilliseconds)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ReadStarted.TrySetResult();
            if (offset == current.Length)
            {
                if (!input.TryTake(out byte[]? next, timeoutMilliseconds))
                    throw new TimeoutException();
                current = next;
                offset = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsSpan(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }

        public void Write(byte[] bytes)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            byte[] copy = bytes.ToArray();
            writes.Enqueue(copy);
            OnWrite?.Invoke(copy);
        }

        public void DiscardInput()
        {
            current = [];
            offset = 0;
            while (input.TryTake(out _)) { }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            input.Dispose();
        }
    }
}

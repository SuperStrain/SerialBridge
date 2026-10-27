using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using SerialBridge.Core.Serial;

namespace SerialBridge.Core.Tests;

/// <summary>
/// 模拟串口设备的内存流：一端接 SerialBridge（Stream 语义，支持 ReadTimeout 超时轮询），
/// 另一端由测试代码充当“物理设备”写入/读取。
/// </summary>
public sealed class FakePortStream : Stream
{
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
    private readonly ConcurrentQueue<byte[]> _writtenToPin = new();

    /// <summary>SerialBridge 写入串口的数据（设备端收到的数据）。</summary>
    public ConcurrentQueue<byte[]> DataWrittenByBridge => _writtenToPin;

    public bool IsDisposed { get; private set; }
    public ManualResetEventSlim? WriteGate { get; set; }
    public ManualResetEventSlim WriteStarted { get; } = new(false);

    public override bool CanRead => !IsDisposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !IsDisposed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int ReadTimeout { get; set; } = 200;
    public override int WriteTimeout { get; set; } = 3000;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FakePortStream));
        if (ReadTimeout > 0)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReadTimeout);
            try
            {
                return await ReadCoreAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
        }
        return await ReadCoreAsync(buffer, ct);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken ct)
    {
        ReadResult result = await _pipe.Reader.ReadAsync(ct);
        var len = Math.Min(buffer.Length, result.Buffer.Length);
        result.Buffer.Slice(0, len).CopyTo(buffer.Span);
        // consumed 与 examined 必须一致：若 examined 标记到缓冲区末尾，
        // 剩余未消费的数据会被视为“已检查”，写入端停止后 ReadAsync 将永久挂起
        _pipe.Reader.AdvanceTo(result.Buffer.GetPosition(len), result.Buffer.GetPosition(len));
        return (int)len;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FakePortStream));
        await Task.Yield();
        _writtenToPin.Enqueue(buffer.ToArray());
    }

    /// <summary>模拟物理设备向串口发送数据（SerialBridge 将从 ReadAsync 读到）。</summary>
    public async ValueTask SimulateDeviceSendAsync(ReadOnlyMemory<byte> data)
    {
        await _pipe.Writer.WriteAsync(data);
    }

    public override void Flush() { }

    /// <summary>同步读：与 SerialPort.Read 语义一致——阻塞至有数据或 ReadTimeout 超时。</summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(FakePortStream));
        var deadline = ReadTimeout > 0 ? Environment.TickCount64 + ReadTimeout : -1;
        while (true)
        {
            if (_pipe.Reader.TryRead(out var result))
            {
                var len = (int)Math.Min(count, result.Buffer.Length);
                result.Buffer.Slice(0, len).CopyTo(buffer.AsSpan(offset, len));
                _pipe.Reader.AdvanceTo(result.Buffer.GetPosition(len), result.Buffer.GetPosition(len));
                return len;
            }
            if (deadline > 0 && Environment.TickCount64 >= deadline) throw new TimeoutException();
            Thread.Sleep(5);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteStarted.Set();
        WriteGate?.Wait();
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _writtenToPin.Enqueue(buffer[offset..(offset + count)]);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) => IsDisposed = true;
}

/// <summary>可控的假串口工厂：记录打开的流，可模拟拔出与占用。</summary>
public sealed class FakePortFactory : IPortStreamFactory
{
    public ConcurrentQueue<FakePortStream> OpenedStreams { get; } = new();
    public Func<PortParams, PortOpenException>? OpenFailure { get; set; }
    public event Action<FakePortStream>? StreamOpened;

    public PortStreamHandle Open(PortParams parameters)
    {
        if (OpenFailure is not null) throw OpenFailure(parameters);
        var stream = new FakePortStream();
        OpenedStreams.Enqueue(stream);
        StreamOpened?.Invoke(stream);
        return new PortStreamHandle(stream, () => stream.Dispose(), parameters.PortName);
    }
}

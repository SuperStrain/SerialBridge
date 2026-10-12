namespace SerialBridge.App.Services;

/// <summary>进程内非阻塞下载门：同一时刻只允许一个升级包下载。</summary>
internal sealed class DownloadGate
{
    private int _held;

    public bool TryAcquire(out IDisposable lease)
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            lease = EmptyLease.Instance;
            return false;
        }

        lease = new GateLease(this);
        return true;
    }

    private void Release() => Volatile.Write(ref _held, 0);

    private sealed class GateLease(DownloadGate owner) : IDisposable
    {
        private DownloadGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public void Dispose() { }
    }
}

using System.IO;
using System.IO.Ports;

namespace SerialBridge.Core.Serial;

/// <summary>串口通信参数。</summary>
public sealed record PortParams(
    string PortName,
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    Handshake Handshake);

/// <summary>打开失败的具体原因，便于 UI 给出可理解的提示。</summary>
public enum PortOpenError
{
    None = 0,
    /// <summary>串口不存在（可能已被拔出）。</summary>
    NotFound,
    /// <summary>串口被其他程序占用。</summary>
    AccessDenied,
    /// <summary>其他 IO 错误。</summary>
    IoError,
}

/// <summary>
/// 物理串口打开结果。Stream 用于异步读写；Dispose 需同时关闭串口本身。
/// 抽象出该工厂便于单元测试中用内存流替代真实串口。
/// </summary>
public interface IPortStreamFactory
{
    /// <exception cref="PortOpenException">打开失败时抛出。</exception>
    PortStreamHandle Open(PortParams parameters);
}

/// <summary>串口打开失败异常。</summary>
public sealed class PortOpenException(PortOpenError kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PortOpenError Kind { get; } = kind;
}

/// <summary>一次成功打开的串口会话（持有 Stream 与释放动作）。</summary>
public sealed class PortStreamHandle(Stream stream, Action disposeAction, string portName) : IDisposable
{
    /// <summary>支持异步读写的双向流（真实串口为 SerialPort.BaseStream）。</summary>
    public Stream Stream { get; } = stream;

    public string PortName { get; } = portName;

    public void Dispose() => disposeAction();
}

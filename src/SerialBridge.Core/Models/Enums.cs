namespace SerialBridge.Core.Models;

/// <summary>TCP 客户端接入模式。</summary>
public enum ClientMode
{
    /// <summary>同一时间仅允许一个客户端。</summary>
    Single = 0,

    /// <summary>允许多个客户端，串口数据广播给所有客户端。</summary>
    Multi = 1,
}

/// <summary>物理串口会话状态。</summary>
public enum SerialState
{
    /// <summary>未启动或已停止。</summary>
    Stopped = 0,

    /// <summary>正在打开串口。</summary>
    Opening = 1,

    /// <summary>串口已打开，可正常收发。</summary>
    Open = 2,

    /// <summary>串口异常断开，等待自动重连。</summary>
    ReconnectWaiting = 3,

    /// <summary>无法打开（如被其他程序占用），且不会自动重试。</summary>
    Faulted = 4,
}

/// <summary>映射整体运行状态。</summary>
public enum MappingState
{
    Stopped = 0,
    Starting = 1,
    Running = 2,
    Faulted = 3,
}

/// <summary>数据方向（以物理串口为参照）。</summary>
public enum DataDirection
{
    /// <summary>串口 → 网络：设备上传给客户端。</summary>
    SerialToNet = 0,

    /// <summary>网络 → 串口：客户端下发到设备。</summary>
    NetToSerial = 1,
}

using System.IO;
using System.IO.Ports;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Serial;

/// <summary>基于 System.IO.Ports 的真实串口工厂。</summary>
public sealed class SerialPortStreamFactory : IPortStreamFactory
{
    public PortStreamHandle Open(PortParams p)
    {
        var port = new SerialPort(p.PortName, p.BaudRate, p.Parity, p.DataBits, p.StopBits)
        {
            Handshake = p.Handshake,
            ReadTimeout = 200,      // 读循环用超时轮询，保证能干净退出
            WriteTimeout = 3000,    // 硬件流控阻塞时避免写线程永久挂起
            DtrEnable = true,
            RtsEnable = true,
        };
        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new PortOpenException(PortOpenError.AccessDenied,
                $"串口 {p.PortName} 被其他程序占用", ex);
        }
        catch (IOException ex)
        {
            port.Dispose();
            throw new PortOpenException(PortOpenError.NotFound,
                $"串口 {p.PortName} 不存在或已被移除", ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            port.Dispose();
            throw new PortOpenException(PortOpenError.IoError,
                $"串口 {p.PortName} 参数无效（波特率等不受支持）", ex);
        }
        catch (Exception ex)
        {
            port.Dispose();
            throw new PortOpenException(PortOpenError.IoError,
                $"打开串口 {p.PortName} 失败：{ex.Message}", ex);
        }

        LogX.Info($"串口 {p.PortName} 已打开（{p.BaudRate} bps）");
        var stream = port.BaseStream;
        return new PortStreamHandle(stream, () =>
        {
            try
            {
                port.Close();
            }
            catch (Exception ex)
            {
                LogX.Warn($"关闭串口 {p.PortName} 时出现异常：{ex.Message}");
            }
            port.Dispose();
        }, p.PortName);
    }
}

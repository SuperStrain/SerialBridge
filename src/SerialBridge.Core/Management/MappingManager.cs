using SerialBridge.Core.Bridge;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Management;

/// <summary>
/// 映射管理器：持有全部映射运行时，负责创建/修改/删除、启动/停止
/// 以及串口与 TCP 端口的互斥检查（同一物理串口、同一监听端点不允许被两条运行中的映射同时占用）。
/// </summary>
public sealed class MappingManager : IDisposable
{
    private readonly object _gate = new();
    private readonly List<MappingRuntime> _mappings = new();

    /// <summary>映射列表变化（后台或 UI 线程触发，取决于调用线程）。</summary>
    public event Action? MappingsChanged;

    public IReadOnlyList<MappingRuntime> Mappings { get { lock (_gate) return _mappings.ToArray(); } }

    public int RunningCount { get { lock (_gate) return _mappings.Count(m => m.IsRunning); } }

    public long TotalRxBytes { get { lock (_gate) return _mappings.Sum(m => m.SerialRxBytes); } }
    public long TotalTxBytes { get { lock (_gate) return _mappings.Sum(m => m.SerialTxBytes); } }

    /// <summary>校验结果（供 UI 展示的友好错误）。</summary>
    public string? ValidateNew(MappingConfig candidate, MappingRuntime? self = null)
    {
        lock (_gate)
        {
            foreach (var m in _mappings)
            {
                if (ReferenceEquals(m, self) || !m.IsRunning) continue;
                if (string.Equals(m.Config.PortName, candidate.PortName, StringComparison.OrdinalIgnoreCase))
                    return $"串口 {candidate.PortName} 正在被映射「{m.Config.Name}」使用，不能同时占用。";
                if (string.Equals(m.Config.ListenIp, candidate.ListenIp, StringComparison.OrdinalIgnoreCase) &&
                    m.Config.ListenPort == candidate.ListenPort)
                    return $"监听地址 {candidate.ListenIp}:{candidate.ListenPort} 正在被映射「{m.Config.Name}」使用。";
            }
        }
        if (candidate.ListenPort is < 1 or > 65535)
            return "TCP 端口必须在 1–65535 之间。";
        return null;
    }

    public MappingRuntime CreateMapping(MappingConfig config)
    {
        var rt = new MappingRuntime(config);
        lock (_gate) _mappings.Add(rt);
        LogX.Info($"已创建映射「{config.Name}」：{config.PortName} ↔ {config.ListenIp}:{config.ListenPort}");
        MappingsChanged?.Invoke();
        return rt;
    }

    /// <summary>用编辑后的配置替换映射（映射运行中则先停止）。返回校验错误，null 表示成功。</summary>
    public async Task<string?> ReplaceMappingAsync(MappingRuntime rt, MappingConfig newConfig)
    {
        var error = ValidateNew(newConfig, rt);
        if (error is not null) return error;

        var wasRunning = rt.IsRunning;
        if (wasRunning) await rt.StopAsync();
        rt.Config.CopyFrom(newConfig);
        LogX.Info($"映射「{rt.Config.Name}」配置已更新");
        MappingsChanged?.Invoke();
        return null;
    }

    public async Task<string?> StartMappingAsync(MappingRuntime rt)
    {
        var error = ValidateNew(rt.Config, rt);
        if (error is not null) return error;
        await rt.StartAsync();
        return rt.State == MappingState.Faulted ? rt.FaultMessage : null;
    }

    public async Task StopMappingAsync(MappingRuntime rt) => await rt.StopAsync();

    public async Task StartAllAsync()
    {
        foreach (var rt in Mappings)
            await StartMappingAsync(rt);
    }

    public async Task StopAllAsync()
    {
        foreach (var rt in Mappings)
            await rt.StopAsync();
    }

    public async Task RemoveMappingAsync(MappingRuntime rt)
    {
        await rt.StopAsync();
        await rt.DisposeAsync();
        lock (_gate) _mappings.Remove(rt);
        LogX.Info($"已删除映射「{rt.Config.Name}」");
        MappingsChanged?.Invoke();
    }

    /// <summary>删除所有映射（用于配置整体重置）。</summary>
    public async Task ClearAsync()
    {
        var all = Mappings;
        foreach (var rt in all) await RemoveMappingAsync(rt);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var rt in _mappings) rt.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            _mappings.Clear();
        }
    }
}

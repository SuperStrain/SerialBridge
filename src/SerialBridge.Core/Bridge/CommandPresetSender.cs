using SerialBridge.Core.Models;

namespace SerialBridge.Core.Bridge;

/// <summary>
/// 将命令预设/宏的不可变快照发送到映射的串口写队列。
/// 终端命令栏与命令面板共用同一条路径：状态预检 → 逐步校验编码 → 顺序入队。
/// 映射停止或串口断开后立即永久中止；即使重连完成，也不继续发送后续步骤。
/// </summary>
public static class CommandPresetSender
{
    /// <summary>串口未连接时抛出，供 UI 在确认弹窗前先行预检。</summary>
    public static void ThrowIfNotConnected(MappingRuntime runtime)
    {
        var status = runtime.GetStatus();
        if (status.State != MappingState.Running || status.SerialState != SerialState.Open)
            throw new IOException("目标串口当前未连接，已取消发送。");
    }

    /// <param name="external">调用方的取消令牌（如“停止”按钮）。</param>
    public static async Task SendAsync(MappingRuntime runtime, CommandPreset preset,
        CancellationToken external = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(preset);
        ThrowIfNotConnected(runtime);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
        void OnRuntimeStatusChanged(MappingRuntime rt)
        {
            var state = rt.GetStatus();
            if (state.State != MappingState.Running || state.SerialState != SerialState.Open)
            {
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { /* execution already ended */ }
            }
        }

        runtime.StatusChanged += OnRuntimeStatusChanged;
        try
        {
            // Close the gap between the precheck and the subscription.
            OnRuntimeStatusChanged(runtime);
            cancellation.Token.ThrowIfCancellationRequested();
            await CommandMacroRunner.RunAsync(preset, bytes =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var current = runtime.GetStatus();
                return current.State == MappingState.Running &&
                    current.SerialState == SerialState.Open && runtime.WriteLocal(bytes);
            }, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            runtime.StatusChanged -= OnRuntimeStatusChanged;
        }
    }
}

using Microsoft.Win32;
using SerialBridge.Core.Logging;

namespace SerialBridge.App.Services;

/// <summary>开机自启动（HKCU 注册表 Run 键，当前用户，无需管理员权限）。</summary>
public static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "SerialBridge";

    public static bool IsLaunchOnStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        catch { return false; }
    }

    /// <summary>设置或取消开机自启动。</summary>
    public static void SetLaunchOnStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(AppName, $"\"{exe}\" --minimized");
            }
            else
            {
                if (key.GetValue(AppName) is not null) key.DeleteValue(AppName);
            }
        }
        catch (Exception ex)
        {
            LogX.Warn($"设置开机自启动失败：{ex.Message}");
        }
    }
}

/// <summary>
/// Windows 防火墙辅助：检测规则是否存在、以管理员权限添加入站规则。
/// 添加规则会触发 UAC，用户拒绝时安静失败并提示手动操作。
/// </summary>
public static class FirewallHelper
{
    public const string RuleName = "SerialBridge TCP Server";

    public static bool IsRuleInstalled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            var output = p?.StandardOutput.ReadToEnd() ?? "";
            p?.WaitForExit(3000);
            return output.Contains(RuleName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以管理员权限添加防火墙入站规则。返回 false 表示用户拒绝或失败。</summary>
    public static bool TryAddRule(out string message)
    {
        var exe = Environment.ProcessPath ?? "";
        var args = $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow " +
                   $"program=\"{exe}\" enable=yes profile=private,public";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
            };
            var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(15000);
            if (p is { HasExited: true } && p.ExitCode == 0)
            {
                message = "防火墙规则已添加。";
                return true;
            }
            message = p is { HasExited: true }
                ? $"添加规则失败（netsh 退出码 {p.ExitCode}）。"
                : "添加规则未完成。";
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            message = "未授予管理员权限，未添加防火墙规则。";
            return false;
        }
        catch (Exception ex)
        {
            message = "添加防火墙规则失败：" + ex.Message;
            return false;
        }
    }
}

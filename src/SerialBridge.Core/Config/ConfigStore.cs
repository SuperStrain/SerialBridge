using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialBridge.Core.Logging;
using SerialBridge.Core.Models;

namespace SerialBridge.Core.Config;

/// <summary>
/// 配置持久化：JSON 存于 %AppData%\SerialBridge\config.json（与程序目录分离，
/// 升级/重装不会丢失配置）。写入采用“临时文件 + 原子替换”，加载失败时自动备份损坏文件。
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ConfigStore(string? pathOverride = null)
    {
        _path = pathOverride ?? LogService.ConfigFilePath;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                LogX.Info("配置文件不存在，使用默认配置。");
                return new AppSettings();
            }
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null) throw new InvalidDataException("配置内容为空");
            Sanitize(settings);
            LogX.Info($"已加载配置：{settings.Mappings.Count} 条映射。");
            return settings;
        }
        catch (Exception ex)
        {
            LogX.Error($"配置文件加载失败：{ex.Message}，已备份损坏文件并使用默认配置。", ex);
            try
            {
                if (File.Exists(_path))
                    File.Copy(_path, _path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
            }
            catch { }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            settings.Version = AppSettings.CurrentVersion;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            LogX.Error($"配置保存失败：{ex.Message}", ex);
            throw;
        }
    }

    /// <summary>对加载的数据做基本合法性修复。</summary>
    private static void Sanitize(AppSettings s)
    {
        s.General ??= new GeneralSettings();
        s.General.LogRetentionDays = Math.Clamp(s.General.LogRetentionDays, 1, 365);
        s.Mappings ??= new List<MappingConfig>();
        var seenIds = new HashSet<Guid>();
        var usedPorts = new HashSet<int>();
        foreach (var m in s.Mappings.ToList())
        {
            if (m.Id == Guid.Empty || !seenIds.Add(m.Id)) m.Id = Guid.NewGuid();
            m.Name = string.IsNullOrWhiteSpace(m.Name) ? "新映射" : m.Name.Trim();
            if (m.ListenPort is < 1 or > 65535) m.ListenPort = 9001;
            if (m.MaxClients < 1) m.MaxClients = 1;
            if (m.ReconnectIntervalMs < 500) m.ReconnectIntervalMs = 3000;
            if (string.IsNullOrWhiteSpace(m.PortName) || !m.PortName.ToUpperInvariant().StartsWith("COM"))
                m.PortName = "COM3";
            if (string.IsNullOrWhiteSpace(m.ListenIp)) m.ListenIp = "0.0.0.0";
            if (!usedPorts.Add(m.ListenPort))
                m.ListenPort = FindFreePort(usedPorts); // 防止配置里端口重复导致启动互斥
        }
    }

    private static int FindFreePort(HashSet<int> used)
    {
        for (var p = 9001; p < 65535; p++)
            if (used.Add(p)) return p;
        return 40000;
    }
}

/// <summary>串口枚举工具。</summary>
public static class SerialPortInfo
{
    /// <summary>枚举系统当前可用的串口名（COM1、COM7……），排序输出。</summary>
    public static List<string> GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}

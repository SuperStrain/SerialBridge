namespace SerialBridge.Core.Models;

/// <summary>软件全局设置（与映射配置一同持久化）。</summary>
public sealed class GeneralSettings
{
    /// <summary>"Light" / "Dark" / "Auto"（跟随系统）。</summary>
    public string Theme { get; set; } = "Light";

    public bool LaunchOnStartup { get; set; }

    public bool RestoreMappingsOnStartup { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    /// <summary>true = 点击关闭按钮隐藏到托盘；false = 直接退出。</summary>
    public bool CloseToTray { get; set; }

    public int LogRetentionDays { get; set; } = 30;
}

/// <summary>整个应用的持久化根对象。</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<MappingConfig> Mappings { get; set; } = new();

    public GeneralSettings General { get; set; } = new();
}

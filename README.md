<div align="center">
  <img src="src/SerialBridge.App/Assets/app-logo.png" alt="SerialBridge Logo" width="112" />

  # SerialBridge

  **让局域网内的 AI Agent 直接访问设备串口。**

  将 Windows 上的物理串口转换为局域网内可访问的 TCP 端点，让 AI Agent、开发者和自动化脚本直接与开发板及 MCU 通信，加速设备调试与验证。

  [![Build & Release](https://github.com/SuperStrain/SerialBridge/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/SuperStrain/SerialBridge/actions/workflows/build.yml)
  [![Latest Release](https://img.shields.io/github/v/release/SuperStrain/SerialBridge?display_name=tag)](https://github.com/SuperStrain/SerialBridge/releases/latest)
  [![Downloads](https://img.shields.io/github/downloads/SuperStrain/SerialBridge/total)](https://github.com/SuperStrain/SerialBridge/releases)
  [![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](#系统要求)
  [![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

  **简体中文** · [English](README.en.md)

  [**下载最新版**](https://github.com/SuperStrain/SerialBridge/releases/latest) · [使用指南](docs/用户使用说明.md) · [反馈问题](https://github.com/SuperStrain/SerialBridge/issues) · [参与贡献](#参与贡献)
</div>

---

## ✨ 为什么选择 SerialBridge？

**项目的核心目标，是打通 AI Agent 与真实硬件之间的串口通道。** 将 USB 转串口设备连接到运行 SerialBridge 的 Windows 主机，同一局域网内的 AI Agent 即可通过 TCP 读取启动日志、发送调试命令、获取设备反馈，并根据结果继续分析或执行下一步操作。开发者不必在 AI 与串口终端之间反复复制粘贴，大幅减少设备调试、问题定位和回归验证的人工操作。

SerialBridge 同样支持普通 TCP 客户端和人工调试工具；**它提供的是透明串口通道，而不是内置 AI 模型或无需授权的自动控制系统**。

<div align="center">
  <img src="docs/assets/serialbridge-agent-workflow-zh.svg" alt="AI Agent 通过局域网 TCP 访问 SerialBridge，再经串口双向连接嵌入式开发板" width="100%" />
</div>

> **透明透传**：SerialBridge 转发原始字节，不将 TCP 流量包装成 Telnet 或其他控制协议。实际串口参数由 Windows 端映射配置决定。

## 🚀 主要功能

| 功能 | 说明 |
| --- | --- |
| 🤖 **AI Agent 访问设备** | 同一局域网内的 Agent 可通过 TCP 与设备串口交互；支持配套 Agent Skill / Python 脚本 |
| 🔌 **多路串口映射** | 一条映射对应一个 COM 端口和 TCP 监听地址；独立启动、停止和配置 |
| 🔄 **双向透明传输** | 支持任意二进制字节、单/多客户端模式、串口写入顺序化 |
| 🛡️ **断线恢复** | 串口拔插后可自动重连；端口占用检测、慢客户端保护、写入队列背压 |
| 💻 **内置串口终端** | WebView2 + xterm.js，逐键透传、文本搜索、滚动历史、字体缩放 |
| ⚡ **快捷命令与宏** | 终端底部可显示常用命令按钮；Text / HEX、CR / LF / CRLF、分步延时与取消 |
| 📊 **实时监控与日志** | HEX / ASCII、收发方向、时间戳、统计、导出及可配置日志采集 |
| 🖥️ **桌面体验** | WPF Fluent 风格、深浅主题、系统托盘、开机自启、映射恢复 |
| 📦 **安装与更新** | Windows 安装包 / 便携 ZIP、GitHub Releases 更新检查、升级保留配置 |

> 宏是**固定延时的顺序发送**，不自动等待设备 Shell 提示符或命令执行完成。详见 [快捷命令与宏说明](docs/快捷命令与宏.md)。

## 🤖 用 AI Agent 调试真实设备

例如，AI Agent 在 Linux 开发机上运行，而开发板的 USB 串口连接在另一台 Windows 电脑上：

1. 在 Windows 上启动 SerialBridge，将开发板的 `COM3` 映射到 TCP 端口 `9001`。
2. 在同一可信局域网内，让 Agent 使用普通 TCP 客户端，或使用仓库提供的 [serial-tcp-uart Agent Skill](skills/serial-tcp-uart/SKILL.md) 与 [Python 脚本](skills/serial-tcp-uart/scripts/serial_session.py) 连接。
3. Agent 可以读取日志、发送经授权的诊断命令、等待设备提示符，并结合返回结果辅助定位问题；人工仍可通过内置终端接管调试。

```bash
# 在 Agent 所在的 Linux 主机上，按实际主机 IP 修改地址
python3 skills/serial-tcp-uart/scripts/serial_session.py \
  --host 192.168.1.100 --port 9001 exec "uname -a"
```

> **边界说明：** SerialBridge 本身不推理、不执行 Agent 工作流；自动化逻辑由外部 Agent/脚本负责。串口是否需要登录、提示符如何匹配以及命令权限，都取决于目标设备和 Agent 配置。固件刷写、重启等改变设备状态的操作应先获得明确授权。

## 📥 安装

前往 [**GitHub Releases**](https://github.com/SuperStrain/SerialBridge/releases/latest)，选择适合你的版本：

| 版本 | 适用场景 |
| --- | --- |
| `SerialBridge-Setup-<版本>-x64.exe` | **推荐**：安装、桌面与开始菜单快捷方式、卸载入口 |
| `SerialBridge-Portable-<版本>-win-x64.zip` | 便携版：解压后运行 `SerialBridge.exe` |

### 系统要求

- **Windows 10 1809 或更新版本 / Windows 11，x64**（项目目标平台；低版本 Windows 兼容性请以实际测试为准）
- 需要一个可用的物理 COM 端口；USB 转串口适配器可能需要厂商驱动
- 自包含发布，**无需另外安装 .NET Runtime**
- 内置终端使用 Microsoft Edge **WebView2 Runtime**；缺失时需安装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

## ⚡ 三步开始

**示例：把本机 COM3 暴露为 TCP 9001。**

1. 运行 SerialBridge，点击 **新增映射**，选择 `COM3`，设置波特率（例如 `115200 8N1`）。
2. 将监听端口设为 `9001`；如需允许局域网设备接入，选择合适的监听网卡/IP，保存后点击 **启动**。
3. 在另一台设备上，连接 SerialBridge 显示的 **Windows 主机局域网 IP + 9001**。

```text
Windows：COM3 @ 115200 8N1  ⇄  0.0.0.0:9001（监听）
Linux：  连接 192.168.1.100:9001（示例地址）
```

`0.0.0.0` 是监听地址，**不是客户端应填写的目标地址**。请使用软件界面显示的实际主机 IP，并检查 Windows 防火墙设置。

### 从 Linux 进行交互式调试

推荐使用 `socat` 建立本地 PTY，再用 `picocom` 获得串口式逐键交互（包括 Tab、Ctrl+C 和方向键）：

```bash
sudo apt install socat picocom
socat PTY,link=/tmp/ttydev,waitslave TCP:192.168.1.100:9001 &
picocom /tmp/ttydev
```

结束后关闭 `picocom` 并清理对应 `socat` 进程。更完整的连接方式和排查说明见 [用户指南](docs/用户使用说明.md)。

### 在终端中快捷发送命令

打开对应映射的 **串口终端** → 点击 **快捷命令 / 宏** → **编辑**，创建例如 `uname -a` 的 Text 步骤，选择 `CR` 后保存。以后可直接点击终端底部按钮发送。多步骤宏可设置每步延时，运行时可取消**尚未发送**的步骤。

## 🏗️ 技术架构

```mermaid
flowchart TB
    subgraph APP["SerialBridge.App · C# / WPF / MVVM"]
        direction LR
        UI["映射管理 / 设置"]
        TERM["串口终端<br/>WebView2 + xterm.js"]
        MON["数据监控 / 日志展示"]
        MACRO["快捷命令 / 宏"]
        TRAY["托盘 / 自动更新"]
    end

    subgraph CORE["SerialBridge.Core · .NET 8"]
        direction TB
        MANAGER["MappingManager<br/>映射与生命周期"]
        RUNTIME["MappingRuntime<br/>双向数据转发"]
        SERIAL["SerialPortDevice"]
        TCP["TcpServerService"]
        SUPPORT["ConfigStore · Capture · Logging"]
        MANAGER --> RUNTIME
        RUNTIME <--> SERIAL
        RUNTIME <--> TCP
        SUPPORT -.-> RUNTIME
    end

    UI --> MANAGER
    TERM <--> RUNTIME
    MON -.-> RUNTIME
    MACRO --> RUNTIME
    TRAY -.-> MANAGER
    SERIAL <--> DEVICE["物理串口 / 开发板"]
    TCP <--> CLIENT["局域网 TCP 客户端 / AI Agent"]

    classDef app fill:#e8f0ff,stroke:#477dd6,color:#123d81
    classDef core fill:#eff6ff,stroke:#6c97c9,color:#15375d
    classDef edge fill:#e7f7ee,stroke:#3a9e72,color:#185c3e
    class UI,TERM,MON,MACRO,TRAY app
    class MANAGER,RUNTIME,SERIAL,TCP,SUPPORT core
    class DEVICE,CLIENT edge
```

- **语言与框架：** C#、.NET 8、WPF、WPF-UI、CommunityToolkit.Mvvm
- **串口与网络：** `System.IO.Ports`、`TcpListener` / Socket、`System.Threading.Channels`
- **终端：** WebView2 + xterm.js
- **测试与发布：** xUnit、模拟串口、GitHub Actions、Inno Setup

## 🛠️ 从源码构建

在 Windows 上安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)、Python 3；如需制作安装包，再安装 Inno Setup 6.5+。

```powershell
git clone https://github.com/SuperStrain/SerialBridge.git
cd SerialBridge

# 编译和单元测试
dotnet test SerialBridge.sln -c Debug --nologo

# 发布自包含 Windows x64 程序
dotnet publish src/SerialBridge.App -c Release -r win-x64 --self-contained true -o dist/publish

# 如需完整匹配 CI 的图标资源处理和安装包流程，请参见构建文档
```

详细说明：[开发者编译与打包指南](docs/编译与打包说明.md) · [GitHub Actions](https://github.com/SuperStrain/SerialBridge/actions)

## 📚 文档与资源

| 资源 | 内容 |
| --- | --- |
| [用户使用指南](docs/用户使用说明.md) | 映射配置、Windows / Linux 连接与常见问题 |
| [快捷命令与宏](docs/快捷命令与宏.md) | 终端命令栏、Text / HEX、延时和安全边界 |
| [开发者构建指南](docs/编译与打包说明.md) | 环境、编译、安装包与发布 |
| [测试记录与已知限制](docs/测试记录.md) | 测试范围、问题及手工验证事项 |
| [serial-tcp-uart Agent Skill](skills/serial-tcp-uart/SKILL.md) | 供自动化代理通过 TCP 操作串口的技能与脚本 |

## 🔐 安全须知

**原始 TCP 桥接默认没有加密、身份认证或 Telnet 协议处理。** 建议只在可信局域网中使用，配置合适的监听地址和 Windows 防火墙规则，**不要直接暴露到公网**。

多客户端模式下，串口数据会向多个客户端广播，写入按队列序列化，但不同客户端的请求与响应**不会自动配对**。快捷宏若含有密码，当前会写入本地配置文件，建议不要保存真实敏感凭据。

## 🤝 参与贡献

欢迎提交 [Issue](https://github.com/SuperStrain/SerialBridge/issues) 或 [Pull Request](https://github.com/SuperStrain/SerialBridge/pulls)：

1. 创建功能分支并保持改动范围清晰。
2. 运行 `dotnet test SerialBridge.sln -c Debug`。
3. 描述变更目的、测试方式，以及涉及串口/网络行为的兼容性影响。

如果这个项目对你有帮助，欢迎为仓库点一颗 ⭐。

## 📄 开源许可

基于 [MIT License](LICENSE) 开源。

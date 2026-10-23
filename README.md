# SerialBridge 串口网络桥接工具

**简体中文** | [English](README.en.md)

Windows 桌面软件：把本机物理串口（COM 口，含 USB 转串口）桥接到 TCP 监听端口，实现**双向透明数据转发**。局域网设备连接 `主机IP:端口` 即可与串口设备通信，无需驱动、无需互联网。

![技术栈](https://img.shields.io/badge/.NET-8.0_LTS-blue) ![UI](https://img.shields.io/badge/WPF-Fluent_Design-2563EB) ![测试](https://img.shields.io/badge/unit_tests-14%2F14-brightgreen) ![协议](https://img.shields.io/badge/license-MIT-green)

## 核心特性

- **多串口独立映射**：COM3→9001、COM4→9002……各映射独立启停、互不影响
- **TCP 双向透明透传**：任意二进制数据原样转发，保证顺序，支持 0x00 等特殊字节
- **单/多客户端模式**：单客户端拒绝后来者；多客户端广播 + 写串口串行化
- **异常自愈**：串口意外断开自动重连（间隔可配）、慢速客户端保护、端口冲突检测
- **实时监控**：HEX/ASCII 双模式、方向颜色、时间戳、统计、暂停/清空/导出
- **现代 UI**：WPF-UI Fluent Design、深浅主题、DPI 适配、系统托盘
- **完整安装体验**：Inno Setup 中文安装包，升级保留配置，卸载可选拆除

## 快速开始

1. 运行 `dist/installer/SerialBridge-Setup-1.0.0-x64.exe` 安装
2. 新增映射：选择串口 → 设置波特率 → 设置 TCP 端口 → 启动
3. 局域网设备连接主界面显示的 `局域网IP:端口` 开始通信

## 构建

```bash
dotnet test tests/SerialBridge.Core.Tests                              # 单元测试（无需硬件）
dotnet publish src/SerialBridge.App -c Release -r win-x64 --self-contained true -o dist/publish
ISCC.exe installer/SerialBridge.iss                                    # 安装包
```

## 文档

- [用户使用说明](docs/用户使用说明.md)
- [开发者编译与打包说明](docs/编译与打包说明.md)
- [测试记录与已知问题](docs/测试记录.md)

## 技术架构

```
┌─────────────── SerialBridge.App (WPF, MVVM) ───────────────┐
│  主界面仪表盘 │ 映射编辑 │ 数据监控 │ 设置 │ 系统托盘        │
└──────────────────────────┬─────────────────────────────────┘
┌──────────────────────────┴──── SerialBridge.Core ───────────┐
│  MappingManager（互斥检查/生命周期）                          │
│  MappingRuntime（双向桥接 + 统计 + 监控事件流）                │
│  SerialPortDevice        TcpServerService                   │
│  （会话循环/重连/写串行化）（监听/客户端管理/发送队列）        │
│  ConfigStore(JSON)  Serilog  NetworkInfo                     │
└─────────────────────────────────────────────────────────────┘
```

- 开发语言：C# / .NET 8 LTS
- 串口：System.IO.Ports　网络：TcpListener/Socket（全异步）
- UI：WPF + WPF-UI 4.3（Fluent Design）+ CommunityToolkit.Mvvm
- 测试：xUnit + 假串口设备（内存 Pipe），无需硬件即可回归
- 安装：Inno Setup 6，self-contained 部署

## 开源协议

本项目以 [MIT License](LICENSE) 协议开源，欢迎 Star、提交 Issue 与 Pull Request。

## 安全提示

原始 TCP 透传不包含加密与身份认证，请仅在可信局域网使用；可通过监听地址限定网卡、防火墙规则控制访问范围。

# SerialBridge — Serial-to-TCP Bridge

[简体中文](README.md) | **English**

SerialBridge is a Windows desktop application that bridges local physical serial ports (COM ports, including USB-to-serial adapters) to TCP listening ports for **transparent, bidirectional data transfer**. Devices on the local network can connect to `host-IP:port` to communicate with the serial device, without extra client-side serial drivers or internet access.

![.NET](https://img.shields.io/badge/.NET-8.0_LTS-blue) ![UI](https://img.shields.io/badge/WPF-Fluent_Design-2563EB) ![License](https://img.shields.io/badge/license-MIT-green)

## Key Features

- **Independent serial mappings:** Map COM3 → TCP 9001, COM4 → TCP 9002, and so on. Start and stop mappings independently.
- **Transparent two-way TCP bridging:** Forward binary data without intentional transformation, including special bytes such as `0x00`, while preserving byte order.
- **Single- and multi-client modes:** Reject additional connections in single-client mode; support broadcasting to multiple clients and serialized writes to the serial port in multi-client mode.
- **Recovery and protection:** Configurable automatic reconnection after serial disconnection, slow-client handling, and TCP port conflict detection.
- **Live monitoring:** HEX/ASCII views, direction indicators, timestamps, statistics, pause, clear, and export.
- **Modern desktop UI:** WPF-UI Fluent Design, light/dark themes, DPI awareness, and a system tray icon.
- **Windows installer:** Inno Setup installer, with settings preserved during upgrades and optional cleanup when uninstalling.

## Quick Start

1. Download the latest installer from [GitHub Releases](https://github.com/SuperStrain/SerialBridge/releases) and run `SerialBridge-Setup-<version>-x64.exe`.
2. Add a mapping: select a COM port, configure the baud rate and TCP listening port, then start the mapping.
3. From another device on the LAN, connect to the `LAN-IP:port` shown by SerialBridge.

## Build

Build on Windows with the .NET 8 SDK and Inno Setup 6:

```powershell
dotnet test tests/SerialBridge.Core.Tests
dotnet publish src/SerialBridge.App -c Release -r win-x64 --self-contained true -o dist/publish
ISCC.exe installer/SerialBridge.iss
```

The core unit tests can run without physical serial hardware. The full desktop application and installer target Windows.

## Documentation

The following documentation is currently maintained in Chinese:

- [User guide](docs/用户使用说明.md)
- [Developer build and packaging guide](docs/编译与打包说明.md)
- [Test records and known issues](docs/测试记录.md)

## Architecture

```text
SerialBridge.App (WPF + MVVM)
  Dashboard | Mapping editor | Live monitor | Settings | System tray
                        |
SerialBridge.Core
  MappingManager (mapping conflicts and lifecycle)
  MappingRuntime (bidirectional bridging, statistics, monitoring events)
  SerialPortDevice | TcpServerService
  ConfigStore (JSON) | Logging | Network information
```

- **Language/runtime:** C# / .NET 8 LTS
- **Serial/network:** `System.IO.Ports`, `TcpListener` / `Socket` (asynchronous I/O)
- **Desktop UI:** WPF, WPF-UI 4.3 (Fluent Design), CommunityToolkit.Mvvm
- **Testing:** xUnit with simulated in-memory serial devices
- **Packaging:** Inno Setup 6; self-contained Windows deployment

## License

Released under the [MIT License](LICENSE). Issues, pull requests, and stars are welcome.

## Security

Raw TCP forwarding provides **no built-in encryption or authentication**. Use SerialBridge only on trusted networks, and restrict access using the configured listening interface and host firewall rules.

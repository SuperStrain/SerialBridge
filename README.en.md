<div align="center">
  <img src="src/SerialBridge.App/Assets/app-logo.png" alt="SerialBridge logo" width="112" />

  # SerialBridge

  **Your serial ports, available across the network.**

  A lightweight Windows serial ↔ TCP bridge with independent mappings, transparent bidirectional forwarding, a built-in terminal, and one-click commands.

  [![Build & Release](https://github.com/SuperStrain/SerialBridge/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/SuperStrain/SerialBridge/actions/workflows/build.yml)
  [![Latest Release](https://img.shields.io/github/v/release/SuperStrain/SerialBridge?display_name=tag)](https://github.com/SuperStrain/SerialBridge/releases/latest)
  [![Downloads](https://img.shields.io/github/downloads/SuperStrain/SerialBridge/total)](https://github.com/SuperStrain/SerialBridge/releases)
  [![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)](#requirements)
  [![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

  [简体中文](README.md) · **English**

  [**Download latest**](https://github.com/SuperStrain/SerialBridge/releases/latest) · [User guide](docs/用户使用说明.md) · [Report an issue](https://github.com/SuperStrain/SerialBridge/issues) · [Contribute](#contributing)
</div>

---

## ✨ Why SerialBridge?

Stop requiring every machine to have direct access to a USB-to-serial adapter. Connect the adapter to a Windows computer running SerialBridge, then reach the physical serial device using a regular TCP connection. Useful for **embedded Linux development, MCU debugging, serial log collection, and remote maintenance on a LAN**.

```text
     Linux / macOS / Windows / scripts
                   │
              TCP client
                   │  192.168.1.100:9001
                   ▼
       ┌───────────────────────┐
       │ SerialBridge (Windows)│
       │ TCP :9001 ⇄ COM3      │
       │ TCP :9002 ⇄ COM4      │
       └───────────┬───────────┘
                   │ USB / UART
                   ▼
          Board / MCU / Device
```

> **Transparent forwarding:** SerialBridge transfers raw bytes. It does not wrap connections in Telnet or another control protocol. Serial port settings are configured on the Windows host.

## 🚀 Features

| Feature | What it does |
| --- | --- |
| 🔌 **Independent mappings** | Map each COM port to its own TCP listening endpoint; start and stop mappings individually |
| 🔄 **Bidirectional raw transfer** | Forward arbitrary binary data with single- or multi-client modes and serialized serial writes |
| 🛡️ **Connection recovery** | Configurable serial reconnection, occupied-port detection, slow-client protection, and bounded queues |
| 💻 **Built-in terminal** | WebView2 + xterm.js with direct keystroke forwarding, search, scrollback, and font zoom |
| ⚡ **Quick commands & macros** | Optional terminal command bar, Text / HEX presets, CR / LF / CRLF, step delays, and cancellation |
| 📊 **Traffic monitoring & logs** | HEX / ASCII views, direction, timestamps, counters, export, and configurable capture |
| 🖥️ **Windows desktop UX** | Fluent-styled WPF, light/dark theme, system tray, startup and mapping restoration |
| 📦 **Installation & updates** | Windows installer or portable ZIP, GitHub Releases update checks, settings preserved on upgrade |

> Macros send steps sequentially with **fixed delays**; they do **not** wait for a remote shell prompt or command completion. See [Quick commands & macros](docs/快捷命令与宏.md) (Chinese).

## 📥 Install

Visit [**GitHub Releases**](https://github.com/SuperStrain/SerialBridge/releases/latest) and download either:

| Package | Recommended use |
| --- | --- |
| `SerialBridge-Setup-<version>-x64.exe` | **Recommended** — guided installation, shortcuts, and uninstall entry |
| `SerialBridge-Portable-<version>-win-x64.zip` | Portable — extract and run `SerialBridge.exe` |

### Requirements

- **Windows 10 1809 or newer / Windows 11, x64** (the targeted platform; compatibility with older Windows installations should be verified on real machines)
- An available COM port; USB-to-serial adapters may require their vendor-supplied drivers
- Self-contained deployment: **no separate .NET Runtime installation required**
- The embedded terminal uses **Microsoft Edge WebView2 Runtime**; install [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) if it is missing

## ⚡ Quick start

**Example: expose your Windows machine's COM3 over TCP port 9001.**

1. Open SerialBridge, select **Add Mapping**, choose `COM3`, and set the serial parameters (for example, `115200 8N1`).
2. Set the TCP port to `9001`, select a suitable listening interface/address for LAN access, save, and **Start** the mapping.
3. Connect from another device using the **Windows host's actual LAN IP** and port `9001`, as shown in the app.

```text
Windows: COM3 @ 115200 8N1  ⇄  0.0.0.0:9001 (listen)
Linux:   connect to 192.168.1.100:9001 (example IP)
```

`0.0.0.0` means “listen on all interfaces”; it is **not** a destination IP. Use the real host address and check Windows Firewall rules.

### Interactive access from Linux

For terminal-like keystroke handling (Tab completion, Ctrl+C, arrows), use `socat` to create a local PTY and open it with `picocom`:

```bash
sudo apt install socat picocom
socat PTY,link=/tmp/ttydev,waitslave TCP:192.168.1.100:9001 &
picocom /tmp/ttydev
```

Close `picocom` and clean up the matching `socat` process afterward. See the [user guide](docs/用户使用说明.md#10-从-linux-主机访问ubuntu-等) (Chinese) for additional connection options and troubleshooting.

### One-click terminal commands

Open the mapping's **Serial Terminal** → **Quick Commands / Macros** → **Edit**. Create a Text step such as `uname -a` with a `CR` ending, save it, and use the command bar button to send it again. Multi-step macros support per-step delays and cancellation of **unsent** steps.

## 🏗️ Architecture

```text
┌────────────────────────────────────────────────┐
│ SerialBridge.App  ·  C# / WPF / MVVM            │
│ Mappings · Terminal (WebView2 + xterm.js)       │
│ Monitor · Macros · Tray · Updates               │
└───────────────────────┬────────────────────────┘
                        │
┌───────────────────────▼────────────────────────┐
│ SerialBridge.Core  ·  .NET 8                   │
│ MappingManager / MappingRuntime                │
│ SerialPortDevice ⇄ bounded queues ⇄ TCP server │
│ ConfigStore · Capture · Logging                │
└────────────────────────────────────────────────┘
```

- **Language & UI:** C#, .NET 8, WPF, WPF-UI, CommunityToolkit.Mvvm
- **Serial & networking:** `System.IO.Ports`, `TcpListener` / Socket, `System.Threading.Channels`
- **Terminal:** WebView2 + xterm.js
- **Testing & packaging:** xUnit, simulated serial streams, GitHub Actions, Inno Setup

## 🛠️ Build from source

On Windows, install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Python 3. Install Inno Setup 6.5+ if you also want to build the installer.

```powershell
git clone https://github.com/SuperStrain/SerialBridge.git
cd SerialBridge

# Build and run tests
dotnet test SerialBridge.sln -c Debug --nologo

# Publish a self-contained Windows x64 application
dotnet publish src/SerialBridge.App -c Release -r win-x64 --self-contained true -o dist/publish

# See the build guide for icon processing and the full installer workflow
```

See the [developer build & packaging guide](docs/编译与打包说明.md) (Chinese) and [GitHub Actions](https://github.com/SuperStrain/SerialBridge/actions).

## 📚 Documentation & resources

| Resource | Description |
| --- | --- |
| [User guide](docs/用户使用说明.md) | Mapping configuration, Windows/Linux connections, troubleshooting (Chinese) |
| [Quick commands & macros](docs/快捷命令与宏.md) | Command bar, Text / HEX, delays, and safety boundaries (Chinese) |
| [Developer build guide](docs/编译与打包说明.md) | Toolchain, builds, packaging and publishing (Chinese) |
| [Tests & known limitations](docs/测试记录.md) | Test history and manual verification notes (Chinese) |
| [serial-tcp-uart Agent Skill](skills/serial-tcp-uart/SKILL.md) | Agent-oriented skill and scripts for using serial mappings over TCP (Chinese) |

## 🔐 Security

**Raw TCP forwarding has no built-in encryption, authentication, or Telnet negotiation.** Use SerialBridge on trusted networks only, restrict the listening address and host firewall rules, and **do not expose the bridge directly to the public Internet**.

In multi-client mode, serial data is broadcast to connected clients and inbound writes are serialized, but requests and responses from different clients are **not automatically correlated**. Passwords in quick-command presets are currently saved in local configuration; avoid storing sensitive real credentials.

## 🤝 Contributing

Issues and pull requests are welcome:

1. Create a focused feature branch.
2. Run `dotnet test SerialBridge.sln -c Debug`.
3. Explain the purpose, testing, and any effects on serial/network compatibility.

If SerialBridge helps your workflow, consider giving the repository a ⭐.

## 📄 License

Licensed under the [MIT License](LICENSE).

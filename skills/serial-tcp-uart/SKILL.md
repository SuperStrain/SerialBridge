---
name: serial-tcp-uart
description: >
  通过 TCP 端口访问 SerialBridge 串口映射并与物理串口设备（嵌入式 Linux 开发板、
  单片机、调试串口）交互。当需要向串口设备发送命令、读取设备输出、调试嵌入式系统、
  打断/重启设备服务，或用户提到"串口"、"UART"、"调试串口"、"SerialBridge"、
  "通过 TCP/端口访问设备"、"板子"、"COM 口映射"时使用——即使只说"连一下设备看看"
  或"帮我看看设备状态"也应触发。
---

# 通过 TCP 访问串口设备（SerialBridge）

Windows 主机上的 SerialBridge 把物理串口映射为 TCP 监听端口（如 `192.168.x.x:9001`），
对该端口的 TCP 连接就是对串口设备的双向字节级透传。本技能用于**局域网内 Linux 主机**以
agent 身份可靠地与设备交互。

## 连接前必读的纪律（违反会导致交互异常）

1. **端口可能被独占**。映射默认为单客户端模式：已有客户端在线时，新连接会被服务端
   立即关闭（表现为 connect 成功但马上 EOF）。遇到这种情况先确认是谁在用（人工的
   nc/screen 会话、其他 agent），协调释放后再连；不要反复重连硬抢。
2. **用完立即断开**。你占着连接，人工就无法同时调试。
3. **TCP 是字节流，没有消息边界**。设备响应的"完成"以**提示符重现**为准（如 `~ # `、
   `# `、`$ `），而不是"recv 过一次"或"等了固定秒数"。逐字等待提示符是本技能脚本的
   内置行为，自己写代码时也必须如此。
4. **不要用交互式终端工具做 agent 交互**：
   - `nc` 的 stdin 是行缓冲模式——Tab、Ctrl+C 会被本地终端攒住直到回车，设备收不到
     实时按键；
   - `telnet` 会解释 0xFF（IAC）字节并改写行尾，二进制协议必坏；
   - agent 交互一律用下面的脚本方式（raw socket 无上述问题）。

## 首选方式：scripts/serial_session.py

配套脚本位于本技能目录 `scripts/serial_session.py`，仅依赖 Python 3 标准库。
它处理了占用检测与自动重试、登录流程、提示符等待、命令回显剥离、超时保护。

### 库方式（在自己的 python 代码中）

```python
import sys
sys.path.insert(0, "<技能目录>/scripts")   # 按实际路径调整
from serial_session import SerialTcpSession

s = SerialTcpSession("192.168.1.100", 9001,
                     login="root", password="123456")   # 免登录设备省略后两项
if not s.connect():
    raise RuntimeError("端口被占用：先协调对端断开，或改用多客户端模式")
try:
    print(s.exec_command("uname -a"))          # 返回已剥离回显/提示符的输出
    print(s.exec_command("ls /"))
finally:
    s.close()                                  # 一定释放独占
```

### CLI 方式（一次性任务、shell 管道）

```bash
python3 serial_session.py --host 192.168.1.100 --port 9001 exec "uname -a"
python3 serial_session.py --host 192.168.1.100 --port 9001 \
    --login root --password 123456 exec "cat /proc/version"
python3 serial_session.py --host 192.168.1.100 --port 9001 \
    --prompt '(buildroot@.* [#] )' exec "ls /etc"      # 提示符非默认样式时显式给出
python3 serial_session.py --host 192.168.1.100 --port 9001 raw --hex "4154 0d" --wait 2
```

退出码：0 成功；2 连接失败；3 端口被占用；4 等提示符超时。

### 提示符说明（决定 exec_command 是否可靠）

脚本默认匹配 `# `、`$ `、`~ # `、`~ $ ` 结尾的行。设备提示符若是其他样式
（如 buildroot 的 `(buildroot@xxx) # ` 会被默认规则命中；自定义的 `myboard>` 则不会），
必须用 `--prompt` 传入正则。确认方法：先发一条回显明显的命令，观察响应的结尾样式，
再据此构造正则。若某条命令的输出恰好包含与提示符同型的文本导致提前返回，
改用更长超时 + 更精确的提示符正则，或拆分命令。

## 常用配方

- **打断卡死的程序**：`s.send_raw(b"\x03")`（Ctrl+C），随后再发命令。
- **监听设备主动上报**（不经询问的日志/事件）：`s.read_available(seconds)`。
- **发送二进制帧**：`s.send_raw(bytes.fromhex("41540d"))`，然后用
  `read_available()` 或带正则的读取收响应。
- **多条命令**：同一会话内依次 `exec_command`；每条之间无需人为延时，提示符等待
  自带同步。改变设备状态的命令（重启服务、reboot）后提示符可能暂时消失——用
  `--timeout` 放宽或捕获 `PromptTimeoutError` 后重试探测。

## 人工交互场景（给人类用户，不是给 agent）

需要人盯着看时，把以下命令告诉用户（不要自己起交互终端）：

```bash
socat PTY,link=/tmp/ttydev,waitslave TCP:192.168.1.100:9001 &   # PTY 方式
picocom /tmp/ttydev                             # 与真实串口体验一致
```

并提醒用户**不要用交互式终端工具直连 TCP**：`screen //telnet`（实测 Tab 失灵——
其 telnet 协议栈与哑透传对端不匹配）、`nc`（行缓冲，Tab/Ctrl+C 攒到回车才发送）、
`telnet`（IAC 字节破坏二进制数据）。这些都已在真实设备上验证过。

## 故障排查

| 现象 | 原因与处理 |
|---|---|
| connect 返回 False / 退出码 3 | 端口被占用：协调对端断开，或在 SerialBridge 映射配置改为多客户端模式 |
| connect 抛"无法连接" | 映射未启动、主机 IP 错误、端口错误或防火墙拦截；用 `nc -zv <ip> <port>` 验证端口可达性 |
| exec_command 超时（退出码 4） | 提示符样式不匹配（用 `--prompt` 指定）；命令本身卡住（先发 Ctrl+C）；或输出量巨大（加大 timeout） |
| 收到的响应不完整 | 提示符等待提前命中（输出里含提示符样式文本），改用更精确的提示符正则 |
| 用户报告按键失灵（Tab/Ctrl/Ctrl+C 无响应） | 先用脚本排除链路问题：`send_raw(b"\t")` 后 `read_available(1.5)`，若设备回发响应则链路正常，问题出在用户所用的终端工具（nc 行缓冲、screen //telnet 协议不匹配），建议改用 socat PTY + picocom；若设备无响应，再查映射状态与设备本身 |
| Windows 侧看不到流量 | 确认 SerialBridge 主界面该映射为"运行中"且串口已连接 |

## 部署到 Linux 主机

把本技能整个目录拷贝到目标 agent 的技能目录即可（以 ZCode 为例）：

```bash
cp -r serial-tcp-uart ~/.agents/skills/    # 用户级
# 或 <项目>/.agents/skills/                # 项目级
```

SKILL.md 与 scripts/ 需在同一目录，脚本通过相对技能目录引用。

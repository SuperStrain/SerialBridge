---
name: serial-tcp-uart
description: >
  通过 TCP 端口访问 SerialBridge 串口映射并与物理串口设备（嵌入式 Linux 开发板、
  单片机、调试串口）交互。当需要向串口设备发送命令、读取设备输出、调试嵌入式系统、
  或排查嵌入式设备的串口故障时使用。仅在用户希望实际通过 SerialBridge 的 TCP
  映射连接、读取或操作设备时触发；纯 UART 原理咨询不需要使用本技能。
---

# 通过 TCP 访问串口设备（SerialBridge）

Windows 主机上的 SerialBridge 把物理串口映射为 TCP 监听端口（如 `192.168.x.x:9001`），
对该端口的 TCP 连接就是对串口设备的双向字节级透传。本技能用于**局域网内 Linux 主机**以
agent 身份可靠地与设备交互。

## 连接前必读的纪律（违反会导致交互异常）

1. **操作权限**。默认仅执行读取状态、日志等无副作用命令。重启、停止服务、修改配置、写入 Flash、发送控制字节等会改变设备状态的操作，先获得用户明确同意。不要在命令中写入或回显明文密码。
2. **端口可能被独占**。映射默认为单客户端模式：已有客户端在线时，新连接会被服务端
   立即关闭（表现为 connect 成功但马上 EOF）。遇到这种情况先确认是谁在用（人工的
   nc/screen 会话、其他 agent），协调释放后再连；不要反复重连硬抢。
3. **用完立即断开**。你占着连接，人工就无法同时调试。
4. **TCP 是字节流，没有消息边界**。不要假设一次 `recv` 对应一条设备响应。已确认进入
   POSIX/Linux Shell 时，优先使用 `completion="shell"` 随机开始/结束标记协议；它不依赖
   提示符或 TCP 分块边界。U-Boot、MCU CLI 或其他非 Shell 终端继续使用默认
   `completion="prompt"` 模式，不得把 Shell 包装命令发送给这些终端。
5. **不要用交互式终端工具做 Agent 交互**：
   - `nc` 的 stdin 是行缓冲模式——Tab、Ctrl+C 等按键可能被本地终端缓存到回车后才发送，设备收不到实时按键；
   - `telnet` 会解释 0xFF（IAC）字节并改写行尾，二进制协议必坏；
   - Agent 交互一律用下面的脚本方式（raw socket 无上述问题）。

## 首选方式：scripts/serial_session.py

配套脚本位于本技能目录 `scripts/serial_session.py`，仅依赖 Python 3 标准库。
它处理 TCP 断开检测与有限重试、显式登录、提示符兼容模式，以及 Linux Shell 的随机标记命令完成协议。Prompt 和 Shell 两种命令模式都遵守 `max_capture_bytes` 输出上限，超限时返回首尾预览并设置 `output_truncated=True`；完整命令输出文件采集仍仅支持 Shell 模式。

无数据不等于端口被占用。登录设备应显式配置 `--login`，连接成功也不代表已确认 Shell 就绪。

未主动消费的串口数据会保存在会话缓存，可通过 `read_available()` 读取。

### 库方式（在自己的 python 代码中）

```python
import sys
sys.path.insert(0, "<技能目录>/scripts")   # 按实际路径调整
from serial_session import SerialTcpSession

s = SerialTcpSession("192.168.1.100", 9001,
                     login="root", password="123456")
if not s.connect():
    raise RuntimeError("端口被占用：先协调对端断开，或改用多客户端模式")
try:
    # 仅在确认设备已进入 POSIX/Linux Shell 后使用 shell 模式
    result = s.exec_command_result("uname -a", completion="shell")
    print(result.output)
    if result.exit_code != 0:
        print("命令已完成，但 Shell 返回非零退出码:", result.exit_code)

    # U-Boot / MCU CLI / 非 Shell 终端继续使用提示符兼容模式
    print(s.exec_command("version"))
finally:
    s.close()                                  # 一定释放独占
```

### CLI 方式（一次性任务、shell 管道）

```bash
# 已确认的 Linux Shell：不依赖提示符，自动返回真实 Shell 退出码
python3 serial_session.py --host 192.168.1.100 --port 9001 --completion shell exec "uname -a"

# 高频日志 / 大输出：完整内容写文件，Agent 只接收有限首尾预览
python3 serial_session.py --host 192.168.1.100 --port 9001 \
    --completion shell --capture /tmp/uart-command.log --max-output-bytes 65536 \
    exec "cat /var/log/messages"

# 非 Shell 终端仍使用默认提示符模式
python3 serial_session.py --host 192.168.1.100 --port 9001 \
    --prompt '(buildroot@.* [#] )' exec "version"
python3 serial_session.py --host 192.168.1.100 --port 9001 raw --hex "4154 0d" --wait 2

# Raw / 二进制响应或长时间串口采集：完整接收数据保存到文件
python3 serial_session.py --host 192.168.1.100 --port 9001 \
    --capture /tmp/uart-raw.bin --max-output-bytes 65536 \
    raw --hex "4154 0d" --wait 30
```

CLI 退出码：0 表示交互协议完成（Shell 命令自身的退出码单独打印到 stderr）；2 连接、发送或输入错误；3 发现连接被立即拒绝（疑似端口被占用）；4 等待命令完成标记/提示符超时。库调用请使用 `exec_command_result()` 检查 `result.exit_code`；`None` 表示提示符模式无法获知 Shell 退出码。

### 命令完成协议与提示符说明

**已确认的 Linux / POSIX Shell：优先使用 Shell 标记模式。**

```python
result = s.exec_command_result("cat /proc/meminfo | head -n 3", completion="shell")
print(result.output)
print("exit code:", result.exit_code)
```

Shell 模式会为每条命令生成独立随机标记，在命令前后输出标记并捕获退出码。整个协议位于一个外层 Shell 复合命令中，避免交互式 Shell 在标记之间插入 PS1/PS2 提示符；用户命令放在内层花括号组中执行，保留当前 Shell 状态（例如 `cd`）。随机标记不会与普通命令回显或日志内容混淆。提示符后紧跟异步日志、日志与响应同一 TCP 数据块到达、标记跨多个 TCP 分块到达，都不会影响完成识别。

- 仅适用于已确认的 POSIX/Linux Shell（如 BusyBox ash、bash 等）。
- 不适用于 U-Boot 命令行、MCU CLI、AT 命令终端或未知交互协议；这些继续用默认 `completion="prompt"`。
- `CommandResult.exit_code` 是 Shell 命令退出码。非零表示命令已完成但执行失败，不等于 TCP/串口传输失败。
- Prompt 和 Shell 模式默认最多在内存中保留 1 MiB 命令输出预览，均可通过 `max_capture_bytes` / `--max-output-bytes` 调整。超过上限时返回首尾预览并设置 `output_truncated=True`；Prompt 模式不支持完整输出文件采集。Shell 模式设置 `capture_path` 可将完整命令输出分块写入本地文件。若本地磁盘写入失败，完成标记到达前会锁定会话，避免在命令状态未知时继续发送。
- 超时或发送阶段失败意味着命令可能已经执行。脚本会将会话标记为状态不确定并阻止继续发送命令；不要直接重发有副作用的命令。先保留异常中的 `raw_tail` / capture 文件，再重新连接并人工确认设备状态。
- Shell 模式使用随机标记而不是提示符，因此不需要为持续日志增大 timeout，也不要求串口先安静下来。

**非 Shell 终端 / 兼容模式：**

默认 `completion="prompt"` 继续按提示符判断。默认规则支持常见的 `# `、`$ `、`root@host:~# `、`[root@host ~]# ` 等形式，也允许已知日志级别（如 `WRN:`、`ERR:`）紧接在提示符后面。Prompt 模式同样受 `max_capture_bytes` 限制，超限时保留输出首尾预览并设置 `output_truncated=True`，但不支持通过 `capture_path` 保存完整命令输出。提示符识别仍是启发式方式，无法像 Shell 标记协议一样可靠地区分任意日志与提示符。

如果设备提示符是其他样式（例如 `myboard>`），用 `--prompt` 指定正则。非 Shell 模式超时同样表示“未确认完成”，不表示命令没有执行；会话会被锁定，需重新连接并人工确认，不能直接重发。

## 常用配方

- **打断卡死的程序**：`s.send_raw(b"\x03")`（Ctrl+C），随后再发命令。
- **监听设备主动上报**（不经询问的日志/事件）：`s.read_available(seconds, max_bytes=65536, capture_path="/tmp/uart.log")`。返回值是有界首尾预览；完整内容写入文件时通过 `last_read_bytes`、`last_read_truncated` 检查采集量及是否截断。
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
| Shell 模式等待完成标记超时 | 命令可能已经执行但结束标记未收到；查看异常的 `raw_tail` / capture 文件，禁止直接重发；重新连接并人工确认设备状态 |
| 非 Shell 模式等待提示符超时 | 检查提示符正则；提示符后紧跟异步日志时默认规则会尝试识别常见日志级别。超时后不要重发命令，重新连接并人工同步 |
| 收到的响应不完整 | 提示符模式可能提前命中日志中的伪提示符；已确认 Linux Shell 时切换到 `completion="shell"`，非 Shell 设备使用更精确的提示符正则 |
| 用户报告按键失灵（Tab/Ctrl/Ctrl+C 无响应） | 先用脚本排除链路问题：`send_raw(b"\t")` 后 `read_available(1.5)`，若设备回发响应则链路正常，问题出在用户所用的终端工具（nc 行缓冲、screen //telnet 协议不匹配），建议改用 socat PTY + picocom；若设备无响应，再查映射状态与设备本身 |
| Windows 侧看不到流量 | 确认 SerialBridge 主界面该映射为"运行中"且串口已连接 |

## 部署到 Linux 主机

把本技能整个目录拷贝到目标 agent 的技能目录即可（以 ZCode 为例）：

```bash
cp -r serial-tcp-uart ~/.agents/skills/    # 用户级
# 或 <项目>/.agents/skills/                # 项目级
```

SKILL.md 与 scripts/ 需在同一目录，脚本通过相对技能目录引用。

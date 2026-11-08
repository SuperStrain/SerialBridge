using System.Text;

namespace SerialBridge.Core.Models;

/// <summary>Saved command; a single step is a shortcut, multiple steps form a macro.</summary>
public sealed class CommandPreset : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private string _name = "新命令";
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public List<CommandStep> Steps { get; set; } = new() { new() };
}

public sealed class CommandStep : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private string _format = "Text";
    private string _content = "";
    private string _ending = "CR";
    private int _delayMs;

    /// <summary>Text or Hex. Hex accepts whitespace between byte pairs.</summary>
    public string Format
    {
        get => _format;
        set
        {
            if (_format == value) return;
            _format = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Format)));
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Content)));
        }
    }

    /// <summary>None, CR, LF or CRLF; used for text only.</summary>
    public string Ending
    {
        get => _ending;
        set
        {
            if (_ending == value) return;
            _ending = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Ending)));
        }
    }

    /// <summary>Delay after this step (0-60000ms).</summary>
    public int DelayMs
    {
        get => _delayMs;
        set
        {
            if (_delayMs == value) return;
            _delayMs = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DelayMs)));
        }
    }
}

/// <summary>
/// Validates and executes an immutable snapshot of the steps. Never invokes shell
/// commands locally: all bytes go through the caller's existing serial send path.
/// </summary>
public static class CommandMacroRunner
{
    public static byte[] Encode(CommandStep step)
    {
        if (step is null) throw new ArgumentNullException(nameof(step));
        var format = step.Format?.Trim();
        byte[] bytes;
        if (string.Equals(format, "Hex", StringComparison.OrdinalIgnoreCase))
        {
            var compact = new string((step.Content ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (compact.Length == 0 || compact.Length % 2 != 0 ||
                !compact.All(Uri.IsHexDigit))
                throw new FormatException("HEX 数据必须由完整的十六进制字节组成（例如 01 03 00 FF）。");
            bytes = Convert.FromHexString(compact);
            if (step.Ending is not ("None" or null or ""))
                throw new FormatException("HEX 步骤的换行方式必须为 None。");
        }
        else if (string.Equals(format, "Text", StringComparison.OrdinalIgnoreCase))
        {
            var text = step.Content ?? "";
            var ending = step.Ending switch
            {
                "None" or "" or null => "",
                "CR" => "\r",
                "LF" => "\n",
                "CRLF" => "\r\n",
                _ => throw new FormatException("换行方式仅支持 None、CR、LF、CRLF。")
            };
            bytes = Encoding.UTF8.GetBytes(text + ending);
        }
        else throw new FormatException("发送格式仅支持 Text 或 Hex。");

        if (bytes.Length is 0 or > 4096)
            throw new FormatException("每一步必须有内容，发送长度不得超过 4096 字节。");
        if (step.DelayMs is < 0 or > 60000)
            throw new FormatException("步骤延时必须在 0 到 60000 毫秒之间。");
        return bytes;
    }

    public static async Task RunAsync(CommandPreset preset, Func<byte[], bool> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(send);
        if (preset.Steps is null || preset.Steps.Count is < 1 or > 100)
            throw new InvalidOperationException("宏必须包含 1 至 100 个步骤。");

        // Validate everything before the first byte is sent (no half-macro due to invalid HEX).
        var prepared = preset.Steps.Select(s => (Data: Encode(s), Delay: s.DelayMs)).ToArray();
        foreach (var step in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!send(step.Data))
                throw new IOException("串口映射已停止或发送队列已满，宏已中止。");
            if (step.Delay > 0)
                await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);
        }
    }
}

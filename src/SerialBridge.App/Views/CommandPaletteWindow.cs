using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SerialBridge.App.Services;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;
using SerialBridge.Core.Serial;

namespace SerialBridge.App.Views;

/// <summary>Editable command shortcuts and macros for exactly one terminal mapping.</summary>
public sealed class CommandPaletteWindow : Window
{
    private readonly MappingRuntime _runtime;
    private readonly ObservableCollection<CommandPreset> _presets;
    private readonly ListBox _list = new() { MinWidth = 160, DisplayMemberPath = "Name" };
    private readonly TextBox _name = new() { MinWidth = 200 };
    private readonly CheckBox _confirm = new() { Content = "执行前确认（推荐）", IsChecked = true };
    private readonly DataGrid _steps = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false,
        CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        MinHeight = 220, Height = 290, SelectionMode = DataGridSelectionMode.Single
    };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _run = new() { Content = "发送 / 运行宏" };
    private readonly Button _cancel = new() { Content = "取消执行", IsEnabled = false };
    private CancellationTokenSource? _runCancellation;
    private bool _running;

    public CommandPaletteWindow(MappingRuntime runtime)
    {
        _runtime = runtime;
        Title = $"快捷命令与宏 — {runtime.Config.Name} ({runtime.Config.PortName})";
        Width = 920;
        Height = 590;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Edit a detached copy; Cancel/close cannot silently mutate global settings.
        var json = JsonSerializer.Serialize(AppServices.Settings.CommandPresets);
        _presets = new ObservableCollection<CommandPreset>(
            JsonSerializer.Deserialize<List<CommandPreset>>(json) ?? new());
        BuildUi();
        _list.ItemsSource = _presets;
        _list.SelectionChanged += (_, _) => SelectPreset();
        if (_presets.Count > 0) _list.SelectedIndex = 0;
        Closed += (_, _) => _runCancellation?.Cancel();
    }

    private static Button Action(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += (_, _) => action();
        return button;
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var footer = new StackPanel { Orientation = Orientation.Vertical };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_message);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Action("保存预设", Save));
        _run.Click += async (_, _) => await RunSelectedAsync();
        _run.Margin = new Thickness(0, 0, 8, 0);
        _run.Padding = new Thickness(10, 6, 10, 6);
        actions.Children.Add(_run);
        _cancel.Click += (_, _) => _runCancellation?.Cancel();
        actions.Children.Add(_cancel);
        footer.Children.Add(actions);
        root.Children.Add(footer);

        var info = new TextBlock
        {
            Text = "快捷命令 = 1 个步骤；宏 = 多个步骤。Text 默认 UTF-8；HEX 使用 01 03 FF 格式。每个步骤发送后可延时。仅向当前映射发送，不执行本机命令。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(info, Dock.Top);
        root.Children.Add(info);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(body);

        var left = new DockPanel();
        Grid.SetColumn(left, 0);
        body.Children.Add(left);
        var listActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        listActions.Children.Add(Action("新增", AddPreset));
        listActions.Children.Add(Action("删除", DeletePreset));
        DockPanel.SetDock(listActions, Dock.Top);
        left.Children.Add(listActions);
        left.Children.Add(_list);

        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        body.Children.Add(right);
        right.Children.Add(new TextBlock { Text = "名称", Margin = new Thickness(0, 0, 0, 4) });
        right.Children.Add(_name);
        _name.TextChanged += (_, _) =>
        {
            if (_list.SelectedItem is CommandPreset p && !_running)
            {
                p.Name = _name.Text;
                // List selection remains stable while the name is being edited.
            }
        };
        _confirm.Margin = new Thickness(0, 10, 0, 10);
        _confirm.Checked += (_, _) => UpdateConfirmation();
        _confirm.Unchecked += (_, _) => UpdateConfirmation();
        right.Children.Add(_confirm);

        var stepTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        stepTools.Children.Add(Action("添加步骤", AddStep));
        stepTools.Children.Add(Action("删除步骤", DeleteStep));
        right.Children.Add(stepTools);

        _steps.Columns.Add(new DataGridTextColumn
        {
            Header = "内容", Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Binding(nameof(CommandStep.Content)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
        });
        _steps.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "格式", Width = new DataGridLength(75),
            ItemsSource = new[] { "Text", "Hex" },
            SelectedItemBinding = new Binding(nameof(CommandStep.Format)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
        });
        _steps.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "换行", Width = new DataGridLength(75),
            ItemsSource = new[] { "None", "CR", "LF", "CRLF" },
            SelectedItemBinding = new Binding(nameof(CommandStep.Ending)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
        });
        _steps.Columns.Add(new DataGridTextColumn
        {
            Header = "延时 ms", Width = new DataGridLength(85),
            Binding = new Binding(nameof(CommandStep.DelayMs)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }
        });
        right.Children.Add(_steps);
        right.Children.Add(new TextBlock
        {
            Text = "发送 HEX 时请将换行设置为 None；单步最多 4096 字节，最多 100 步，单步延时 0–60000ms。宏顺序发送；有其他 TCP 客户端写入时仍可能交错。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.DimGray
        });
    }

    private void SelectPreset()
    {
        var preset = _list.SelectedItem as CommandPreset;
        _name.IsEnabled = !_running && preset is not null;
        _confirm.IsEnabled = !_running && preset is not null;
        _steps.IsReadOnly = _running;
        _name.Text = preset?.Name ?? "";
        _confirm.IsChecked = preset?.ConfirmBeforeRun ?? true;
        _steps.ItemsSource = preset?.Steps;
    }

    private void UpdateConfirmation()
    {
        if (_list.SelectedItem is CommandPreset p && !_running)
            p.ConfirmBeforeRun = _confirm.IsChecked == true;
    }

    private void AddPreset()
    {
        if (_running) return;
        if (_presets.Count >= 200) { _message.Text = "预设最多 200 个。"; return; }
        var preset = new CommandPreset { Name = "新命令" };
        _presets.Add(preset);
        _list.SelectedItem = preset;
    }

    private void DeletePreset()
    {
        if (_running || _list.SelectedItem is not CommandPreset p) return;
        if (MessageBox.Show($"删除预设「{p.Name}」？", "删除", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _presets.Remove(p);
        _list.SelectedIndex = _presets.Count > 0 ? 0 : -1;
    }

    private void AddStep()
    {
        if (_running || _list.SelectedItem is not CommandPreset p) return;
        if (p.Steps.Count >= 100) { _message.Text = "每个宏最多 100 步。"; return; }
        p.Steps.Add(new CommandStep());
        _steps.ItemsSource = null;
        _steps.ItemsSource = p.Steps;
        _steps.SelectedIndex = p.Steps.Count - 1;
    }

    private void DeleteStep()
    {
        if (_running || _list.SelectedItem is not CommandPreset p ||
            _steps.SelectedItem is not CommandStep step) return;
        if (p.Steps.Count <= 1) { _message.Text = "至少保留一个步骤。"; return; }
        p.Steps.Remove(step);
        _steps.ItemsSource = null;
        _steps.ItemsSource = p.Steps;
    }

    private bool ValidateAndCommit()
    {
        _steps.CommitEdit(DataGridEditingUnit.Cell, true);
        _steps.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var preset in _presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80)
                throw new FormatException("预设名称必须为 1–80 个字符。");
            if (preset.Steps is null || preset.Steps.Count is < 1 or > 100)
                throw new FormatException($"「{preset.Name}」必须有 1–100 个步骤。");
            foreach (var step in preset.Steps)
                CommandMacroRunner.Encode(step);
        }
        return true;
    }

    private void Save()
    {
        try
        {
            ValidateAndCommit();
            // Use the existing atomic settings store; don't create a second config file.
            AppServices.Settings.CommandPresets = _presets.ToList();
            AppServices.SaveSettings();
            _message.Text = "预设已保存。";
        }
        catch (Exception ex) { _message.Text = "保存失败：" + ex.Message; }
    }

    private async Task RunSelectedAsync()
    {
        if (_running || _list.SelectedItem is not CommandPreset preset) return;
        try
        {
            ValidateAndCommit();
            var status = _runtime.GetStatus();
            if (status.State != MappingState.Running || status.SerialState != SerialState.Open)
                throw new IOException("目标串口当前未连接，已取消发送。");
            var snapshot = JsonSerializer.Deserialize<CommandPreset>(JsonSerializer.Serialize(preset))!;
            if (snapshot.ConfirmBeforeRun &&
                MessageBox.Show($"向 { _runtime.Config.Name } ({ _runtime.Config.PortName }) 发送「{snapshot.Name}」？\n共 {snapshot.Steps.Count} 步。",
                    "确认目标设备", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _runCancellation = new CancellationTokenSource();
            _running = true;
            _run.IsEnabled = false;
            _cancel.IsEnabled = true;
            _steps.IsReadOnly = true;
            _list.IsEnabled = false;
            _message.Text = "执行中…";
            await CommandMacroRunner.RunAsync(snapshot, bytes =>
            {
                var current = _runtime.GetStatus();
                return current.State == MappingState.Running &&
                    current.SerialState == SerialState.Open && _runtime.WriteLocal(bytes);
            }, _runCancellation.Token);
            _message.Text = "全部步骤已进入串口发送队列。";
        }
        catch (OperationCanceledException) { _message.Text = "宏已取消。"; }
        catch (Exception ex) { _message.Text = "执行中止：" + ex.Message; }
        finally
        {
            _running = false;
            _run.IsEnabled = true;
            _cancel.IsEnabled = false;
            _steps.IsReadOnly = false;
            _list.IsEnabled = true;
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }
}

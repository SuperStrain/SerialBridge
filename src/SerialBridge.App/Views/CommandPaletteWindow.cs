using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SerialBridge.App.Services;
using SerialBridge.Core.Bridge;
using SerialBridge.Core.Models;

namespace SerialBridge.App.Views;

/// <summary>快捷命令与宏的编辑窗口；发送统一由终端命令栏完成，本窗口只负责维护预设。</summary>
public sealed class CommandPaletteWindow : Window
{
    /// <summary>预设保存成功后触发；监听方（如终端命令栏）刷新显示。</summary>
    internal static event Action? PresetsSaved;

    private readonly ObservableCollection<CommandPreset> _presets;
    private readonly ListBox _list = new() { MinWidth = 170, DisplayMemberPath = "Name" };
    private readonly TextBox _name = new() { MinWidth = 200 };
    private readonly DataGrid _steps = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false,
        CanUserDeleteRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
        MinHeight = 170, SelectionMode = DataGridSelectionMode.Single
    };
    private DataGridComboBoxColumn? _formatColumn;
    private DataGridTextColumn? _delayColumn;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private CommandStep? _editingFormatStep;
    private string? _editingOriginalFormat;

    public CommandPaletteWindow(MappingRuntime runtime)
    {
        Title = $"快捷命令与宏 — {runtime.Config.Name} ({runtime.Config.PortName})";
        Width = 880;
        Height = 560;
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
        else _message.Text = "点击「新增」创建第一个预设。";

        // Ctrl+S 保存并关闭
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Save();
            }
        };
    }

    private static Button Action(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += (_, _) => action();
        return button;
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;

        // 底部：状态消息 + 保存按钮（保存成功后自动关闭窗口）
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _message.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_message, 0);
        footer.Children.Add(_message);
        var saveButton = new Button
        {
            Content = "保存预设", MinWidth = 100,
            Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(10, 0, 0, 0),
            ToolTip = "保存全部预设并关闭窗口（Ctrl+S）"
        };
        saveButton.Click += (_, _) => Save();
        Grid.SetColumn(saveButton, 1);
        footer.Children.Add(saveButton);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(body);

        // 左列：新增/删除在顶部，列表随窗口拉伸
        var left = new DockPanel();
        Grid.SetColumn(left, 0);
        body.Children.Add(left);
        var listActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        listActions.Children.Add(Action("新增", AddPreset));
        listActions.Children.Add(Action("删除", DeletePreset));
        DockPanel.SetDock(listActions, Dock.Top);
        left.Children.Add(listActions);
        left.Children.Add(_list);

        // 右列：名称 → 步骤工具 → 步骤表格（随窗口拉伸）→ 提示
        var right = new Grid();
        Grid.SetColumn(right, 2);
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(right);

        var namePanel = new StackPanel();
        namePanel.Children.Add(new TextBlock { Text = "名称", Margin = new Thickness(0, 0, 0, 4) });
        namePanel.Children.Add(_name);
        _name.TextChanged += (_, _) =>
        {
            if (_list.SelectedItem is CommandPreset p)
            {
                p.Name = _name.Text;
                // List selection remains stable while the name is being edited.
            }
        };
        Grid.SetRow(namePanel, 0);
        right.Children.Add(namePanel);

        var stepTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 6) };
        stepTools.Children.Add(Action("添加步骤", AddStep));
        stepTools.Children.Add(Action("删除步骤", DeleteStep));
        Grid.SetRow(stepTools, 1);
        right.Children.Add(stepTools);

        Grid.SetRow(_steps, 2);
        right.Children.Add(_steps);

        var hint = new TextBlock
        {
            Text = "换行 CR = 回车键（串口控制台标准）；Hex 格式换行固定为 None；单步 ≤4096 字节、≤100 步；延时 0–60000ms，为与下一步的间隔，最后一步不生效。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.DimGray
        };
        Grid.SetRow(hint, 3);
        right.Children.Add(hint);

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
        _formatColumn = (DataGridComboBoxColumn)_steps.Columns[^1];
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
        _delayColumn = (DataGridTextColumn)_steps.Columns[^1];
        // Hex 步骤不允许换行、Text 步骤通常要回车：随格式自动纠正，避免发送时报错。
        _steps.BeginningEdit += OnStepCellBeginEdit;
        _steps.CellEditEnding += OnStepCellEditEnding;
        // 延时是「与下一步的间隔」：最后一步没有下一步，其单元格置灰禁用并提示。
        _steps.LoadingRow += OnStepRowLoading;
    }

    private void OnStepCellBeginEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        // 最后一行（含单步命令）没有下一步：延时单元格拒绝进入编辑并提示，
        // 避免填入永远不会生效的值；已填数值保留，后续追加步骤后自动生效。
        if (e.Column == _delayColumn && e.Row.Item is CommandStep tail &&
            !DelayEditable(_list.SelectedItem as CommandPreset, tail))
        {
            e.Cancel = true;
            _message.Text = "最后一步没有后续步骤，延时无效；如需步骤间隔，请在需要等待位置的上一步骤上配置延时。";
            return;
        }
        // 记录进入编辑时的原始格式：仅在格式真正变化时才纠正换行。
        if (e.Column != _formatColumn || e.Row.Item is not CommandStep step)
        {
            _editingFormatStep = null;
            return;
        }
        _editingFormatStep = step;
        _editingOriginalFormat = step.Format;
    }

    private void OnStepCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Column != _formatColumn || e.Row.Item is not CommandStep step)
        {
            _editingFormatStep = null;
            return;
        }
        var originalFormat = _editingFormatStep == step ? _editingOriginalFormat : null;
        _editingFormatStep = null;
        if (e.EditAction != DataGridEditAction.Commit) return;
        // 格式列绑定带 PropertyChanged 触发器，提交时模型已是新值。
        var format = NormalizeFormat(step.Format);
        if (step.Format != format) step.Format = format;
        // 未切换格式时不得覆盖用户显式配置的换行（例如 Text + None）。
        step.Ending = ComputeEndingAfterFormatChange(originalFormat, format, step.Ending);
        // CommandStep 带 INPC：直接赋值即可刷新同行换行 ComboBox 的显示。
        // 不能在此调用 Items.Refresh —— 编辑事务尚未结束会抛 InvalidOperationException。
    }

    /// <summary>格式实际变化时的换行纠正规则；格式未变化则原样保留当前配置。</summary>
    internal static string ComputeEndingAfterFormatChange(string? originalFormat, string newFormat, string currentEnding)
    {
        var format = newFormat?.Trim();
        var original = originalFormat?.Trim();
        if (string.Equals(original, format, StringComparison.OrdinalIgnoreCase)) return currentEnding;
        return string.Equals(format, "Hex", StringComparison.OrdinalIgnoreCase) ? "None" : "CR";
    }

    /// <summary>延时是「与下一步的间隔」：最后一行（含单步命令）没有下一步，其延时单元格应禁用。</summary>
    internal static bool DelayEditable(CommandPreset? preset, CommandStep? step) =>
        preset?.Steps is { Count: > 0 } steps && step is not null && !ReferenceEquals(steps[^1], step);

    private void OnStepRowLoading(object? sender, DataGridRowEventArgs e) =>
        e.Row.Loaded += OnStepRowLoaded;

    private void OnStepRowLoaded(object sender, RoutedEventArgs e)
    {
        // 行模板应用完成后才能找到延时单元格；添加/删除步骤会重置 ItemsSource
        // 重新生成行，置灰状态随之自动重算。
        if (sender is DataGridRow row)
        {
            row.Loaded -= OnStepRowLoaded;
            RefreshDelayCellState(row);
        }
    }

    private void RefreshDelayCellState(DataGridRow row)
    {
        if (_delayColumn is null || row.Item is not CommandStep) return;
        if (FindCellsPresenter(row) is not { } presenter) return;
        // 按“列集合索引”定位容器：DisplayIndex 是显示序，用户拖动列后两者不一致，
        // 会导致禁用错误的单元格（CellsPresenter 的容器按集合顺序生成）。
        var index = _steps.Columns.IndexOf(_delayColumn);
        if (index < 0 || index >= presenter.Items.Count ||
            presenter.ItemContainerGenerator.ContainerFromIndex(index) is not DataGridCell cell) return;
        var editable = DelayEditable(_list.SelectedItem as CommandPreset, row.Item as CommandStep);
        cell.IsEnabled = editable;
        cell.ToolTip = editable ? null : "最后一步没有后续步骤，延时无效";
    }

    private static DataGridCellsPresenter? FindCellsPresenter(DependencyObject parent)
    {
        if (parent is DataGridCellsPresenter presenter) return presenter;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is not DependencyObject child) continue;
            if (FindCellsPresenter(child) is { } found) return found;
        }
        return null;
    }

    private static string NormalizeFormat(string? format) =>
        string.Equals(format?.Trim(), "Hex", StringComparison.OrdinalIgnoreCase) ? "Hex" : "Text";

    private void SelectPreset()
    {
        var preset = _list.SelectedItem as CommandPreset;
        _name.IsEnabled = preset is not null;
        _name.Text = preset?.Name ?? "";
        _steps.ItemsSource = preset?.Steps;
    }

    private void AddPreset()
    {
        if (_presets.Count >= 200) { _message.Text = "预设最多 200 个。"; return; }
        var preset = new CommandPreset { Name = "新命令" };
        _presets.Add(preset);
        _list.SelectedItem = preset;
    }

    private void DeletePreset()
    {
        if (_list.SelectedItem is not CommandPreset p) return;
        if (MessageBox.Show($"删除预设「{p.Name}」？", "删除", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _presets.Remove(p);
        _list.SelectedIndex = _presets.Count > 0 ? 0 : -1;
        if (_presets.Count == 0) _message.Text = "点击「新增」创建预设。";
    }

    private void AddStep()
    {
        if (_list.SelectedItem is not CommandPreset p) return;
        if (p.Steps.Count >= 100) { _message.Text = "每个宏最多 100 步。"; return; }
        p.Steps.Add(new CommandStep());
        _steps.ItemsSource = null;
        _steps.ItemsSource = p.Steps;
        _steps.SelectedIndex = p.Steps.Count - 1;
    }

    private void DeleteStep()
    {
        if (_list.SelectedItem is not CommandPreset p || _steps.SelectedItem is not CommandStep step) return;
        if (p.Steps.Count <= 1) { _message.Text = "至少保留一个步骤。"; return; }
        p.Steps.Remove(step);
        _steps.ItemsSource = null;
        _steps.ItemsSource = p.Steps;
    }

    private void CommitPendingEdit()
    {
        // Invalid WPF bindings (e.g. DelayMs = "abc") keep the old model value.
        // Never send or save that stale value while the editor still shows an error.
        if (!_steps.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !_steps.CommitEdit(DataGridEditingUnit.Row, true))
            throw new FormatException("当前步骤编辑无效，请修正单元格后重试。");
    }

    private static void ValidatePreset(CommandPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80)
            throw new FormatException("预设名称必须为 1–80 个字符。");
        if (preset.Steps is null || preset.Steps.Count is < 1 or > 100)
            throw new FormatException($"「{preset.Name}」必须有 1–100 个步骤。");
        foreach (var step in preset.Steps)
            CommandMacroRunner.Encode(step);
    }

    private static List<CommandPreset> SnapshotPresets(IEnumerable<CommandPreset> presets) =>
        JsonSerializer.Deserialize<List<CommandPreset>>(JsonSerializer.Serialize(presets))!;

    private void Save()
    {
        try
        {
            CommitPendingEdit();
            foreach (var preset in _presets) ValidatePreset(preset);
            // Global settings must not alias editable presets or their Steps.
            // This ensures subsequent unsaved edits are discarded on close.
            AppServices.Settings.CommandPresets = SnapshotPresets(_presets);
            AppServices.SaveSettings();
            PresetsSaved?.Invoke();
            // 保存成功即关闭，编辑完成立即回到终端；失败时留在窗口显示原因。
            Close();
        }
        catch (Exception ex) { _message.Text = "保存失败：" + ex.Message; }
    }
}

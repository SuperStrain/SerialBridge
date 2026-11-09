using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SerialBridge.App.Services;
using SerialBridge.App.ViewModels;

namespace SerialBridge.App.Views;

/// <summary>
/// 更新弹窗(Clash Verge 式):标题 + Release 正文(Markdown 渲染)+ 前往发布 / 取消 / 更新 / 跳过此版本。
/// 由主界面 NEW 徽章点击打开;下载进度在状态行展示,安装由 Setup 接力重启。
/// </summary>
public sealed class UpdatePopupWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateViewModel _update;
    private readonly FlowDocumentScrollViewer _notesHost;

    public UpdatePopupWindow(UpdateViewModel update)
    {
        _update = update;
        DataContext = update;
        Title = "软件更新";
        Width = 660;
        Height = 520;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;

        // 底部：状态行 + 按钮组
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(UpdateViewModel.PopupStatusText)));
        footer.Children.Add(status);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var skipButton = new Wpf.Ui.Controls.Button
        {
            Content = "跳过此版本", MinWidth = 92, Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "跳过该版本后不再提醒（可删除 %AppData%\\SerialBridge\\skip_version.txt 恢复）"
        };
        skipButton.SetBinding(UIElement.IsEnabledProperty,
            new Binding(nameof(UpdateViewModel.PopupActionVisible)));
        skipButton.Click += (_, _) =>
        {
            _update.SkipVersion();
            Close();
        };
        actions.Children.Add(skipButton);

        var cancelButton = new Wpf.Ui.Controls.Button
        {
            Content = "取消", MinWidth = 92, Margin = new Thickness(0, 0, 8, 0)
        };
        cancelButton.Click += (_, _) => Close();
        actions.Children.Add(cancelButton);

        var updateButton = new Wpf.Ui.Controls.Button
        {
            Content = "更新", MinWidth = 108,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary
        };
        updateButton.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty,
            new Binding(nameof(UpdateViewModel.UpdateNowCommand)));
        actions.Children.Add(updateButton);

        footer.Children.Add(actions);
        root.Children.Add(footer);

        // 顶部：标题 + 前往发布
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(UpdateViewModel.UpdatePopupTitle)));
        Grid.SetColumn(title, 0);
        header.Children.Add(title);
        var releaseButton = new Wpf.Ui.Controls.Button
        {
            Content = "前往发布", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            ToolTip = "在浏览器打开 GitHub 发布页"
        };
        releaseButton.Click += (_, _) => _update.Updater.OpenReleasesPage();
        Grid.SetColumn(releaseButton, 1);
        header.Children.Add(releaseButton);
        root.Children.Add(header);

        // 中部：Release 正文（Markdown 渲染，可滚动、可选中复制）
        // 说明：FlowDocumentScrollViewer 中超链接默认即可点击（IsDocumentEnabled 是 RichTextBox 的属性）。
        _notesHost = new FlowDocumentScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            IsToolBarVisible = false,   // 隐藏底部缩放工具条
        };
        root.Children.Add(_notesHost);
        RenderNotes();
        _update.PropertyChanged += OnUpdatePropertyChanged;
        Closed += (_, _) => _update.PropertyChanged -= OnUpdatePropertyChanged;
    }

    private void RenderNotes()
    {
        _notesHost.Document = ReleaseNotesRenderer.ToFlowDocument(_update.ReleaseNotes);
    }

    private void OnUpdatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateViewModel.ReleaseNotes)) RenderNotes();
    }
}

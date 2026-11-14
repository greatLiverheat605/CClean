using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CClean.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource closing = new();
    private readonly System.Windows.Threading.DispatcherTimer capacityTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool capacityReading;
    private bool dark;
    private bool preferencesLoaded;
    private string? initializationError;
    private ViewModels.AppPreferences preferences = new();
    private ViewModels.WorkspaceViewModel? workspace;
    private Views.ProjectSetupView? projectSetup;
    private Views.OverviewView? overview;
    private Views.ProjectWorkbenchView? workbench;
    private Views.ProjectWorkbenchView? activities;
    private Views.CleanupPreviewView? cleanup;
    private Views.CleanupHistoryView? cleanupHistory;
    private Action? returnToContext;
    internal ViewModels.WorkspaceViewModel? Workspace => workspace;
    private readonly bool enableSessionDiscovery;
    public MainWindow() : this(true) { }
    internal MainWindow(bool enableSessionDiscovery)
    {
        this.enableSessionDiscovery = enableSessionDiscovery;
        InitializeComponent();
        Navigation.SelectedIndex = 0;
        capacityTimer.Tick += async (_, _) => await ReadCapacity();
        capacityTimer.Start();
        FitWorkArea();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(FitWorkArea);
        SidebarScroll.SizeChanged += (_, _) => SidebarBody.MinHeight = Math.Max(0, SidebarScroll.ActualHeight - 56);
        InitializeLifecycle();
    }
    private void FitWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height));
        if (!double.IsNaN(Left)) Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        if (!double.IsNaN(Top)) Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ReadCapacity();
        if (exitRequested) return;
        var dataDirectory = Environment.GetEnvironmentVariable("CCLEAN_DATA_DIR") ??
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CClean");
        try
        {
            preferences = ViewModels.AppPreferences.Load(dataDirectory);
            preferencesLoaded = true; dark = preferences.Dark; ApplyTheme();
            workspace = new(enableSessionDiscovery);
            workspace.Changed += UpdateObservationStatus;
            overview = new(workspace);
            cleanup = new(workspace);
            overview.ManageScopeRequested += OpenScope;
            ShowWorkspace();
            UpdateObservationStatus();
            await workspace.Resume();
        }
        catch (Exception error)
        {
            initializationError = "数据目录：" + dataDirectory + "\n" + error.Message;
            ShowInitializationError();
        }
    }
    private void ShowInitializationError()
    {
        PageTitle.Text = "本地配置不可用";
        Status.Text = "初始化未完成，原配置文件未覆盖。";
        ReturnButton.Visibility = Visibility.Collapsed; returnToContext = null;
        PageContent.Content = new TextBlock
        {
            Text = "无法载入本地配置：" + initializationError +
                "\n\n请检查文件内容与访问权限，修复后重新启动 CClean。当前主题可临时切换，偏好设置不会保存。",
            TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(20)
        };
    }
    private void OnClosed(object? sender, EventArgs e) => CompleteClosed();
    private async void Refresh(object sender, RoutedEventArgs e) => await ReadCapacity();
    private async Task ReadCapacity()
    {
        if (capacityReading || closing.IsCancellationRequested) return;
        capacityReading = true;
        try
        {
            var volumes = await Task.Run(Core.Storage.VolumeSpaceReader.ReadLocalVolumes, closing.Token);
            if (exitRequested || closing.IsCancellationRequested) return;
            if (workspace is not null) await workspace.EnforceBudget();
            if (exitRequested || closing.IsCancellationRequested) return;
            Capacity.Text = string.Join("  ·  ", volumes.Select(v => v.Space is null ? $"{v.Root} 容量未知" :
                $"{v.Root} 可用 {v.Space.UserAvailableBytes / 1073741824d:F1} / {v.Space.TotalBytes / 1073741824d:F1} GiB")) +
                $"  ·  {DateTime.Now:HH:mm:ss}；容量不代表已盘点覆盖";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status.Text = "容量读取失败：" + error.Message; }
        finally { capacityReading = false; }
    }

    private void UpdateObservationStatus()
    {
        UpdateTray();
        if (workspace is null || exitRequested) return;
        var samples = workspace.Monitors.Values.Where(m => m.Revision > 0).Select(m => m.Snapshot.CapturedAt).ToArray();
        Status.Text = workspace.Status + (samples.Length == 0 ? " 最后有效索引：尚未取得。" : $" 最后有效索引：{samples.Max().ToLocalTime():MM-dd HH:mm:ss}。");
    }
    private void OpenSpace(string root)
    {
        var originTitle = PageTitle.Text;
        var restore = CapturePageContext();
        overview?.ShowScope(root);
        if (Navigation.SelectedIndex != 0) Navigation.SelectedIndex = 0;
        else ShowWorkspace();
        if (originTitle != "空间与清理")
        {
            ReturnButton.Content = $"← 返回{originTitle}";
            ReturnButton.Visibility = Visibility.Visible;
            returnToContext = restore;
        }
    }
    private void OpenScope(string? root)
    {
        if (workspace is null) return;
        if (projectSetup is null)
        {
            projectSetup = new(workspace);
            projectSetup.SpaceRequested += OpenSpace;
        }
        projectSetup.ShowScope(root);
        ShowAuxiliary("管理范围", projectSetup);
    }
    private void OpenScopeManagement(object sender, RoutedEventArgs e) => OpenScope(null);
    private void OpenHistory(object sender, RoutedEventArgs e)
    {
        if (workspace is null) return;
        cleanupHistory ??= new(workspace);
        ShowAuxiliary("处理历史", cleanupHistory);
    }
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (initializationError is not null) { ShowInitializationError(); return; }
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "工具识别与覆盖", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "只覆盖已确认的 Windows 物理目录；自动发现不等于找齐全部 Agent。自定义工具需在管理范围中登记，工具标签不证明独占、来源或删除资格。WSL / Docker 内部和旧版本处理不在首批范围。", Margin = new Thickness(0, 0, 0, 16) });
        var audit = new Button { Content = "打开已实现功能审计", HorizontalAlignment = HorizontalAlignment.Left }; audit.Click += OpenAudit; panel.Children.Add(audit);
        ShowAuxiliary("设置与覆盖说明", new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
    private void ShowAuxiliary(string title, object content)
    {
        if (ReferenceEquals(PageContent.Content, content)) return;
        var originTitle = PageTitle.Text;
        var restore = CapturePageContext();
        PageTitle.Text = title; PageContent.Content = content; ReturnButton.Visibility = Visibility.Visible;
        ReturnButton.Content = $"← 返回{originTitle}";
        returnToContext = restore;
    }
    private Action CapturePageContext()
    {
        var title = PageTitle.Text; var content = PageContent.Content; var index = Navigation.SelectedIndex;
        var returnVisibility = ReturnButton.Visibility; var returnLabel = ReturnButton.Content; var priorReturn = returnToContext;
        return () =>
        {
            Navigation.SelectedIndex = index; PageTitle.Text = title; PageContent.Content = content;
            ReturnButton.Visibility = returnVisibility; ReturnButton.Content = returnLabel; returnToContext = priorReturn;
        };
    }
    private void ReturnToWorkspace(object sender, RoutedEventArgs e) { if (returnToContext is { } action) action(); else ShowWorkspace(); }

    private void Navigate(object sender, SelectionChangedEventArgs e)
    {
        if (PageContent is null) return;
        ShowWorkspace();
    }
    private void ShowWorkspace()
    {
        if (initializationError is not null) { ShowInitializationError(); return; }
        if (Navigation.SelectedItem is not ListBoxItem selected) return;
        var title = selected.Content.ToString()!;
        PageTitle.Text = title; ReturnButton.Visibility = Visibility.Collapsed; returnToContext = null;
        if (title == "空间与清理" && overview is not null) { PageContent.Content = overview; return; }
        if (title == "开发活动" && workspace is not null)
        {
            if (activities is null)
            {
                activities = new(workspace, preferences, activityWorkspace: true);
                activities.SpaceRequested += OpenSpace;
                activities.ManageScopeRequested += OpenScope;
            }
            PageContent.Content = activities; return;
        }
        if (title == "项目与版本" && workspace is not null)
        {
            if (workbench is null)
            {
                workbench = new(workspace, preferences);
                workbench.SpaceRequested += OpenSpace;
                workbench.ManageScopeRequested += OpenScope;
            }
            PageContent.Content = workbench; return;
        }
        PageContent.Content = new TextBlock { Text = "正在载入本地工作区…",
            FontSize = 18, Margin = new Thickness(20) };
    }

    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        dark = !dark; ApplyTheme();
        if (!preferencesLoaded || initializationError is not null || workspace is null)
        {
            Status.Text = "主题已临时切换；本地配置尚未成功载入，原配置文件未覆盖。";
            return;
        }
        preferences.Dark = dark;
        try { preferences.Save(workspace.DataDirectory); }
        catch (Exception error) { Status.Text = "主题已切换，但保存失败：" + error.Message; }
    }
    private void ApplyTheme()
    {
        var colors = dark ? new[] { "#171D25", "#222B36", "#F2F5F8", "#B9C5D2", "#8BB9F7", "#465568", "#142237" } :
            new[] { "#F5F6F8", "#FFFFFF", "#17212B", "#526170", "#245BA8", "#D5DCE4", "#FFFFFF" };
        var keys = new[] { "Surface", "Panel", "Ink", "Muted", "Accent", "Line", "OnAccent" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    private void OpenAudit(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "docs", "audit", "implemented-features.md");
        try
        {
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("此开发目录未携带审计文档，请使用安装版或仓库 docs/audit/implemented-features.md。");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"))
                { UseShellExecute = true, ArgumentList = { path } })?.Dispose();
        }
        catch (Exception error) { Status.Text = "审计文档无法打开：" + error.Message; }
    }
}

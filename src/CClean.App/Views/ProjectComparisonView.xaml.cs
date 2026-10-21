using System.Windows;
using System.Windows.Controls;
using CClean.Core.Projects;
using CClean.Core.Storage;

namespace CClean.App.Views;

public partial class ProjectComparisonView : UserControl
{
    private ProjectModule? project;
    private CancellationTokenSource? work;
    private ProjectComparisonResult? result;
    private int generation;
    public ProjectComparisonView()
    {
        InitializeComponent();
        OtherPath.TextChanged += OtherPathChanged;
        Unloaded += (_, _) => work?.Cancel();
    }
    public void ShowProject(ProjectModule selected)
    {
        generation++; work?.Cancel(); project = selected; result = null;
        CurrentPath.Text = "当前项目：" + selected.Root; CurrentPath.ToolTip = selected.Root;
        OtherPath.Text = ""; Results.ItemsSource = null; TableStatus.Text = "";
        Status.Text = "选择另一版本文件夹后开始比较。独有与相同结果均不授权删除。";
        Details.Text = "每侧最多盘点 10,000 项，合计最多读取 512 MiB 内容，单次时间预算 30 秒；较大项目请比较具体子目录。";
        UpdateButtons();
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        if (work is not null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择项目的另一个版本文件夹" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        OtherPath.Text = dialog.FolderName; OtherPath.ToolTip = dialog.FolderName;
        generation++; ClearResult("目录已选择，可以开始只读比较。"); UpdateButtons();
    }
    private void OtherPathChanged(object sender, TextChangedEventArgs e)
    {
        generation++; work?.Cancel(); OtherPath.ToolTip = OtherPath.Text;
        ClearResult(string.IsNullOrWhiteSpace(OtherPath.Text) ? "请选择或输入另一版本文件夹。" : "目录已更改，需要重新开始比较。");
        UpdateButtons();
    }
    private void ClearResult(string message)
    {
        result = null; RefreshRows(); Status.Text = Details.Text = message;
    }
    private async void Compare(object sender, RoutedEventArgs e)
    {
        if (work is not null || project is null || string.IsNullOrWhiteSpace(OtherPath.Text)) return;
        var request = ++generation; var left = project.Root; var right = OtherPath.Text;
        using var cancellation = new CancellationTokenSource(); work = cancellation;
        ClearResult("正在盘点与逐字节比较，可随时取消。目录中发现的敏感数据保持未比较。"); UpdateButtons();
        try
        {
            var comparison = await Task.Run(() => ProjectComparison.Compare(left, right, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (request != generation) return;
            result = comparison; RefreshRows();
            Details.Text = "比较已结束，选择当前结果中的一行查看依据。内容相同也不代表目录可删除。";
            var counts = comparison.Items.GroupBy(i => i.Difference).ToDictionary(g => g.Key, g => g.Count());
            int Count(ProjectDifference value) => counts.GetValueOrDefault(value);
            Status.Text = $"当前独有 {Count(ProjectDifference.LeftOnly)} · 另一侧独有 {Count(ProjectDifference.RightOnly)} · 相同 {Count(ProjectDifference.ContentEqual)} · 差异 {Count(ProjectDifference.SizeDifferent) + Count(ProjectDifference.ContentDifferent)} · 未确认 {Count(ProjectDifference.Unknown)}。" +
                $"已读取 {IndexedFile.FormatBytes(comparison.ReadBytes)} 内容。" +
                (!comparison.LeftComplete || !comparison.RightComplete ? "盘点有缺口，缺失位置不能认定为独有。" : "") +
                (comparison.Limited ? "达到预算，请缩小比较范围。" : "") +
                (comparison.HiddenSystemObjects > 0 ? "系统对象已隐藏。" : "");
        }
        catch (OperationCanceledException) { if (request == generation) ClearResult("已取消比较，没有修改任何文件。"); }
        catch (Exception error) { if (request == generation) ClearResult("无法比较：" + error.Message); }
        finally { if (ReferenceEquals(work, cancellation)) work = null; UpdateButtons(); }
    }
    private void Cancel(object sender, RoutedEventArgs e)
    {
        if (work is null) return;
        work.Cancel(); ClearResult("正在取消比较，不会保留本次未完成的结论。");
    }
    private void UpdateButtons()
    {
        BrowseButton.IsEnabled = work is null;
        OtherPath.IsReadOnly = work is not null;
        CompareButton.IsEnabled = work is null && project is not null && !string.IsNullOrWhiteSpace(OtherPath.Text);
        CancelButton.IsEnabled = work is not null;
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (Results is not null) RefreshRows(); }
    private void RefreshRows()
    {
        var rows = (result?.Items ?? []).Where(i => Filter.SelectedIndex switch
        {
            0 => i.Difference != ProjectDifference.ContentEqual,
            2 => i.Difference is ProjectDifference.LeftOnly or ProjectDifference.RightOnly,
            3 => i.Difference == ProjectDifference.ContentEqual,
            _ => true
        }).ToArray();
        Results.ItemsSource = rows.Take(1000).Select(i => new Row(i)).ToArray();
        TableStatus.Text = result is null ? "" : rows.Length == 0 ? "此筛选条件下没有结果。" : $"符合筛选 {rows.Length:N0} 项，显示前 {Math.Min(rows.Length, 1000):N0} 项；选择一行查看依据。";
    }
    private void Selected(object sender, SelectionChangedEventArgs e)
    {
        if (Results.SelectedItem is Row row) Details.Text = row.RelativePath + "\n" + row.Item.Reason;
        else if (result is not null) Details.Text = "请选择当前结果中的一行查看依据。";
    }
    private sealed record Row(ProjectComparisonItem Item)
    {
        public string RelativePath => Item.RelativePath;
        public string LeftSize => Item.LeftBytes is long value ? IndexedFile.FormatBytes(value) : "—";
        public string RightSize => Item.RightBytes is long value ? IndexedFile.FormatBytes(value) : "—";
        public string Label => Item.Difference switch
        {
            ProjectDifference.LeftOnly => "当前独有 · 保留",
            ProjectDifference.RightOnly => "另一侧独有 · 保留",
            ProjectDifference.SizeDifferent => "大小不同 · 保留",
            ProjectDifference.ContentDifferent => "内容不同 · 保留",
            ProjectDifference.ContentEqual => "内容相同 · 只读结论",
            _ => "未确认 · 保护"
        };
    }
}

using System.Windows;
using System.Windows.Controls;
using CClean.App.ViewModels;

namespace CClean.App.Views;

public partial class OverviewView : UserControl
{
    private readonly WorkspaceViewModel workspace;
    public OverviewView(WorkspaceViewModel workspace)
    {
        InitializeComponent(); this.workspace = workspace;
        Loaded += async (_, _) => await Update();
        workspace.Indexed += async () => { if (IsVisible) await Update(); };
    }
    private async void Refresh(object sender, RoutedEventArgs e) => await Update();
    private async Task Update()
    {
        try
        {
            var totals = await Task.Run(() => workspace.Store.Totals(Environment.SystemDirectory));
            Logical.Text = Core.Storage.IndexedFile.FormatBytes(totals.Logical);
            Allocated.Text = Core.Storage.IndexedFile.FormatBytes(totals.Allocated);
            Growth.Text = totals.Unknown > 0 ? "未知（存在未计量对象）" : (totals.Growth > 0 ? "+" : "") + Core.Storage.IndexedFile.FormatBytes(totals.Growth);
            Coverage.Text = $"C 盘索引 {totals.Files} 个路径，{totals.Unknown} 个未知对象；索引可能因暂停或缺口过期。登记根共 {workspace.Projects.Count} 个。";
            if (!workspace.Projects.Any(p => System.IO.Path.GetPathRoot(p.Root)!.Equals(System.IO.Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)))
                Logical.Text = Allocated.Text = Growth.Text = "尚未登记 C 盘范围";
        }
        catch (Exception error) { Coverage.Text = "统计不可用：" + error.Message; }
    }
}

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
    private bool dark;
    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectedIndex = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await ReadCapacity();
    private void OnClosed(object? sender, EventArgs e) { closing.Cancel(); closing.Dispose(); }
    private async void Refresh(object sender, RoutedEventArgs e) => await ReadCapacity();
    private async Task ReadCapacity()
    {
        try
        {
            var space = await Task.Run(() => Core.Storage.VolumeSpaceReader.Read(Environment.SystemDirectory), closing.Token);
            Capacity.Text = $"C: 可用 {space.UserAvailableBytes / 1073741824d:F1} GiB / {space.TotalBytes / 1073741824d:F1} GiB  ·  更新 {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Status.Text = "容量读取失败：" + error.Message; }
    }

    private void Navigate(object sender, SelectionChangedEventArgs e)
    {
        if (PageContent is null || Navigation.SelectedItem is not ListBoxItem selected) return;
        var title = selected.Content.ToString()!;
        PageTitle.Text = title;
        PageContent.Content = new TextBlock { Text = title == "总览" ? "尚未登记监测范围\n\n先登记一个项目或目录，建立当前文件基线后再查看增长。" : "此页面正在接入已验证的本地能力。当前未开放清理执行。",
            FontSize = 18, Margin = new Thickness(20) };
    }

    private void ToggleTheme(object sender, RoutedEventArgs e)
    {
        dark = !dark;
        var colors = dark ? new[] { "#171D25", "#222B36", "#F2F5F8", "#B9C5D2", "#8BB9F7", "#465568" } :
            new[] { "#F5F6F8", "#FFFFFF", "#17212B", "#526170", "#245BA8", "#D5DCE4" };
        var keys = new[] { "Surface", "Panel", "Ink", "Muted", "Accent", "Line" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}

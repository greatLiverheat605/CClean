using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CClean.Core.Attribution;
using CClean.Core.History;
using CClean.Core.Projects;
using CClean.Core.Sessions;

namespace CClean.App.Controls;

public sealed class TimelineCanvas : Control
{
    private IReadOnlyList<TimelineNode> nodes = [];
    private IReadOnlyList<RegisteredProject> projects = [];
    private IReadOnlyList<DevelopmentSession> sessions = [];
    private readonly List<(int Index, Point Point)> targets = [];
    private DateTimeOffset since, until;
    private int selected = -1;
    public event Action<TimelineNode>? Selected;
    internal event Action<IReadOnlyList<TimelineNode>>? FrameRendered;
    public TimelineCanvas()
    {
        Focusable = true; MinWidth = 640;
        SetResourceReference(ForegroundProperty, "Ink"); SetResourceReference(BackgroundProperty, "Panel");
        AutomationProperties.SetName(this, "开发时间线图。左右方向键选择变化；列表视图提供完整键盘与朗读替代。");
        var next = new RoutedCommand(); var previous = new RoutedCommand();
        InputBindings.Add(new KeyBinding(next, Key.Right, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(previous, Key.Left, ModifierKeys.None));
        CommandBindings.Add(new CommandBinding(next, (_, _) => Choose(Math.Min(selected + 1, nodes.Count - 1))));
        CommandBindings.Add(new CommandBinding(previous, (_, _) => Choose(Math.Max(0, selected - 1))));
    }
    private static string Lane(TimelineNode node) => node.Project + ":" + (node.Project is "共享" or "未知" ? "" : string.Join(",", node.Evidence.Sessions));
    public void Load(TimelineResult result, IReadOnlyList<RegisteredProject> projects, IReadOnlyList<DevelopmentSession> sessions, DateTimeOffset since, DateTimeOffset until)
    {
        nodes = result.Nodes.Take(500).OrderBy(n => n.Time).ToArray(); this.projects = projects; this.sessions = sessions; this.since = since; this.until = until;
        selected = -1;
        Height = Math.Max(180, 70 + nodes.GroupBy(Lane).Sum(l => 36 + l.GroupBy(n => n.Time).Max(g => g.Count()) * 30));
        InvalidateVisual();
    }
    public void Select(TimelineNode node) { selected = nodes.ToList().IndexOf(node); InvalidateVisual(); }
    private void Choose(int index)
    {
        if (index < 0 || index >= nodes.Count) return;
        selected = index; InvalidateVisual(); Selected?.Invoke(nodes[index]);
        var target = targets.FirstOrDefault(t => t.Index == index);
        BringIntoView(new Rect(target.Point.X - 20, target.Point.Y - 20, 40, 40));
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus(); e.Handled = true; var point = e.GetPosition(this);
        var target = targets.OrderBy(t => (t.Point - point).LengthSquared).FirstOrDefault();
        if (targets.Count > 0 && (target.Point - point).Length <= 20) Choose(target.Index);
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); targets.Clear();
        var ink = Foreground; var line = (Brush)FindResource("Line"); var accent = (Brush)FindResource("Accent");
        dc.DrawRectangle(Background, IsKeyboardFocused ? new Pen(accent, 2) : null, new Rect(0, 0, ActualWidth, ActualHeight));
        void Text(string value, double x, double y, double width = 170)
        {
            var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(1, width), MaxTextHeight = 34, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(text, new Point(x, y));
        }
        if (until == default) { Text("等待加载观察历史…", 20, 65, ActualWidth - 40); return; }
        Text(since.ToLocalTime().ToString("MM-dd HH:mm"), 220, 10);
        Text(until.ToLocalTime().ToString("MM-dd HH:mm"), Math.Max(400, ActualWidth - 150), 10, 140);
        if (nodes.Count == 0) { Text("该范围暂无可显示变化，请调整筛选或开始观察。", 20, 65, ActualWidth - 40); return; }
        double y = 55;
        var projectAnchors = new Dictionary<string, double>();
        foreach (var lane in nodes.Select((node, index) => (Node: node, Index: index)).GroupBy(n => Lane(n.Node)))
        {
            var first = lane.First().Node;
            var name = projects.FirstOrDefault(p => p.Id.ToString() == first.Project)?.Name ?? first.Project;
            var session = sessions.FirstOrDefault(s => first.Evidence.Sessions.Contains(s.Id));
            Text(name, 12, y - 10, 160);
            if (session is not null && first.Project is not ("共享" or "未知")) Text(session.Tool + " · " + session.Label, 28, y + 8, 170);
            var pen = new Pen(line, 1.5);
            if (first.Evidence.Kind != EvidenceKind.Context) pen.DashStyle = DashStyles.Dash;
            if (first.Evidence.Kind == EvidenceKind.Context)
            {
                if (projectAnchors.TryGetValue(first.Project, out var parentY)) dc.DrawLine(new Pen(accent, 2), new Point(180, parentY), new Point(180, y));
                else projectAnchors[first.Project] = y;
            }
            dc.DrawLine(new Pen(accent, 2), new Point(180, y - 12), new Point(180, y + 12));
            dc.DrawLine(pen, new Point(180, y), new Point(ActualWidth - 15, y));
            var rows = new Dictionary<DateTimeOffset, int>();
            foreach (var entry in lane)
            {
                var row = rows.GetValueOrDefault(entry.Node.Time); rows[entry.Node.Time] = row + 1;
                var fraction = (entry.Node.Time - since).TotalSeconds / Math.Max(1, (until - since).TotalSeconds);
                var point = new Point(220 + Math.Clamp(fraction, 0, 1) * Math.Max(1, ActualWidth - 265), y + row * 30);
                if (row > 0) dc.DrawLine(pen, new Point(point.X, y), point);
                var marker = new Pen(entry.Index == selected ? accent : ink, entry.Index == selected ? 3 : 1);
                if (entry.Node.Evidence.Kind == EvidenceKind.Shared) dc.DrawRectangle(Background, marker, new Rect(point.X - 6, point.Y - 6, 12, 12));
                else dc.DrawEllipse(Background, marker, point, 6, 6);
                targets.Add((entry.Index, point));
            }
            y += 36 + rows.Values.Max() * 30;
        }
        FrameRendered?.Invoke(nodes);
    }
}

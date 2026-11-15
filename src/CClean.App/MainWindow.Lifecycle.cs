using System.ComponentModel;
using System.Windows;
using CClean.App.Lifecycle;

namespace CClean.App;

public partial class MainWindow
{
    private TrayIconHost? tray;
    private Task? exitTask;
    private bool exitRequested, canClose;
    private WindowState restoredState = WindowState.Normal;
    internal bool TrayAvailable => tray?.IsAvailable == true;
    internal bool ExitReady => canClose;

    private void InitializeLifecycle()
    {
        Closing += HandleClosing;
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) restoredState = WindowState; };
        Loaded += (_, _) =>
        {
            if (exitRequested) return;
            var actions = new TrayIconHost.TrayActions(RestoreWindow, () => _ = ToggleObservation(),
                () => { RestoreWindow(); OpenSettings(this, new RoutedEventArgs()); }, () => _ = RequestExit(), TrayFailure);
            if (!TrayIconHost.TryCreate(Dispatcher, actions, out tray, out var error))
                TrayFailure(error ?? "系统托盘不可用。");
            UpdateTray();
        };
        Application.Current.SessionEnding += HandleSessionEnding;
    }

    internal void RestoreWindow()
    {
        if (canClose) return;
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = restoredState;
        Activate();
    }
    private void TrayFailure(string error)
    {
        if (canClose) return;
        RestoreWindow();
        Status.Text = "托盘不可用，窗口保持可见；请使用“退出 CClean”停止程序。" + error;
    }
    private void UpdateTray() => tray?.Update(workspace?.Status ?? "正在读取本地配置…",
        workspace?.ObservationPaused == true, workspace is not null && !exitRequested, exitRequested);

    private async Task ToggleObservation()
    {
        if (workspace is null || exitRequested) return;
        try { await workspace.SetObservationPaused(!workspace.ObservationPaused); }
        catch (Exception error) { Status.Text = "观察状态未完成切换：" + error.Message; }
        UpdateTray();
    }
    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (canClose) return;
        e.Cancel = true;
        if (exitRequested) return;
        if (tray?.IsAvailable == true) { Hide(); ShowInTaskbar = false; }
        else TrayFailure(tray?.LastError ?? "未建立托盘入口。");
    }
    private void ExitClicked(object sender, RoutedEventArgs e) => _ = RequestExit();

    internal Task RequestExit()
    {
        if (canClose) return Task.CompletedTask;
        if (exitTask is { IsCompleted: false }) return exitTask;
        return exitTask = ExitCore();
    }
    private async Task ExitCore()
    {
        try
        {
            exitRequested = true;
            IsEnabled = false;
            Status.Text = "正在退出：停止后续清理，等待当前项与后台保存收尾…";
            capacityTimer.Stop(); closing.Cancel();
            workspace?.BeginShutdown();
            overview?.CancelPendingOperations(); workbench?.CancelPendingOperations(); activities?.CancelPendingOperations(); projectSetup?.CancelPendingOperations(); cleanupHistory?.CancelPendingOperations(); cleanup?.CancelPendingOperations();
            // Unloading also cancels a pending preview; the approved executor remains owned by Workspace.
            PageContent.Content = null;
            UpdateTray();
            await Task.WhenAll(overview?.WaitForPendingOperationsAsync() ?? Task.CompletedTask,
                workbench?.WaitForPendingOperationsAsync() ?? Task.CompletedTask,
                activities?.WaitForPendingOperationsAsync() ?? Task.CompletedTask,
                projectSetup?.WaitForPendingOperationsAsync() ?? Task.CompletedTask,
                cleanupHistory?.WaitForPendingOperationsAsync() ?? Task.CompletedTask,
                cleanup?.WaitForPendingOperationsAsync() ?? Task.CompletedTask);
            if (workspace is not null) await workspace.ShutdownAsync();
            tray?.Dispose(); tray = null;
            canClose = true;
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            // An incomplete drain must not release audit storage or announce a completed exit.
            try
            {
                RestoreWindow(); IsEnabled = true;
                Status.Text = "退出尚未完成，程序保持停止请求状态：" + error.Message;
            }
            catch { /* The dispatcher/window may already be shutting down; no new work is started. */ }
            exitTask = null;
        }
    }
    private void HandleSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (canClose) return;
        _ = RequestExit();
        // Idle synchronous completion can proceed; defer only an incomplete drain, never replay a logout.
        e.Cancel = !canClose;
    }
    private void CompleteClosed()
    {
        Application.Current.SessionEnding -= HandleSessionEnding;
        capacityTimer.Stop(); closing.Cancel();
        tray?.Dispose(); tray = null;
        if (canClose) workspace?.Dispose();
        closing.Dispose();
    }
}

using System.Windows;
using System.Windows.Threading;
using TjClient.Core;

namespace TjClient.Wpf;

static class WindowTests
{
    public static void Cancellation() => Run().GetAwaiter().GetResult();
    private static async Task Run()
    {
        using var fixture = new Fixture();
        await using var server = await BodyServer.Start();
        using var http = TestTls.Create(server.Ca);
        using (var warm = new CollectorClient(server.Address, "synthetic", http)) await warm.Status();
        server.HangStatusBody = true;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(fixture.Root, true, value => new CollectorClient(value.Endpoint, value.Token, http));
            app.Dispatcher.BeginInvoke(async () =>
            {
                try { await window.CheckCancellationRegression(server, completed); }
                catch (Exception error) { completed.TrySetException(error); window.DisposeTestWindow(); app.Shutdown(); }
            });
            app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { thread.Join(TimeSpan.FromSeconds(3)); }
    }
}

// Компилируется только тестами вместе с неизменёнными XAML и code-behind окна.
public partial class MainWindow
{
    internal async Task CheckCancellationRegression(BodyServer server, TaskCompletionSource completed)
    {
        FolderBox.Text = stateDirectory;
        EndpointBox.Text = server.Address;
        TokenBox.Password = "synthetic";
        CheckClick(this, new RoutedEventArgs());
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(busy);
        Pause();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (busy && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
        Assert.True(!busy && CheckButton.IsEnabled, "Пауза должна отменять проверку и освобождать кнопки");
        Assert.True(cancellation == null);
        Assert.True(checkedProfile == null && RecipientCheck.IsChecked != true);
        await OversizeRetryRegression(server);
        // Имитация зависшего файлового ввода: выход не должен ждать busy бесконечно.
        SetBusy(true);
        Closed += (_, _) => completed.TrySetResult();
        Exit();
    }
    internal void DisposeTestWindow()
    {
        cancellation?.Cancel();
        exiting = true;
        timer.Stop();
        tray.Dispose();
        Close();
    }
    private async Task OversizeRetryRegression(BodyServer server)
    {
        server.HangStatusBody = false;
        server.Collector.MaxFileBytes = 2;
        var root = Path.Combine(stateDirectory, "exports");
        var path = Path.Combine(root, "rphost_1", "26112501.log");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "synthetic oversized file");
        profile = new ClientProfile { Folder = root, Endpoint = server.Address, Token = "synthetic", SourceId = 1,
            Mode = InitialMode.ModifiedSince, Since = DateTimeOffset.MinValue }.Approve();
        FinishedCheck.IsChecked = true;
        RecipientCheck.IsChecked = true;
        using (var queue = OpenQueue(profile)) queue.BeginObservation(root, [], profile.Mode, profile.Since);
        await Cycle();
        using (var queue = OpenQueue(profile))
        {
            Assert.Equal(QueueState.Error, queue.All().Single().State);
            ShowQueue(queue.All());
        }
        Assert.True(RetryButton.IsEnabled);
        server.Collector.MaxFileBytes = 100000;
        await Cycle();
        Assert.Equal(0, server.Collector.Registrations);
        QueueGrid.SelectedIndex = 0;
        await ClickRetryWithAnswer(false);
        using (var queue = OpenQueue(profile)) Assert.Equal(QueueState.Error, queue.All().Single().State);
        File.Move(path, path + ".holding");
        QueueGrid.SelectedIndex = 0;
        await ClickRetryWithAnswer(true);
        using (var queue = OpenQueue(profile)) Assert.Equal(QueueState.Error, queue.All().Single().State);
        Assert.Equal(0, server.Collector.Registrations);
        File.Move(path + ".holding", path);
        QueueGrid.SelectedIndex = 0;
        await ClickRetryWithAnswer(true);
        using (var queue = OpenQueue(profile)) Assert.Equal(QueueState.Delivered, queue.All().Single().State);
        Assert.Equal(1, server.Collector.Registrations);
        Assert.Equal(1, server.Collector.Completions);
    }
    private async Task ClickRetryWithAnswer(bool yes)
    {
        var threadId = DialogAnswer.CurrentThreadId();
        var answer = Task.Run(() => DialogAnswer.Click(threadId, yes));
        RetryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        await answer;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (busy && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
        Assert.True(!busy, "Явный повтор должен завершаться");
    }
}

// Ответ только нашему модальному окну в указанном UI-потоке, без глобальных клавиш.
static class DialogAnswer
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    internal static extern uint CurrentThreadId();
    private delegate bool EnumWindow(nint window, nint parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, nint parameter);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int capacity);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDlgItem(nint window, int id);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    internal static void Click(uint thread, bool yes)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            var clicked = false;
            EnumThreadWindows(thread, (window, _) =>
            {
                var title = new System.Text.StringBuilder(128);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString() != "Явный повтор") return true;
                var button = GetDlgItem(window, yes ? 6 : 7);
                if (button != 0) clicked = PostMessage(button, 0x00F5, 0, 0);
                return !clicked;
            }, 0);
            if (clicked) return;
            Thread.Sleep(25);
        }
        throw new Exception("Не найден собственный диалог подтверждения повтора");
    }
}

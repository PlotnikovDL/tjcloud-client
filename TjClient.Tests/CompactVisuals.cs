using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TjClient.Core;

namespace TjClient.Wpf;

// Только тестовая сборка: собственные окна, синтетические строки и loopback HTTPS.
static class CompactVisuals
{
    internal static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output);
        using var fixture = new Fixture();
        await using var server = await BodyServer.Start();
        using var http = TestTls.Create(server.Ca);
        using (var warm = new CollectorClient(server.Address, "synthetic", http)) await warm.Status();
        CompactWindowTests.InSta(async () =>
        {
            var window = new MainWindow(fixture.Root, false, value => new CollectorClient(value.Endpoint, value.Token, http))
                { ShowActivated = false, Title = "TJ Cloud Client - изолированная визуальная проверка" };
            try
            {
                window.Show();
                await Task.Delay(80);
                await window.CaptureCompactStates(output, server);
            }
            finally { window.DisposeTestWindow(); }
            var saved = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost:19443", Token = "synthetic", SourceId = 7,
                Mode = InitialMode.ModifiedSince, Since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Startup = true }.Approve();
            ProtectedStore.Save(Path.Combine(fixture.Root, "settings.dpapi"), saved);
            var reopened = new MainWindow(fixture.Root, false, _ => throw new Exception("Сеть не разрешена при открытии сохранённого профиля")) { ShowActivated = false };
            try { reopened.Show(); reopened.AssertSavedProfile(saved); await reopened.CaptureSavedCompactProfile(output); }
            finally { reopened.DisposeTestWindow(); }
        });
        Console.WriteLine("PASS actual-wpf-layout main/minimum 100/125/150 closed/open/date/menu/paused/check-cancel synthetic-queue");
        return 0;
    }
}

public partial class MainWindow
{
    internal async Task CaptureSavedCompactProfile(string output)
    {
        SettingsExpander.IsExpanded = true;
        await CaptureLayout(output, "11-saved-dev-profile-date");
    }
    internal async Task CaptureCompactStates(string output, BodyServer server)
    {
        await CaptureLayout(output, "01-new-closed");
        SettingsExpander.IsExpanded = true;
        await CaptureLayout(output, "02-new-settings");
        FolderBox.Text = Path.Combine(stateDirectory, "synthetic-finished-exports", "long-folder-name-for-layout-verification", "another-long-folder-component");
        Directory.CreateDirectory(FolderBox.Text);
        EndpointBox.Text = server.Address;
        TokenBox.Password = "synthetic";
        SinceRadio.IsChecked = true;
        SincePicker.SelectedDate = new DateTime(2026, 1, 1);
        await CaptureLayout(output, "03-existing-date");
        Assert.True(SinceRadio.IsChecked == true && NewFilesRadio.IsChecked == false && SincePicker.IsEnabled);
        foreach (var width in new[] { 960d, MinWidth })
        {
            Width = width;
            Height = width == MinWidth ? MinHeight : 760;
            foreach (var scale in new[] { 1d, 1.25, 1.5 })
                await CaptureLayout(output, $"04-layout-{width}-{scale * 100:0}", scale);
        }
        Width = 960; Height = 760;
        server.HangStatusBody = true;
        CheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await server.BodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(busy && PauseButton.IsVisible && PauseButton.IsEnabled && !StartButton.IsVisible);
        await CaptureLayout(output, "05-check-busy");
        PauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (busy && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.True(!busy && StatusText.Text.Contains("отменена"));
        await CaptureLayout(output, "06-paused-after-cancel");
        server.HangStatusBody = false;
        CheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        while (busy) await Task.Delay(20);
        Assert.True(checkedProfile != null && RecipientText.Text.EndsWith(" - проверен"));
        Assert.True(!RecipientText.Text.Contains(FolderBox.Text) && !RecipientText.Text.Contains("ID"));
        RecipientCheck.IsChecked = true;
        ShowQueue(new[]
        {
            new QueueItem(new LocalFile("rphost_1/26112501.log", 4507, 0, 0), Offset: 4507, State: QueueState.Delivered, JobId: 22),
            new QueueItem(new LocalFile("rphost_1/26112502.log", 9000, 0, 0), Offset: 9000, State: QueueState.Importing, JobId: 23),
            new QueueItem(new LocalFile("rphost_1/26112503.log", 12000, 0, 0), Offset: 12000, State: QueueState.Imported, JobId: 24),
            new QueueItem(new LocalFile("rphost_2/26112504.log", 16000, 0, 0), State: QueueState.Error, Error: "Превышен лимит. После устранения причины нужен явный повтор."),
            new QueueItem(new LocalFile("rphost_2/26112505.log", 2147483648, 0, 0), Offset: 1073741824, State: QueueState.Uploading)
        });
        var rows = QueueGrid.Items.Cast<QueueRow>().ToList();
        Assert.Equal("Получено сервером", rows[0].State);
        Assert.True(rows[0].Details.Contains("Задание: 22") && rows[3].Details.Contains("лимит"));
        Assert.True(!ModeCaption.Text.Contains("импорт", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("всего 5", TotalText.Text);
        QueueGrid.SelectedIndex = 3;
        SettingsExpander.IsExpanded = false;
        await CaptureLayout(output, "07-queue-closed");
        MoreButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Task.Delay(80);
        Assert.True(MoreButton.ContextMenu.IsOpen);
        SaveVisual(MoreButton.ContextMenu, Path.Combine(output, "08-more-menu.png"), 1);
        MoreButton.ContextMenu.IsOpen = false;
        SettingsExpander.IsExpanded = true;
        existingQueue = true;
        SetBusy(false);
        await CaptureLayout(output, "09-queue-locked");
        Width = MinWidth; Height = MinHeight;
        await CaptureLayout(output, "10-queue-minimum-150", 1.5);
    }

    private async Task CaptureLayout(string output, string name, double scale = 1)
    {
        await Task.Delay(80);
        UpdateLayout();
        Assert.True(QueueGrid.ActualHeight >= 85, "Таблица должна оставаться видимой при раскрытых настройках");
        foreach (var control in new FrameworkElement[] { FolderBox, TokenBox, RecipientText, RecipientCheck,
            CheckButton, StartButton, PauseButton, MoreButton, QueueGrid, TotalText, LastContactText, SincePicker, StartupBox, ModeCaption })
        {
            if (!control.IsVisible) continue;
            var bounds = control.TransformToAncestor(RootPanel).TransformBounds(new Rect(control.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= RootPanel.ActualWidth + 1 && bounds.Bottom <= RootPanel.ActualHeight + 1,
                "Элемент обрезан: " + control.Name);
        }
        if (SincePicker.IsVisible)
        {
            var dateBounds = SincePicker.TransformToAncestor(RootPanel).TransformBounds(new Rect(SincePicker.RenderSize));
            var radioBounds = SinceRadio.TransformToAncestor(RootPanel).TransformBounds(new Rect(SinceRadio.RenderSize));
            Assert.True(Math.Abs(dateBounds.Top - radioBounds.Top) < 1 && Math.Abs(dateBounds.Height - radioBounds.Height) < 1, "Дата и режим на одной базовой строке");
            var text = (System.Windows.Controls.Primitives.DatePickerTextBox)SincePicker.Template.FindName("PART_TextBox", SincePicker);
            Assert.Equal(HorizontalAlignment.Center, text.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, text.VerticalContentAlignment);
            Assert.Equal(TextAlignment.Center, text.TextAlignment);
        }
        SaveVisual(this, Path.Combine(output, name + ".png"), scale);
    }

    private static void SaveVisual(FrameworkElement visual, string path, double scale)
    {
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}

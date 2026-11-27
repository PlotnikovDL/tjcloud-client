using System.Windows;
using System.Windows.Threading;
using TjClient.Core;

namespace TjClient.Wpf;

static class CompactWindowTests
{
    public static void ProfileDefaults() => InSta(async () =>
    {
        using var fixture = new Fixture();
        var calls = 0;
        CollectorClient NoNetwork(ClientProfile _) { calls++; throw new Exception("Сеть не разрешена в этом тесте"); }
        var fresh = new MainWindow(fixture.Root, false, NoNetwork) { ShowActivated = false };
        try
        {
            fresh.Show();
            await Task.Delay(50);
            fresh.AssertNewProfile();
            Assert.Equal(0, calls);
            Assert.True(!File.Exists(Path.Combine(fixture.Root, "settings.dpapi")), "Открытие нового профиля не сохраняет настройки");
        }
        finally { fresh.DisposeTestWindow(); }
        var saved = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost:19443", Token = "synthetic",
            SourceId = 7, SourceName = "Синтетический источник", Mode = InitialMode.ModifiedSince,
            Since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Startup = true }.Approve();
        var path = Path.Combine(fixture.Root, "settings.dpapi");
        ProtectedStore.Save(path, saved);
        var original = File.ReadAllBytes(path);
        var existing = new MainWindow(fixture.Root, false, NoNetwork) { ShowActivated = false };
        try
        {
            existing.Show();
            await Task.Delay(50);
            existing.AssertSavedProfile(saved);
            Assert.Equal(0, calls);
            Assert.True(original.SequenceEqual(File.ReadAllBytes(path)), "Открытие сохранённого профиля не меняет DPAPI-файл");
        }
        finally { existing.DisposeTestWindow(); }
    });

    public static void ModesAndDate() => InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try { window.AssertModesAndDate(); }
        finally { window.DisposeTestWindow(); }
        var saved = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost:19443", Token = "synthetic", SourceId = 7,
            Mode = InitialMode.ModifiedSince, Since = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }.Approve();
        ProtectedStore.Save(Path.Combine(fixture.Root, "settings.dpapi"), saved);
        using (var queue = new QueueStore(Path.Combine(fixture.Root, saved.QueueKey + ".db")))
            queue.BeginObservation(saved.Folder, [], saved.Mode, saved.Since);
        var existing = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try { existing.AssertPausedPeriod(); }
        finally { existing.DisposeTestWindow(); }
        return Task.CompletedTask;
    });

    public static void CompactSurface() => InSta(() =>
    {
        using var fixture = new Fixture();
        var window = new MainWindow(fixture.Root, false) { ShowActivated = false };
        try { window.Show(); window.AssertCompactSurface(); }
        finally { window.DisposeTestWindow(); }
        return Task.CompletedTask;
    });

    internal static void InSta(Func<Task> test)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(async () =>
            {
                try { await test(); completed.TrySetResult(); }
                catch (Exception error) { completed.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { completed.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); }
        finally { thread.Join(TimeSpan.FromSeconds(3)); }
    }
}

public partial class MainWindow
{
    internal void AssertCompactSurface()
    {
        Assert.True(FindName("SettingsExpander") is System.Windows.Controls.Expander, "Настройки должны быть раскрываемыми");
        var expander = (System.Windows.Controls.Expander)FindName("SettingsExpander");
        Assert.True(!expander.IsExpanded);
        Assert.True(FolderBox.IsVisible && TokenBox.IsVisible && RecipientText.IsVisible && !EndpointBox.IsVisible);
        Assert.True(RecipientText.Text.Contains("https://app.tjcloud.ru") && RecipientText.Text.Contains("не проверен"));
        Assert.True(FinishedCheck.IsVisible && RecipientCheck.IsVisible);
        Assert.True(FinishedCheck.ToolTip?.ToString()?.Contains("Активная папка ТЖ") == true);
        Assert.Equal(3, QueueGrid.Columns.Count);
        Assert.Equal("Файл", QueueGrid.Columns[0].Header.ToString());
        Assert.Equal("Передано", QueueGrid.Columns[2].Header.ToString());
        Assert.True(FindName("SendButton") is System.Windows.Controls.MenuItem && FindName("OpenButton") is System.Windows.Controls.MenuItem && FindName("RetryButton") is System.Windows.Controls.MenuItem);
        Assert.True(StartButton.IsVisible && !PauseButton.IsVisible);
        SetBusy(true);
        Assert.True(!StartButton.IsVisible && PauseButton.IsVisible && PauseButton.IsEnabled && !CheckButton.IsEnabled);
        Assert.Equal("Отменить", PauseButton.Content.ToString());
        SetBusy(false);
        running = true;
        SetBusy(false);
        Assert.True(!StartButton.IsVisible && PauseButton.IsVisible && PauseButton.IsEnabled);
        Assert.Equal("Пауза", PauseButton.Content.ToString());
        Pause();
        Assert.True(StartButton.IsVisible && !PauseButton.IsVisible);
        expander.IsExpanded = true;
        UpdateLayout();
        Assert.True(EndpointBox.IsVisible);
        EndpointBox.Text = "https://userinfo:synthetic@example.org";
        Assert.True(!RecipientText.Text.Contains("userinfo") && !RecipientText.Text.Contains("synthetic") && !RecipientText.Text.Contains("example.org"));
        EndpointBox.Text = "https://localhost:19443";
        FolderBox.Text = @"C:\synthetic\exports";
        TokenBox.Password = "synthetic";
        Assert.True(!RecipientText.Text.Contains(FolderBox.Text) && !RecipientText.Text.Contains("ID"));
        Assert.True(RecipientText.Text.Contains("https://localhost:19443"));
        Assert.True(RecipientCheck.IsChecked != true && !RecipientCheck.IsEnabled);
        var more = (System.Windows.Controls.Button)FindName("MoreButton");
        more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.True(more.ContextMenu.IsOpen, "Ещё открывает нативное меню");
        Assert.Equal(4, more.ContextMenu.Items.Count);
        more.ContextMenu.IsOpen = false;
    }
    internal void AssertModesAndDate()
    {
        SinceRadio.IsChecked = true;
        Assert.True(NewFilesRadio.IsChecked == false, "Режимы должны быть взаимоисключающими");
        NewFilesRadio.IsChecked = true;
        Assert.True(SinceRadio.IsChecked == false && !SincePicker.IsEnabled, "Дата недоступна в режиме новых файлов");
        SinceRadio.IsChecked = true;
        Assert.True(SincePicker.IsEnabled);
        SincePicker.SelectedDate = new DateTime(2026, 1, 1);
        SincePicker.ApplyTemplate();
        var text = (System.Windows.Controls.Primitives.DatePickerTextBox)SincePicker.Template.FindName("PART_TextBox", SincePicker);
        Assert.Equal(HorizontalAlignment.Center, text.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Center, text.VerticalContentAlignment);
        Assert.Equal(TextAlignment.Center, text.TextAlignment);
        Assert.Equal(SinceRadio.Height, SincePicker.Height);
        SetBusy(true);
        Assert.True(!SincePicker.IsEnabled);
        SetBusy(false);
        Assert.True(SincePicker.IsEnabled);
    }
    internal void AssertPausedPeriod()
    {
        Assert.True(SinceRadio.IsChecked == true && NewFilesRadio.IsChecked == false);
        Assert.True(SinceRadio.IsEnabled && NewFilesRadio.IsEnabled && SincePicker.IsEnabled, "На паузе можно изменить сохранённый период");
        NewFilesRadio.IsChecked = true;
        Assert.True(!SincePicker.IsEnabled && RecipientCheck.IsChecked == false);
    }
    internal void AssertNewProfile()
    {
        Assert.Equal("https://app.tjcloud.ru", EndpointBox.Text);
        Assert.True(NewFilesRadio.IsChecked == true && SinceRadio.IsChecked == false);
        Assert.True(StartupBox.IsChecked == false && !running && !busy);
        Assert.True(TokenBox.Password.Length == 0 && RecipientCheck.IsChecked != true && FinishedCheck.IsChecked != true);
    }
    internal void AssertSavedProfile(ClientProfile saved)
    {
        Assert.Equal(saved.Endpoint, EndpointBox.Text);
        Assert.True(TokenBox.Password == saved.Token, "Сохранённый ключ не должен меняться");
        Assert.Equal(saved.Since?.LocalDateTime.Date, SincePicker.SelectedDate?.Date);
        Assert.True(SinceRadio.IsChecked == true && NewFilesRadio.IsChecked == false);
        Assert.True(StartupBox.IsChecked == true && RecipientCheck.IsChecked == true && FinishedCheck.IsChecked == true);
        Assert.True(profile.ConsentBinding == saved.ConsentBinding && !running && !busy);
    }
}

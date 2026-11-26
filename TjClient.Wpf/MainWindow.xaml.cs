using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using TjClient.Core;
using TjClient.Updates;
using Forms = System.Windows.Forms;

namespace TjClient.Wpf;

public partial class MainWindow : Window
{
    private readonly string stateDirectory;
    private readonly string settingsPath;
    private readonly bool smoke;
    private readonly Func<ClientProfile, CollectorClient> createClient;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ClientProfile profile = new();
    private ClientProfile? checkedProfile;
    private IReadOnlyList<LocalFile>? previewSnapshot;
    private CancellationTokenSource? cancellation;
    private bool loading = true, busy, running, exiting;
    private int failures;
    private DateTimeOffset nextCycle;
    private bool existingQueue;
    private bool updateQuiescing;
    private bool updateDrainFailed;
    private bool startIntentPending;
    private int activeOperations;
    private TaskCompletionSource operationsDrained = CompletedDrain();

    private static TaskCompletionSource CompletedDrain()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    // Leases cover the entire async UI operation, including queue/network disposal.
    // busy alone is not a completion signal: Start/Retry may clear it between awaits.
    private IDisposable? AdmitOperation()
    {
        Dispatcher.VerifyAccess();
        if (updateQuiescing || exiting) return null;
        if (activeOperations++ == 0)
            operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return new OperationLease(this);
    }

    private sealed class OperationLease(MainWindow owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (--owner.activeOperations == 0) owner.operationsDrained.TrySetResult();
        }
    }

    internal async Task<bool> PrepareForUpdateAsync(TimeSpan timeout, CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        if (updateQuiescing || exiting) return false;
        updateQuiescing = true;
        updateDrainFailed = false;
        timer.Stop();
        SetBusy(busy);
        try
        {
            cancellation?.Cancel();
            await operationsDrained.Task.WaitAsync(timeout, token);
            if (busy || exiting || updateDrainFailed || startIntentPending || token.IsCancellationRequested) throw new OperationCanceledException();
            return true;
        }
        catch
        {
            ResumeAfterUpdate();
            return false;
        }
    }

    internal void ResumeAfterUpdate()
    {
        Dispatcher.VerifyAccess();
        updateQuiescing = false;
        // Read the current intent, never restore a snapshot over a user pause/revocation.
        running = profile.Running && !startIntentPending;
        if (!exiting) timer.Start();
        SetBusy(busy);
    }

    private void OperationFailed(Exception error, bool pause = false)
    {
        if (updateQuiescing && error is not OperationCanceledException) updateDrainFailed = true;
        if (pause && !updateQuiescing) Pause();
        StatusText.Text = Friendly(error);
    }

    public MainWindow(string stateDirectory, bool smoke, Func<ClientProfile, CollectorClient>? createClient = null,
        Func<string, IUpdateLifetime, UpdateCoordinator>? createUpdates = null, Func<Task<bool>>? requestUpdateConsent = null)
    {
        this.stateDirectory = stateDirectory;
        this.smoke = smoke;
        this.createClient = createClient ?? (value => new CollectorClient(value.Endpoint, value.Token));
        settingsPath = Path.Combine(stateDirectory, "settings.dpapi");
        InitializeComponent();
        Title += " - " + typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        SincePicker.ApplyTemplate();
        if (SincePicker.Template.FindName("PART_TextBox", SincePicker) is System.Windows.Controls.Primitives.DatePickerTextBox dateText)
        {
            dateText.HorizontalContentAlignment = HorizontalAlignment.Center;
            dateText.VerticalContentAlignment = VerticalAlignment.Center;
            dateText.TextAlignment = TextAlignment.Center;
            dateText.Padding = new Thickness(0);
        }
        profile = ProtectedStore.Load<ClientProfile>(settingsPath) ?? new() { Endpoint = "https://app.tjcloud.ru" };
        FolderBox.Text = profile.Folder;
        EndpointBox.Text = profile.Endpoint;
        TokenBox.Password = profile.Token;
        IntervalBox.Text = profile.IntervalSeconds.ToString();
        NewFilesRadio.IsChecked = profile.Mode == InitialMode.NewFiles;
        SinceRadio.IsChecked = profile.Mode == InitialMode.ModifiedSince;
        SincePicker.SelectedDate = profile.Since?.LocalDateTime;
        StartupBox.IsChecked = profile.Startup;
        try { profile.RequireConsent(); FinishedCheck.IsChecked = true; RecipientCheck.IsChecked = true; } catch (InvalidOperationException) { }
        FolderBox.TextChanged += (_, _) =>
        {
            if (loading) return;
            profile = profile.ChangeFolder(FolderBox.Text);
            loading = true; NewFilesRadio.IsChecked = true; SincePicker.SelectedDate = null; loading = false;
            FinishedCheck.IsChecked = false;
            InvalidateCheck(true);
        };
        EndpointBox.TextChanged += (_, _) =>
        {
            if (loading) return;
            profile = profile.ChangeEndpoint(EndpointBox.Text);
            loading = true; TokenBox.Password = ""; loading = false;
            InvalidateCheck(true);
        };
        TokenBox.PasswordChanged += (_, _) => InvalidateCheck();
        IntervalBox.TextChanged += (_, _) => InvalidateCheck();
        NewFilesRadio.Checked += (_, _) => InvalidateCheck();
        SinceRadio.Checked += (_, _) => InvalidateCheck();
        SincePicker.SelectedDateChanged += (_, _) => InvalidateCheck();
        StartupBox.Click += (_, _) => SetStartup();
        FinishedCheck.Unchecked += (_, _) => RevokeConsent();
        RecipientCheck.Unchecked += (_, _) => RevokeConsent();
        using (var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("TjCloudClient.Branding.Icon")
            ?? throw new InvalidOperationException("Не найдена иконка приложения."))
        {
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconStream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            Icon.Freeze();
            iconStream.Position = 0;
            using var sourceIcon = new System.Drawing.Icon(iconStream);
            trayIcon = (System.Drawing.Icon)sourceIcon.Clone();
        }
        tray = new Forms.NotifyIcon { Text = "TJ Cloud Client - на паузе", Icon = trayIcon, Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        menu.Items.Add("Пауза", null, (_, _) => Dispatcher.Invoke(Pause));
        menu.Items.Add("Выход", null, async (_, _) => await Dispatcher.InvokeAsync(Exit));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        Closing += OnClosing;
        Closed += (_, _) => { tray.Dispose(); trayIcon.Dispose(); };
        timer.Tick += async (_, _) => { if (running && !busy && DateTimeOffset.UtcNow >= nextCycle) await Cycle(); };
        timer.Start();
        loading = false;
        if (Path.IsPathFullyQualified(profile.Folder))
        {
            try
            {
                if (File.Exists(Path.Combine(stateDirectory, profile.QueueKey + ".db")))
                {
                    using var queue = OpenQueue(profile);
                    existingQueue = queue.Initialized;
                }
            }
            catch (Exception error) { StatusText.Text = Friendly(error); }
        }
        SetBusy(false);
        UpdateRecipient();
        ConfigureUpdates(createUpdates, requestUpdateConsent);
        Loaded += async (_, _) =>
        {
            if (smoke) { await Smoke(); return; }
            BeginUpdates();
            if (profile.Running)
            {
                try { profile.RequireConsent(); running = true; await Cycle(); }
                catch { Pause(); StatusText.Text = "Нужно заново проверить параметры и подтвердить передачу."; }
            }
        };
    }

    private void InvalidateCheck(bool resetQueue = false)
    {
        if (loading) return;
        checkedProfile = null; previewSnapshot = null;
        if (resetQueue) existingQueue = false;
        RecipientCheck.IsChecked = false; RecipientCheck.IsEnabled = false;
        UpdateModeAvailability();
        UpdateRecipient();
        profile = profile with { ConsentBinding = "", Running = false };
    }
    private void UpdateRecipient()
    {
        try
        {
            var endpoint = EndpointPolicy.Parse(EndpointBox.Text.Trim());
            RecipientText.Text = endpoint.AbsoluteUri + (checkedProfile != null ? " - проверен" : " - не проверен");
        }
        catch (ArgumentException) { RecipientText.Text = "Адрес недопустим. Укажите HTTPS-адрес без логина и параметров в настройках."; }
    }
    private ClientProfile ReadFields()
    {
        if (!int.TryParse(IntervalBox.Text, out var interval) || interval is < 5 or > 86400) throw new InvalidOperationException("Интервал - целое число от 5 до 86400 секунд.");
        var endpoint = EndpointPolicy.Parse(EndpointBox.Text.Trim());
        if (!Path.IsPathFullyQualified(FolderBox.Text.Trim())) throw new InvalidOperationException("Укажите полный локальный или UNC-путь.");
        return profile with { Folder = Path.GetFullPath(FolderBox.Text.Trim()), Endpoint = endpoint.AbsoluteUri, Token = TokenBox.Password,
            IntervalSeconds = interval, Mode = SinceRadio.IsChecked == true ? InitialMode.ModifiedSince : InitialMode.NewFiles,
            Since = SincePicker.SelectedDate is DateTime date ? new DateTimeOffset(date) : null, Startup = StartupBox.IsChecked == true };
    }
    private void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите каталог завершённых выгрузок", Multiselect = false };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }
    private async void CheckClick(object sender, RoutedEventArgs e)
    {
        if (busy || running) return;
        using var operation = AdmitOperation();
        if (operation == null) return;
        SetBusy(true);
        checkedProfile = null; previewSnapshot = null;
        UpdateRecipient();
        StatusText.Text = "Проверка параметров и завершённых выгрузок...";
        RecipientCheck.IsChecked = false; RecipientCheck.IsEnabled = false;
        cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        try
        {
            var candidate = ReadFields();
            using var api = createClient(candidate);
            var status = await api.Status(ct);
            if (status.ProtocolVersion != 1 || status.ChunkBytes is < 1 or > 4194304 || status.SourceId <= 0) throw new InvalidOperationException("Несовместимый протокол сервера.");
            candidate = candidate with { SourceId = status.SourceId, SourceName = status.SourceName, Running = false, ConsentBinding = "" };
            var snapshot = await Task.Run(() => LogScanner.Scan(candidate.Folder), ct);
            ct.ThrowIfCancellationRequested();
            using var queue = OpenQueue(candidate);
            existingQueue = queue.Initialized;
            var known = queue.All();
            var knownPaths = known.Select(x => x.File.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected = existingQueue
                ? known.Where(x => x.State is QueueState.Pending or QueueState.Uploading or QueueState.Retry).Select(x => x.File).Concat(queue.SelectForDiscovery(snapshot).Where(x => !knownPaths.Contains(x.RelativePath))).ToList()
                : PreviewPlan.Select(candidate, snapshot);
            await Task.Run(() => { foreach (var file in selected) { ct.ThrowIfCancellationRequested(); using var locked = new LockedLog(candidate.Folder, file); locked.Verify(); } }, ct);
            ct.ThrowIfCancellationRequested();
            previewSnapshot = snapshot;
            checkedProfile = candidate;
            profile = candidate;
            UpdateRecipient();
            RecipientCheck.IsEnabled = true;
            RecipientCheck.IsChecked = false;

            StatusText.Text = existingQueue ? "Проверка пройдена. Сохранённая очередь будет продолжена, начальный режим уже закреплён." : "Проверка пройдена. Журналы не передавались. Подтвердите папку и получателя.";
            LastContactText.Text = $"Последняя успешная связь: {DateTime.Now:G}";
            ShowQueue(existingQueue ? known : selected.Select(x => new QueueItem(x)).ToList());
            SaveProfile();
        }
        catch (OperationCanceledException) { StatusText.Text = "Проверка отменена. Передача не начиналась."; }
        catch (Exception error) { OperationFailed(error); }
        finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
    }
    private async void StartClick(object sender, RoutedEventArgs e) => await Start(true);
    private async void SendClick(object sender, RoutedEventArgs e) => await Start(running);
    private async Task Start(bool schedule)
    {
        if (busy) return;
        using var operation = AdmitOperation();
        if (operation == null) return;
        try
        {
            if (checkedProfile == null || previewSnapshot == null) throw new InvalidOperationException("Сначала нажмите «Проверить».");
            if (FinishedCheck.IsChecked != true || RecipientCheck.IsChecked != true) throw new InvalidOperationException("Нужно подтвердить завершённость файлов и передачу получателю.");
            profile = checkedProfile.Approve() with { Running = schedule, Startup = StartupBox.IsChecked == true };
            profile.RequireConsent();
            startIntentPending = true;
            SetBusy(true);
            cancellation = new CancellationTokenSource();
            try
            {
                var selected = profile;
                var snapshot = previewSnapshot;
                await Task.Run(() => { using var queue = OpenQueue(selected); queue.BeginObservation(selected.Folder, snapshot, selected.Mode, selected.Since); });
                // Persist only after the baseline exists, using the latest intent (a Pause may have intervened).
                existingQueue = true;
                SaveProfile();
                startIntentPending = false;
                running = profile.Running;
                cancellation.Token.ThrowIfCancellationRequested();
            }
            finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
            await Cycle();
        }
        catch (Exception error) { OperationFailed(error); }
    }
    private async Task Cycle()
    {
        if (busy) return;
        using var operation = AdmitOperation();
        if (operation == null) return;
        SetBusy(true);
        cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        var active = profile;
        try
        {
            if (FinishedCheck.IsChecked != true || RecipientCheck.IsChecked != true) throw new InvalidOperationException("Согласие на передачу отозвано. Подтвердите папку и получателя.");
            active.RequireConsent();
            StatusText.Text = "Проверка очереди и доставка...";
            await Task.Run(async () =>
            {
                using var queue = OpenQueue(active);
                queue.BeginObservation(active.Folder, [], active.Mode, active.Since);
                using var api = createClient(active);
                var engine = new ClientEngine(active.Folder, active.SourceId, queue, api);
                var lastProgress = DateTimeOffset.MinValue;
                engine.Progress += () =>
                {
                    if ((DateTimeOffset.UtcNow - lastProgress).TotalMilliseconds < 350) return;
                    lastProgress = DateTimeOffset.UtcNow;
                    var rows = queue.All();
                    _ = Dispatcher.BeginInvoke(() => ShowQueue(rows));
                };
                try { await engine.Run(ct); }
                finally { var rows = queue.All(); _ = Dispatcher.BeginInvoke(() => ShowQueue(rows)); }
            }, ct);
            failures = 0;
            LastContactText.Text = $"Последняя успешная связь: {DateTime.Now:G}";
            StatusText.Text = running ? "Наблюдение включено. Принятие сервером и импорт отображаются отдельно." : "Отправка сейчас завершена. Периодическое наблюдение на паузе.";
        }
        catch (OperationCanceledException) { StatusText.Text = "На паузе. Подтверждённые смещения сохранены."; }
        catch (TransportException error) when (error.Transient)
        {
            if (updateQuiescing) updateDrainFailed = true;
            failures++;
            StatusText.Text = error.Message + " Повтор с ограниченной задержкой.";
        }
        catch (Exception error) { OperationFailed(error, pause: true); }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            nextCycle = DateTimeOffset.UtcNow.AddSeconds(Math.Max(profile.IntervalSeconds, failures == 0 ? 0 : Math.Min(900, 5 * Math.Pow(2, Math.Min(failures - 1, 8)))));
            SetBusy(false);
        }
    }
    private void RevokeConsent()
    {
        if (loading) return;
        profile = profile with { ConsentBinding = "" };
        Pause();
    }
    private void PauseClick(object sender, RoutedEventArgs e) => Pause();
    private void Pause()
    {
        running = false; cancellation?.Cancel();
        profile = profile with { Running = false };
        if (!loading) SaveProfile();
        startIntentPending = false;
        StatusText.Text = "На паузе. Подтверждённые смещения сохранены.";
        if (!busy) SetBusy(false);
    }
    private void OpenClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(new Uri(EndpointPolicy.Parse(EndpointBox.Text.Trim()), "/import").AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { StatusText.Text = Friendly(error); }
    }
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        MoreButton.ContextMenu.IsOpen = true;
    }
    private async void RetryClick(object sender, RoutedEventArgs e)
    {
        if (busy || QueueGrid.SelectedItem is not QueueRow row) return;
        using var operation = AdmitOperation();
        if (operation == null) return;
        try
        {
            profile.RequireConsent();
            using (var queue = OpenQueue(profile))
            {
                var item = queue.All().Single(x => x.File.RelativePath == row.Path);
                if (item.State is not (QueueState.Expired or QueueState.Error)) throw new InvalidOperationException("Повтор разрешён только для истёкшей передачи или исправленной ошибки. Конфликт нельзя перезаписать.");
                if (MessageBox.Show(this, "Повторить выбранную передачу с прежней идентичностью файла? Истёкшая передача будет зарегистрирована заново.", "Явный повтор", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                if (updateQuiescing || exiting) return;
                if (item.UploadId == null || item.State == QueueState.Expired)
                {
                    SetBusy(true);
                    cancellation = new CancellationTokenSource();
                    var ct = cancellation.Token;
                    var folder = profile.Folder;
                    try
                    {
                        await Task.Run(() =>
                        {
                            using var locked = new LockedLog(folder, item.File);
                            if (item.Hash.Length != 0 && locked.Hash() != item.Hash) throw new FileChangedException();
                            locked.Verify();
                        }, ct);
                        ct.ThrowIfCancellationRequested();
                    }
                    finally { cancellation.Dispose(); cancellation = null; SetBusy(false); }
                }
                queue.Save(item with { State = item.JobId != null ? QueueState.Delivered : QueueState.Pending, UploadId = item.State == QueueState.Expired ? null : item.UploadId,
                    Offset = item.State == QueueState.Expired ? 0 : item.Offset, Attempts = 0, NextAttemptTicks = 0, Error = "" });
            }
            await Cycle();
        }
        catch (Exception error) { OperationFailed(error); }
    }
    private QueueStore OpenQueue(ClientProfile value) => new(Path.Combine(stateDirectory, value.QueueKey + ".db"));
    private void SaveProfile() { if (!smoke) ProtectedStore.Save(settingsPath, profile); }
    private void UpdateModeAvailability()
    {
        ModePanel.IsEnabled = !existingQueue && !busy && !running && !updateQuiescing;
        SincePicker.IsEnabled = ModePanel.IsEnabled && SinceRadio.IsChecked == true;
        ModeCaption.Text = existingQueue ? "Период очереди закреплён" : SinceRadio.IsChecked == true ? "Существующие по дате изменения" : "Только новые файлы";
    }
    private void SetBusy(bool value)
    {
        busy = value;
        SourceSettings.IsEnabled = TransferSettings.IsEnabled = !value && !running && !updateQuiescing;
        UpdateModeAvailability();
        CheckButton.IsEnabled = !value && !running && !updateQuiescing;
        StartButton.IsEnabled = !value && !running && !updateQuiescing;
        StartButton.Visibility = value || running ? Visibility.Collapsed : Visibility.Visible;
        PauseButton.Visibility = value || running ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = value && !running ? "Отменить" : "Пауза";
        SendButton.IsEnabled = !value && !updateQuiescing;
        RetryButton.IsEnabled = !value && !running && !updateQuiescing;
        tray.Text = running ? "TJ Cloud Client - наблюдение" : "TJ Cloud Client - на паузе";
    }
    private void ShowQueue(IReadOnlyList<QueueItem> items)
    {
        QueueGrid.ItemsSource = items.Select(x => new QueueRow(x.File.RelativePath, StateName(x.State), x.Offset, x.File.Size, x.JobId?.ToString() ?? "", x.Error)).ToList();
        TotalText.Text = $"всего {items.Count}";
    }

    private static string StateName(QueueState state) => state switch
    {
        QueueState.Baseline => "Существовал до наблюдения", QueueState.Pending => "Ожидает отправки", QueueState.Uploading => "Передаётся", QueueState.Retry => "Ожидает повтора",
        QueueState.Delivered => "Получено сервером", QueueState.Importing => "Импортируется", QueueState.Imported => "Импортировано", QueueState.ImportFailed => "Ошибка импорта",
        QueueState.Expired => "Истекла - нужен повтор", QueueState.Conflict => "Конфликт - остановлено", _ => "Ошибка - остановлено"
    };
    private static string Friendly(Exception error) => error switch
    {
        TransportException or FileChangedException or QueueFullException or InvalidOperationException or ArgumentException => error.Message,
        UnauthorizedAccessException => "Нет доступа. Проверьте права текущего пользователя к папке или профилю.",
        IOException => "Ошибка чтения папки, файл занят, изменён или путь содержит ссылку. Передача остановлена.",
        _ => "Операция остановлена. Проверьте параметры, доступ к папке и состояние сервера. Секреты в диагностику не записываются."
    };
    private void SetStartup()
    {
        if (loading || smoke) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (StartupBox.IsChecked == true) key.SetValue("TJCloudClient", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("TJCloudClient", false);
            var actual = key.GetValue("TJCloudClient") as string;
            if ((actual != null) != (StartupBox.IsChecked == true)) throw new IOException();
            profile = profile with { Startup = StartupBox.IsChecked == true };
            SaveProfile();
        }
        catch { StartupBox.IsChecked = profile.Startup; StatusText.Text = "Не удалось изменить автозапуск текущего пользователя."; }
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } }
    private async void Exit()
    {
        exiting = true;
        await DisposeUpdatesAsync();
        Pause();
        var wait = Stopwatch.StartNew();
        while (busy && wait.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(50);
        exiting = true; timer.Stop(); Close(); Application.Current.Shutdown();
    }
    private async Task Smoke()
    {
        await Task.Delay(500);
        var ok = TokenBox is System.Windows.Controls.PasswordBox && NewFilesRadio.IsChecked == true && StartupBox.IsChecked == false && CheckButton.IsEnabled;
        await Start(true);
        ok &= !running && StatusText.Text.Contains("Проверить");
        loading = false;
        EndpointBox.Text = "https://localhost";
        TokenBox.Password = "synthetic";
        EndpointBox.Text = "https://other.example";
        ok &= TokenBox.Password.Length == 0 && RecipientCheck.IsChecked == false;
        profile = new ClientProfile { Folder = @"C:\synthetic", Endpoint = "https://localhost", Token = "synthetic", SourceId = 1 }.Approve();
        FinishedCheck.IsChecked = true;
        FinishedCheck.IsChecked = false;
        ok &= profile.ConsentBinding.Length == 0;
        FinishedCheck.IsChecked = true;
        FolderBox.Text = @"C:\synthetic";
        ok &= FinishedCheck.IsChecked == false;
        File.WriteAllText(Path.Combine(stateDirectory, "window-smoke.txt"), ok ? "PASS masked-token default-new startup-off origin-clears-token folder-clears-consent consent-revoked" : "FAIL");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var image = File.Create(Path.Combine(stateDirectory, "window-smoke.png"))) encoder.Save(image);
        await Task.Delay(3000);
        exiting = true; timer.Stop(); Close(); Application.Current.Shutdown(ok ? 0 : 1);
    }
}

public sealed record QueueRow(string Path, string State, long Offset, long Size, string Job, string Error)
{
    public string Progress
    {
        get
        {
            var (divisor, unit) = Math.Max(Offset, Size) switch
            {
                >= 1073741824 => (1073741824d, "ГиБ"),
                >= 1048576 => (1048576d, "МиБ"),
                >= 1024 => (1024d, "КиБ"),
                _ => (1d, "Б")
            };
            string Format(long value) => value > 0 && value / divisor < 0.1 ? "<0,1" : (value / divisor).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            return $"{Format(Offset)} / {Format(Size)} {unit}";
        }
    }
    public string Details => Path + $"\nПередано: {Offset:N0} / {Size:N0} Б" + (Job.Length == 0 ? "" : "\nЗадание: " + Job) + (Error.Length == 0 ? "" : "\n" + Error);
}

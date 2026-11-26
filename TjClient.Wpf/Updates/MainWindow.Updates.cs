using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using TjClient.Updates;

namespace TjClient.Wpf;

public partial class MainWindow : IUpdateLifetime
{
    private UpdateCoordinator? updates;
    private Func<Task<bool>>? updateConsentPrompt;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(24) };
    private Task updateWork = Task.CompletedTask;
    private Task updatePreferenceWork = Task.CompletedTask;
    private bool updatesStarted;
    private bool updateRevokeNotSaved;

    private void ConfigureUpdates(Func<string, IUpdateLifetime, UpdateCoordinator>? factory, Func<Task<bool>>? prompt)
    {
        updates = smoke ? null : factory?.Invoke(stateDirectory, this);
        updateConsentPrompt = prompt ?? (() => Task.FromResult(MessageBox.Show(this,
            "Разрешить автоматические обновления клиента?\n\nПодписанные пакеты загружаются из GitHub. Перед установкой клиент безопасно остановит передачу и перезапустится, сохранив профиль и очередь. Это отдельное согласие, оно не разрешает отправку журналов. Отключить можно в настройках.",
            "Обновления TJ Cloud Client", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes));
        AutoUpdateBox.IsEnabled = UpdateButton.IsEnabled = updates != null;
        AutoUpdateBox.IsChecked = updates?.Consent == true;
        AutoUpdateBox.Click += (_, _) => updatePreferenceWork = ChangeUpdateConsentAsync();
        updateTimer.Tick += (_, _) => QueueUpdateCheck(false);
        Closed += (_, _) => { updateTimer.Stop(); updatePreferenceWork = DisposeUpdatesAsync(); };
    }
    private void BeginUpdates()
    {
        if (updates == null || updatesStarted) return;
        updatesStarted = true;
        updateWork = InitializeUpdatesAsync();
    }
    private async Task InitializeUpdatesAsync()
    {
        try
        {
            await updates!.InitializeAsync(updateConsentPrompt!);
            AutoUpdateBox.IsChecked = updates.Consent == true;
            updateTimer.Start();
            if (updates.Consent == true) await CheckUpdatesAsync(false);
        }
        catch { SetUpdateStatus("Не удалось сохранить настройку обновлений. Автопроверка не запущена."); }
    }
    private async Task ChangeUpdateConsentAsync()
    {
        if (updates == null) return;
        try
        {
            await updates.SetConsentAsync(AutoUpdateBox.IsChecked == true);
            updateRevokeNotSaved = false;
            if (updates.Consent == true) QueueUpdateCheck(false);
            else SetUpdateStatus("Автоматические обновления отключены.");
        }
        catch
        {
            updateRevokeNotSaved = updates.Consent == false;
            SetUpdateStatus("Не удалось сохранить настройку обновлений.");
        }
        AutoUpdateBox.IsChecked = updates.Consent == true;
    }
    private void UpdateClick(object sender, RoutedEventArgs e) => QueueUpdateCheck(true);
    private void QueueUpdateCheck(bool manual)
    {
        if (updates == null || exiting) return;
        if (updates.Consent != true)
        {
            if (manual) SetUpdateStatus("Разрешите обновления в настройках. Без согласия запросы не отправляются.");
            return;
        }
        if (!updateWork.IsCompleted)
        {
            if (manual) SetUpdateStatus("Проверка или загрузка обновления уже выполняется.");
            return;
        }
        updateWork = CheckUpdatesAsync(manual);
    }
    private async Task CheckUpdatesAsync(bool manual)
    {
        try
        {
            if (manual) SetUpdateStatus("Проверка обновлений...");
            var result = await updates!.CheckAsync();
            if (exiting) return;
            SetUpdateStatus(result switch
            {
                UpdateOutcome.UpToDate when manual => "Установлена актуальная версия клиента.",
                UpdateOutcome.Failed when manual => "Не удалось проверить обновление или его подпись. Установка не начата.",
                UpdateOutcome.Deferred => "Обновление отложено: клиент пока нельзя безопасно перезапустить.",
                UpdateOutcome.Cancelled when manual => "Обновление отменено.",
                UpdateOutcome.InstallerLaunched => "Запущен установщик обновления. Ожидается перезапуск.",
                _ => ""
            });
        }
        catch { if (manual) SetUpdateStatus("Не удалось проверить обновления. Установка не начата."); }
    }
    private void SetUpdateStatus(string message)
    {
        if (updateRevokeNotSaved)
            message = "Не удалось сохранить настройку. Отключение обновлений действует только до перезапуска клиента.";
        UpdateStatusText.Text = message;
        UpdateStatusText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private async Task DisposeUpdatesAsync()
    {
        try { if (updates != null) await updates.DisposeAsync(); }
        catch { /* Shutdown must not turn an updater cleanup error into a process crash. */ }
    }
    Task<bool> IUpdateLifetime.PrepareAsync(CancellationToken token) => Dispatcher.InvokeAsync(async () =>
    {
        SetUpdateStatus("Обновление загружено. Безопасная остановка передачи...");
        return await PrepareForUpdateAsync(TimeSpan.FromSeconds(30), token);
    }).Task.Unwrap();
    Task IUpdateLifetime.ResumeAsync() => Dispatcher.InvokeAsync(() => ResumeAfterUpdate()).Task;
    Task IUpdateLifetime.LaunchAsync(Action launch, CancellationToken token) => Dispatcher.InvokeAsync(() =>
    {
        VerifyUpdateExit(token);
        launch();
    }).Task;
    private void VerifyUpdateExit(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!updateQuiescing || busy || activeOperations != 0 || exiting || startIntentPending)
            throw new InvalidOperationException("Выход для обновления не разрешён.");
    }
    Task IUpdateLifetime.CloseAsync() => ((IUpdateLifetime)this).CloseAsync(() => { }, CancellationToken.None);
    Task IUpdateLifetime.CloseAsync(Action commit, CancellationToken token) => Dispatcher.InvokeAsync(() =>
    {
        VerifyUpdateExit(token);
        commit();
        exiting = true;
        timer.Stop();
        updateTimer.Stop();
        Close();
        Application.Current?.Shutdown();
    }).Task;
}

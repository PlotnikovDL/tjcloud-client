namespace TjClient.Core;

public sealed class ClientEngine(string root, long sourceId, QueueStore queue, CollectorClient api)
{
    public event Action<QueueItem>? Progress;
    public async Task Run(CancellationToken ct = default)
    {
        queue.Bind(root, api.Endpoint.AbsoluteUri, sourceId);
        if (queue.All().Any(x => x.State == QueueState.Conflict)) throw new FileChangedException();
        CollectorStatus status;
        try { status = await api.Status(ct); }
        catch (TransportException error) when (error.Transient)
        {
            queue.Capture(root, queue.BatchLimit, ct);
            throw;
        }
        if (status.ProtocolVersion != 1 || status.SourceId != sourceId || status.ChunkBytes is < 1 or > 4194304 || status.MaxFileBytes <= 0)
            throw new InvalidDataException("Несовместимый протокол или изменился получатель.");
        var target = Math.Min(status.MaxFileBytes, 1024 * 1024);
        queue.RememberBatchLimit(target);
        IOException? scanError = null;
        bool hasRoom = true;
        try { hasRoom = queue.Capture(root, target, ct); }
        catch (IOException error) when (error is not FileChangedException) { scanError = error; }
        // Статус уже известной передачи не зависит от доступности исходного каталога.
        foreach (var known in queue.All().Where(x => x.UploadId != null && x.State is QueueState.Pending or QueueState.Uploading or QueueState.Retry or QueueState.Delivered or QueueState.Importing))
        {
            ct.ThrowIfCancellationRequested();
            if (known.NextAttemptTicks > DateTimeOffset.UtcNow.UtcTicks) continue;
            try { Save(Apply(known, await api.Get(known.UploadId!, ct))); }
            catch (TransportException error)
            {
                SaveTransportFailure(known, error);
                if (error.StatusCode is not (410 or 413)) throw;
            }
        }
        if (!hasRoom && scanError == null)
        {
            try { queue.Capture(root, target, ct); }
            catch (IOException error) when (error is not FileChangedException) { scanError = error; }
        }
        foreach (var known in queue.All().Where(x => x.RangeStart == null && x.Hash.Length != 0 && x.State is QueueState.Delivered or QueueState.Importing or QueueState.Imported or QueueState.ImportFailed or QueueState.Expired or QueueState.ImportStatusUnavailable))
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(root, known.File.RelativePath))) continue;
            try
            {
                using var locked = new LockedLog(root, known.File);
                if (locked.Hash() != known.Hash) throw new FileChangedException();
                locked.Verify();
            }
            catch (FileChangedException error) { Save(known with { State = QueueState.Conflict, Error = error.Message }); throw; }
        }
        foreach (var item in queue.All().Where(x => x.State is QueueState.Pending or QueueState.Uploading or QueueState.Retry))
        {
            ct.ThrowIfCancellationRequested();
            if (item.NextAttemptTicks > DateTimeOffset.UtcNow.UtcTicks || (scanError != null && item.RangeStart == null)) continue;
            try { await Transfer(item, status, ct); }
            catch (FileChangedException error)
            {
                Save(queue.All().Single(x => x.UploadPath == item.UploadPath) with { State = QueueState.Conflict, Error = error.Message });
                throw;
            }
            catch (TransportException error)
            {
                SaveTransportFailure(item, error);
                if (error.StatusCode is not (410 or 413)) throw;
            }
        }
        if (scanError != null) throw scanError;
    }
    private async Task Transfer(QueueItem item, CollectorStatus status, CancellationToken ct)
    {
        if (item is { RangeStart: null, UploadId: null, JobId: null, Offset: 0 } && item.File.Size is 0 or 3)
        {
            // Старая очередь хранит файл целиком. Позиция чтения уже сохранена при её обновлении.
            using var small = item.File.Size == 3 ? new LockedLog(root, item.File) : null;
            if (small == null || small.StartsWithUtf8Marker())
            {
                if (small != null && item.Hash.Length != 0 && item.Hash != small.Hash()) throw new FileChangedException();
                queue.Discard(item);
                return;
            }
        }
        if (item.File.Size > status.MaxFileBytes || item.File.Size > 2147483648L)
        {
            Save(item with { State = QueueState.Error, NextAttemptTicks = 0,
                Error = $"Размер файла {item.File.Size} байт превышает лимит {Math.Min(status.MaxFileBytes, 2147483648L)} байт. Остальная очередь продолжается. После устранения причины нажмите «Повторить выбранное»." });
            return;
        }
        using var locked = item.RangeStart == null ? new LockedLog(root, item.File) : null;
        var hash = locked?.Hash() ?? item.Hash;
        locked?.Verify();
        if (item.Hash.Length != 0 && item.Hash != hash) throw new FileChangedException();
        item = item with { Hash = hash };
        Save(item);
        if (item.UploadId == null)
        {
            item = Apply(item, await api.Register(item.UploadPath, item.File.Size, hash, ct));
            Save(item);
        }
        if (item.State != QueueState.Uploading) return;
        if (locked != null) locked.Stream.Position = item.Offset;
        while (item.Offset < item.File.Size)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(status.ChunkBytes, item.File.Size - item.Offset);
            byte[] bytes;
            if (locked == null) bytes = queue.ReadPayload(item.UploadPath, item.Offset, count);
            else
            {
                bytes = new byte[count];
                await locked.Stream.ReadExactlyAsync(bytes, ct);
            }
            var upload = await api.Chunk(item.UploadId!, item.Offset, bytes, ct);
            if (upload.ReceivedBytes != item.Offset + bytes.Length) throw new InvalidDataException("Сервер подтвердил неверное смещение.");
            item = Apply(item, upload);
            Save(item);
        }
        if (locked != null && locked.Hash() != hash) throw new FileChangedException();
        locked?.Verify();
        Save(Apply(item, await api.Complete(item.UploadId!, ct)));
    }
    private void SaveTransportFailure(QueueItem item, TransportException error)
    {
        var saved = queue.All().Single(x => x.UploadPath == item.UploadPath);
        var attempts = saved.Attempts + 1;
        var state = error.StatusCode switch
        {
            409 => QueueState.Conflict,
            410 when saved.JobId != null => QueueState.ImportStatusUnavailable,
            410 => QueueState.Expired,
            _ when error.Transient => saved.JobId != null ? saved.State : QueueState.Retry,
            _ => QueueState.Error
        };
        var nextAttempt = error.Transient
            ? DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Min(attempts - 1, 8))) + Random.Shared.NextDouble() * 2).UtcTicks : 0;
        Save(saved with { State = state, Attempts = attempts, NextAttemptTicks = nextAttempt,
            Error = state == QueueState.ImportStatusUnavailable ? "Файл принят. Статус импорта недоступен." : error.Message });
    }
    private void Save(QueueItem item) { queue.Save(item); Progress?.Invoke(item); }
    private static QueueItem Apply(QueueItem item, Upload upload)
    {
        if (!Guid.TryParse(upload.UploadId, out _) || upload.RelativePath != item.UploadPath || upload.SizeBytes != item.File.Size
            || upload.ReceivedBytes < 0 || upload.ReceivedBytes > item.File.Size || (item.UploadId != null && item.UploadId != upload.UploadId))
            throw new InvalidDataException("Некорректная идентичность передачи в ответе сервера.");
        var state = upload.Status switch
        {
            "uploading" => QueueState.Uploading,
            "queued" => QueueState.Delivered,
            "importing" => QueueState.Importing,
            "imported" => QueueState.Imported,
            "failed" => QueueState.ImportFailed,
            "expired" => QueueState.Expired,
            _ => throw new InvalidDataException("Неизвестное состояние передачи.")
        };
        if (item.JobId != null && (upload.JobId != item.JobId || state is QueueState.Uploading or QueueState.Expired))
            throw new InvalidDataException("Сервер изменил состояние принятого файла. Повторная отправка запрещена.");
        if (state is QueueState.Delivered or QueueState.Importing or QueueState.Imported or QueueState.ImportFailed)
            if (upload.ReceivedBytes != item.File.Size || upload.JobId == null) throw new InvalidDataException("Не подтверждено завершение передачи.");
        return item with { UploadId = upload.UploadId, Offset = upload.ReceivedBytes, State = state, JobId = upload.JobId, Error = state == QueueState.ImportFailed ? "Ошибка импорта. Подробности на сайте." : "", Attempts = upload.ReceivedBytes > item.Offset || state != QueueState.Uploading ? 0 : item.Attempts, NextAttemptTicks = 0 };
    }
}

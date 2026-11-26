using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using TjClient.Core;

sealed record IntegrationConfig
{
    public string Endpoint { get; init; } = "";
    public string Token { get; init; } = "";
    public string Folder { get; init; } = "";
    public string CaFile { get; init; } = "";
    public string StateDirectory { get; init; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<InitialMode>))]
    public InitialMode Mode { get; init; } = InitialMode.NewFiles;
    public DateTimeOffset? SinceUtc { get; init; }
    public bool FinishedFilesConsent { get; init; }
    public bool RecipientConsent { get; init; }
    public int ExpectedFiles { get; init; } = 1;
    public int MaxCycles { get; init; } = 60;
    public int PollMilliseconds { get; init; } = 1000;
    public bool RequireImported { get; init; }
}

static class IntegrationHarness
{
    public static int ReadUnc(string root)
    {
        if (!root.StartsWith(@"\\") && !root.StartsWith("//")) return 2;
        try
        {
            var files = LogScanner.Scan(root);
            foreach (var file in files) { using var locked = new LockedLog(root, file); Assert.Equal(64, locked.Hash().Length); locked.Verify(); }
            Console.WriteLine($"UNC_READ_VERIFIED files={files.Count} bytes={files.Sum(x => x.Size)}");
            return files.Count > 0 ? 0 : 2;
        }
        catch (Exception e) { Console.WriteLine("UNC_READ_FAILED " + e.GetType().Name); return 2; }
    }
    public static int Protect(string jsonPath)
    {
        try
        {
            var config = JsonSerializer.Deserialize<IntegrationConfig>(File.ReadAllText(jsonPath)) ?? throw new InvalidDataException();
            ProtectedStore.Save(jsonPath + ".dpapi", config);
            Console.WriteLine("CONFIG_PROTECTED");
            return 0;
        }
        catch { Console.WriteLine("CONFIG_PROTECTION_FAILED"); return 2; }
    }
    public static async Task<int> Run(string protectedPath)
    {
        try
        {
            var config = ProtectedStore.Load<IntegrationConfig>(protectedPath) ?? throw new InvalidOperationException("Не найден защищённый конфиг.");
            if (!config.FinishedFilesConsent || !config.RecipientConsent) throw new InvalidOperationException("Нет согласия на завершённые файлы и получателя.");
            if (config.MaxCycles is < 1 or > 3600 || config.PollMilliseconds is < 100 or > 60000 || config.ExpectedFiles is < 0 or > 10000) throw new InvalidOperationException("Неверные ограничения теста.");
            using var single = new ProfileLock(config.StateDirectory);
            using var ca = X509Certificate2.CreateFromPem(File.ReadAllText(config.CaFile));
            using var http = TestTls.Create(ca);
            using var api = new CollectorClient(config.Endpoint, config.Token, http);
            var status = await api.Status();
            var profile = new ClientProfile { Folder = config.Folder, Endpoint = config.Endpoint, Token = config.Token, SourceId = status.SourceId, Mode = config.Mode, Since = config.SinceUtc }.Approve();
            profile.RequireConsent();
            using var queue = new QueueStore(Path.Combine(config.StateDirectory, profile.QueueKey + ".db"));
            var scan = LogScanner.Scan(profile.Folder);
            var selected = PreviewPlan.Select(profile, scan);
            Console.WriteLine($"PREVIEW files={selected.Count} bytes={selected.Sum(x => x.Size)} baseline={scan.Count - selected.Count}");
            queue.BeginObservation(profile.Folder, scan, profile.Mode, profile.Since);
            Console.WriteLine("BASELINE_READY");
            var engine = new ClientEngine(profile.Folder, profile.SourceId, queue, api);
            for (var cycle = 0; cycle < config.MaxCycles; cycle++)
            {
                try { await engine.Run(); }
                catch (TransportException e) when (e.Transient) { Console.WriteLine("TRANSIENT_RETRY_PENDING"); }
                var items = queue.All().Where(x => x.State != QueueState.Baseline).ToList();
                if (items.Any(x => x.State is QueueState.ImportFailed or QueueState.Conflict or QueueState.Error or QueueState.Expired or QueueState.ImportStatusUnavailable))
                { Summary(items); return 2; }
                if (items.Count == config.ExpectedFiles && items.All(x => config.RequireImported ? x.State == QueueState.Imported : x.State is QueueState.Delivered or QueueState.Importing or QueueState.Imported))
                { Summary(items); return 0; }
                await Task.Delay(config.PollMilliseconds);
            }
            Summary(queue.All().Where(x => x.State != QueueState.Baseline).ToList());
            Console.WriteLine("TIMEOUT_WAITING_FOR_EXPECTED_STATE");
            return 3;
        }
        catch (TransportException e) { Console.WriteLine($"TRANSPORT_REJECTED http={e.StatusCode} transient={e.Transient}"); return 2; }
        catch (Exception e) { Console.WriteLine("INTEGRATION_FAILED " + e.GetType().Name); return 2; }
    }
    private static void Summary(IReadOnlyList<QueueItem> items) => Console.WriteLine(JsonSerializer.Serialize(new
    {
        total = items.Count,
        files = items.Select(x => new { upload_id = x.UploadId, received_bytes = x.Offset, size_bytes = x.File.Size, state = x.State.ToString(), job_id = x.JobId, sha256 = x.Hash })
    }));
}

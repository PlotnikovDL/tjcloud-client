namespace TjClient.Core;

public sealed record ClientProfile
{
    public string Folder { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string Token { get; init; } = "";
    public long SourceId { get; init; }
    public string SourceName { get; init; } = "";
    public int IntervalSeconds { get; init; } = 60;
    public InitialMode Mode { get; init; } = InitialMode.NewFiles;
    public DateTimeOffset? Since { get; init; }
    public bool Startup { get; init; }
    public bool Running { get; init; }
    public string ConsentBinding { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string QueueKey => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(Folder).TrimEnd('\\').ToUpperInvariant() + "\n" + EndpointPolicy.Parse(Endpoint).AbsoluteUri + "\n" + SourceId)));
    private string Binding => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(QueueKey + "\n" + Token + "\n" + Mode + "\n" + Since?.UtcTicks)));
    public ClientProfile ChangeEndpoint(string endpoint) => endpoint == Endpoint ? this : this with { Endpoint = endpoint, Token = "", SourceId = 0, SourceName = "", ConsentBinding = "", Running = false };
    public ClientProfile ChangeFolder(string folder) => folder == Folder ? this : this with { Folder = folder, ConsentBinding = "", Mode = InitialMode.NewFiles, Since = null, Running = false };
    public ClientProfile Approve()
    {
        if (SourceId <= 0 || string.IsNullOrWhiteSpace(Token) || !Path.IsPathFullyQualified(Folder) || IntervalSeconds is < 5 or > 86400
            || (Mode == InitialMode.ModifiedSince && Since == null)) throw new InvalidOperationException("Проверьте папку, источник, интервал и дату.");
        return this with { ConsentBinding = Binding };
    }
    public void RequireConsent()
    {
        if (ConsentBinding.Length == 0 || ConsentBinding != Binding) throw new InvalidOperationException("Подтвердите завершённость файлов, выбранную папку и получателя после проверки.");
    }
}

public static class ProtectedStore
{
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            var encrypted = System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var temporary = path + ".new";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(encrypted); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
    public static T? Load<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var bytes = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(path), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(bytes); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}

public sealed class ProfileLock : IDisposable
{
    private readonly FileStream handle;
    public ProfileLock(string folder)
    {
        Directory.CreateDirectory(folder);
        handle = new FileStream(Path.Combine(folder, "profile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => handle.Dispose();
}

using TjClient.Core;

static class ProfileTests
{
    public static void Reconfigure()
    {
        using var fixture = new Fixture();
        fixture.File("rphost_1/26112501.log", "synthetic");
        var profile = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost", Token = "synthetic", SourceId = 1 }.Approve();
        Assert.Equal("", profile.ChangeEndpoint("https://other.example").Token);
        Assert.Equal("", profile.ChangeEndpoint("https://other.example").ConsentBinding);
        var moved = profile.ChangeFolder(fixture.Root + "other");
        Assert.Equal("", moved.ConsentBinding);
        Assert.True(moved.QueueKey != profile.QueueKey);
        Assert.Equal(InitialMode.NewFiles, moved.Mode);
        var snapshot = LogScanner.Scan(fixture.Root);
        Assert.Equal(0, PreviewPlan.Select(profile, snapshot).Count);
        Assert.Equal(1, PreviewPlan.Select(profile with { Mode = InitialMode.ModifiedSince, Since = DateTimeOffset.MinValue }, snapshot).Count);
        Assert.Equal(0, PreviewPlan.Select(profile with { Mode = InitialMode.ModifiedSince, Since = DateTimeOffset.UtcNow.AddDays(1) }, snapshot).Count);
    }
    public static void Protection()
    {
        using var fixture = new Fixture();
        var profile = new ClientProfile { Folder = fixture.Root, Endpoint = "https://localhost", Token = "synthetic-sensitive-token", SourceId = 1 };
        Assert.Equal(InitialMode.NewFiles, profile.Mode);
        Assert.True(!profile.Startup);
        Assert.Throws<InvalidOperationException>(() => profile.RequireConsent());
        profile = profile.Approve();
        profile.RequireConsent();
        Assert.Throws<InvalidOperationException>(() => (profile with { Folder = fixture.Root + "other" }).RequireConsent());
        Assert.Throws<InvalidOperationException>(() => (profile with { Endpoint = "https://other.example" }).RequireConsent());
        Assert.Throws<InvalidOperationException>(() => (profile with { Token = "another" }).RequireConsent());
        var path = Path.Combine(fixture.Root, "settings.dpapi");
        ProtectedStore.Save(path, profile);
        Assert.True(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(profile.Token));
        var restored = ProtectedStore.Load<ClientProfile>(path)!;
        Assert.Equal(profile.Token, restored.Token);
        restored.RequireConsent();
        using var single = new ProfileLock(fixture.Root);
        Assert.Throws<IOException>(() => { using var second = new ProfileLock(fixture.Root); });
    }
}

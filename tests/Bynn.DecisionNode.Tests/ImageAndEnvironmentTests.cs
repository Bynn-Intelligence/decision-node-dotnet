using static Bynn.DecisionNode.Tests.Http;

namespace Bynn.DecisionNode.Tests;

public class ImageTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGM8ISfHwMDAxMDAwMDAAAANBAEIfXHKZgAAAABJRU5ErkJggg==");

    [Fact]
    public void SniffsJpegPngAndWebp()
    {
        Assert.Equal("image/jpeg", Image.SniffMediaType(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal("image/png", Image.SniffMediaType(Png));
        Assert.Equal("image/webp", Image.SniffMediaType("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(Image.SniffMediaType("GIF89a"u8));
        Assert.Null(Image.SniffMediaType(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void FromFileReadsBytesAndMediaType()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Png);
            var image = Image.FromFile("receipt", path);
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal(Convert.ToBase64String(Png), (string?)image.ToJson()["data"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BytesWithoutAMediaTypeAreDetected()
    {
        Assert.Equal("image/png", Image.FromBytes("a", Png).MediaType);
        Assert.Equal("image/jpeg", Image.FromBytes("b", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "").MediaType);
        Assert.Equal("image/webp", Image.FromBytes("c", "RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), null).MediaType);
        // A media type the caller gives is sent as given.
        Assert.Equal("image/png", Image.FromBytes("d", new byte[] { 1, 2, 3 }, "image/png").MediaType);
    }

    [Fact]
    public void BytesOfAnUnknownFormatWithoutAMediaTypeAreAnArgumentError()
    {
        Assert.Throws<ArgumentException>(() => Image.FromBytes("x", "GIF89a"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => Image.FromBytes("x", Array.Empty<byte>()));
    }

    [Fact]
    public void UnreadableOrUnknownFileFailsBeforeSending()
    {
        Assert.Throws<ArgumentException>(() => Image.FromFile("x", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not an image");
            Assert.Throws<ArgumentException>(() => Image.FromFile("x", path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Tests that change process environment variables; they run one at a time.</summary>
[Collection("environment")]
public class EnvironmentTests : IDisposable
{
    private static readonly string[] Vars = { "DECISIONNODE_API_KEY", "DECISIONNODE_BASE_URL", "DECISIONNODE_MODEL" };
    private readonly Dictionary<string, string?> _saved = Vars.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public void Dispose()
    {
        foreach (var (k, v) in _saved)
        {
            Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public async Task EnvironmentFillsWhatIsNotPassed()
    {
        Environment.SetEnvironmentVariable("DECISIONNODE_API_KEY", "dn_from_env");
        Environment.SetEnvironmentVariable("DECISIONNODE_BASE_URL", "https://self-hosted.example.test/");
        Environment.SetEnvironmentVariable("DECISIONNODE_MODEL", "decisionnode-1.0-flash");
        var stub = new StubHandler((_, _) => Ok());
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions { HttpClient = new HttpClient(stub), DelegatingHandlersNeverResendRequests = true });
        await dn.DecideAsync("s", OneTruth());
        var r = Assert.Single(stub.Requests);
        Assert.Equal("https://self-hosted.example.test/v1/decide", r.Uri.ToString());
        Assert.Equal("Bearer dn_from_env", r.Headers["Authorization"]);
        Assert.Equal("decisionnode-1.0-flash", (string?)r.Json!["model"]);
    }

    [Fact]
    public async Task ArgumentsBeatTheEnvironment()
    {
        Environment.SetEnvironmentVariable("DECISIONNODE_API_KEY", "dn_from_env");
        Environment.SetEnvironmentVariable("DECISIONNODE_MODEL", "decisionnode-1.0-flash");
        var stub = new StubHandler((_, _) => Ok());
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions { ApiKey = "dn_explicit", Model = "decisionnode-1.0", BaseUrl = "https://a.example.test", HttpClient = new HttpClient(stub), DelegatingHandlersNeverResendRequests = true });
        await dn.DecideAsync("s", OneTruth());
        var r = Assert.Single(stub.Requests);
        Assert.Equal("Bearer dn_explicit", r.Headers["Authorization"]);
        Assert.Equal("decisionnode-1.0", (string?)r.Json!["model"]);
    }

    [Fact]
    public async Task EmptyArgumentsCountAsNotGiven()
    {
        Environment.SetEnvironmentVariable("DECISIONNODE_API_KEY", "dn_from_env");
        Environment.SetEnvironmentVariable("DECISIONNODE_BASE_URL", "https://self-hosted.example.test");
        Environment.SetEnvironmentVariable("DECISIONNODE_MODEL", "decisionnode-1.0-flash");
        var stub = new StubHandler((_, _) => Ok());
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions { ApiKey = "", BaseUrl = "", Model = "", HttpClient = new HttpClient(stub), DelegatingHandlersNeverResendRequests = true });
        await dn.DecideAsync("s", OneTruth());
        var r = Assert.Single(stub.Requests);
        Assert.Equal("https://self-hosted.example.test/v1/decide", r.Uri.ToString());
        Assert.Equal("Bearer dn_from_env", r.Headers["Authorization"]);
        Assert.Equal("decisionnode-1.0-flash", (string?)r.Json!["model"]);
    }

    [Fact]
    public async Task EmptyKeyWithoutEnvironmentSendsNothing()
    {
        Environment.SetEnvironmentVariable("DECISIONNODE_API_KEY", null);
        var stub = new StubHandler((_, _) => Ok());
        using var dn = new DecisionNodeClient(new DecisionNodeClientOptions { ApiKey = "", HttpClient = new HttpClient(stub), DelegatingHandlersNeverResendRequests = true });
        await Assert.ThrowsAsync<AuthenticationError>(() => dn.DecideAsync("s", OneTruth()));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void DefaultsApplyWithoutEnvironment()
    {
        foreach (var v in Vars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }
        using var dn = new DecisionNodeClient();
        Assert.Equal("https://api.decisionnode.com", dn.BaseUrl);
        Assert.Equal("decisionnode-latest", dn.Model);
        Assert.Equal(TimeSpan.FromSeconds(10), dn.Timeout);
        Assert.Equal(2, dn.MaxRetries);
    }
}

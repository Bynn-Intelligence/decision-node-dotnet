using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Bynn.DecisionNode.Conformance;

/// <summary>Starts the suite's mock server once for the whole test run.</summary>
public sealed class MockServer : IDisposable
{
    private readonly Process _process;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public MockServer()
    {
        SuiteDir = Suite.Directory;
        var python = Environment.GetEnvironmentVariable("PYTHON") ?? "python3";
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(SuiteDir, "mock", "server.py"));
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add("0");
        _process = Process.Start(start) ?? throw new InvalidOperationException("the mock server did not start");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        var line = _process.StandardOutput.ReadLine() ?? string.Empty;
        const string prefix = "listening on ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            _process.Kill();
            throw new InvalidOperationException($"the mock server did not start: '{line}'");
        }
        Url = line[prefix.Length..].Trim().TrimEnd('/');
    }

    public string SuiteDir { get; }

    public string Url { get; }

    /// <summary>The recorded requests of a session (<c>*</c> for every session), optionally only those at <paramref name="path"/>.</summary>
    public async Task<JsonObject> RequestsAsync(string session, string? path = null)
    {
        var query = "session=" + Uri.EscapeDataString(session);
        if (path is not null)
        {
            query += "&path=" + Uri.EscapeDataString(path);
        }
        var text = await _http.GetStringAsync($"{Url}/_mock/requests?{query}");
        return (JsonObject)JsonNode.Parse(text)!;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        finally
        {
            _process.Dispose();
            _http.Dispose();
        }
    }
}

[CollectionDefinition("mock")]
public sealed class MockCollection : ICollectionFixture<MockServer>
{
}

/// <summary>Where the conformance suite is checked out.</summary>
public static class Suite
{
    private static readonly Lazy<string> Dir = new(Find);

    /// <summary>The suite's directory; throws when <c>DECISIONNODE_CONFORMANCE_DIR</c> is unset or wrong.</summary>
    public static string Directory => Dir.Value;

    /// <summary>The id given to the one placeholder case when the suite cannot be found, so that every theory fails instead of finding no data.</summary>
    public const string Missing = "suite-not-found";

    private static string Find()
    {
        // Unset means fail, never skip: the suite is part of every test run.
        var fromEnv = Environment.GetEnvironmentVariable("DECISIONNODE_CONFORMANCE_DIR");
        if (string.IsNullOrEmpty(fromEnv))
        {
            throw new InvalidOperationException("DECISIONNODE_CONFORMANCE_DIR is not set: set it to a checkout of the conformance suite");
        }
        return Check(Path.GetFullPath(fromEnv));
    }

    private static string Check(string dir) =>
        File.Exists(Path.Combine(dir, "mock", "server.py"))
            ? dir
            : throw new InvalidOperationException($"no conformance suite at {dir}: set DECISIONNODE_CONFORMANCE_DIR to a checkout of it");

    public static IEnumerable<object[]> Cases(string kind)
    {
        string folder;
        try
        {
            folder = Path.Combine(Directory, "cases", kind);
        }
        catch (InvalidOperationException)
        {
            // Load throws the reason when this placeholder runs: the test fails, never skips.
            return new[] { new object[] { Missing } };
        }
        return System.IO.Directory.GetFiles(folder, "*.json")
            .Order(StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });
    }

    public static JsonObject Load(string kind, string id)
    {
        _ = Directory; // throws when the suite is not configured
        var text = File.ReadAllText(Path.Combine(Directory, "cases", kind, id + ".json"));
        var node = (JsonObject)JsonNode.Parse(text)!;
        if ((string?)node["id"] != id)
        {
            throw new InvalidOperationException($"case {kind}/{id}: the id does not match the file name");
        }
        return node;
    }
}

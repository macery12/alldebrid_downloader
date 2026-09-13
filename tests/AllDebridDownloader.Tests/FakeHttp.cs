using System.Net;
using System.Net.Http;
using System.Text;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Tests;

/// <summary>
/// Scripted HttpMessageHandler so the whole suite runs with no network access and no
/// real API key.
/// </summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _callCount;

    public FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
        => _responder = responder;

    /// <summary>Always answers with the same JSON body.</summary>
    public static FakeHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new((_, _) => JsonResponse(body, status));

    /// <summary>Answers each call from the queue in order; the last entry repeats.</summary>
    public static FakeHandler Sequence(params Func<HttpResponseMessage>[] responses)
        => new((_, n) => responses[Math.Min(n - 1, responses.Length - 1)]());

    public int CallCount => _callCount;

    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Form bodies as captured strings, for asserting parameter shapes.</summary>
    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _callCount);
        Requests.Add(request);

        Bodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));

        return _responder(request, n);
    }

    public static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
}

/// <summary>A logger writing to a throwaway temp folder, disposed by each test.</summary>
public sealed class TestLogger : IDisposable
{
    public TestLogger()
    {
        Directory = Path.Combine(Path.GetTempPath(), "adw-tests", Guid.NewGuid().ToString("N"));
        Logger = new Logger(Directory, LogLevel.Debug);
    }

    public string Directory { get; }
    public Logger Logger { get; }

    public void Dispose()
    {
        Logger.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* temp */ }
    }
}

/// <summary>A self-deleting temp directory for filesystem tests.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "adw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* temp */ }
    }
}

using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace PeopleSync.TestDouble;

public static class Program
{
    public static async Task Main(string[] args)
    {
        await using var host = await FakeSystemB.StartAsync(cancellationToken: default).ConfigureAwait(false);
        await host.App.WaitForShutdownAsync().ConfigureAwait(false);
    }
}

public enum FakeSystemBFault
{
    None,
    ServiceUnavailable,
    Hang
}

public sealed class FakeSystemBOptions
{
    public int MaxRequestsPerSecond { get; init; } = 10;
    public int MaxBatchSize { get; init; } = 500;
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan HangDuration { get; init; } = TimeSpan.FromSeconds(31);
    public Func<int, FakeSystemBFault>? FaultSelector { get; init; }
}

public sealed record ReceivedPerson(string FirstName, string LastName, string? IdempotencyKey);

public sealed class FakeSystemBState
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _requestTimes = new();
    private readonly Dictionary<string, DateTimeOffset> _idempotencyKeys = new(StringComparer.Ordinal);
    private readonly List<ReceivedPerson> _receivedPeople = [];

    public IReadOnlyList<ReceivedPerson> ReceivedPeople
    {
        get
        {
            lock (_gate)
            {
                return _receivedPeople.ToArray();
            }
        }
    }

    public int RequestCount { get; private set; }

    public bool TryBeginRequest(FakeSystemBOptions options, DateTimeOffset now, out int requestNumber)
    {
        lock (_gate)
        {
            RequestCount++;
            requestNumber = RequestCount;

            while (_requestTimes.TryPeek(out var timestamp) && now - timestamp >= TimeSpan.FromSeconds(1))
            {
                _requestTimes.Dequeue();
            }

            if (_requestTimes.Count >= options.MaxRequestsPerSecond)
            {
                return false;
            }

            _requestTimes.Enqueue(now);
            return true;
        }
    }

    public bool IsDuplicate(string? idempotencyKey, FakeSystemBOptions options, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return false;
        }

        lock (_gate)
        {
            if (_idempotencyKeys.TryGetValue(idempotencyKey, out var timestamp) && now - timestamp <= options.DeduplicationWindow)
            {
                return true;
            }

            _idempotencyKeys[idempotencyKey] = now;
            return false;
        }
    }

    public void RecordPeople(IEnumerable<ReceivedPerson> people)
    {
        lock (_gate)
        {
            _receivedPeople.AddRange(people);
        }
    }
}

public sealed class FakeSystemBHost : IAsyncDisposable
{
    public FakeSystemBHost(WebApplication app, Uri baseUri, FakeSystemBState state)
    {
        App = app;
        BaseUri = baseUri;
        State = state;
    }

    public WebApplication App { get; }

    public Uri BaseUri { get; }

    public FakeSystemBState State { get; }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync().ConfigureAwait(false);
        await App.DisposeAsync().ConfigureAwait(false);
    }
}

public static class FakeSystemB
{
    public static async Task<FakeSystemBHost> StartAsync(FakeSystemBOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new FakeSystemBOptions();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var state = new FakeSystemBState();

        app.MapPost("/people/batch", async context =>
        {
            var now = DateTimeOffset.UtcNow;
            if (!state.TryBeginRequest(options, now, out var requestNumber))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.Append("Retry-After", "1");
                return;
            }

            switch (options.FaultSelector?.Invoke(requestNumber) ?? FakeSystemBFault.None)
            {
                case FakeSystemBFault.ServiceUnavailable:
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                case FakeSystemBFault.Hang:
                    await Task.Delay(options.HangDuration, context.RequestAborted).ConfigureAwait(false);
                    break;
            }

            var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
            if (state.IsDuplicate(idempotencyKey, options, now))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync("<accepted duplicate=\"true\" />").ConfigureAwait(false);
                return;
            }

            var document = await XDocument.LoadAsync(context.Request.Body, LoadOptions.None, context.RequestAborted).ConfigureAwait(false);
            var persons = document.Root?.Elements("person")
                .Select(personElement => new ReceivedPerson(
                    personElement.Element("firstname")?.Value ?? string.Empty,
                    personElement.Element("lastname")?.Value ?? string.Empty,
                    idempotencyKey))
                .ToArray() ?? [];

            if (persons.Length > options.MaxBatchSize)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Batch too large.").ConfigureAwait(false);
                return;
            }

            state.RecordPeople(persons);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync("<accepted />").ConfigureAwait(false);
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var baseUri = new Uri(app.Urls.Single(), UriKind.Absolute);
        return new FakeSystemBHost(app, baseUri, state);
    }
}

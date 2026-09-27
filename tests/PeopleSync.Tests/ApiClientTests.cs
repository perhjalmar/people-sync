using System.Net;
using System.Net.Http;
using System.Text;
using PeopleSync;

namespace PeopleSync.Tests;

public sealed class ApiClientTests
{
    [Fact]
    public async Task RateLimiterEnforcesAtMostTenRequestsPerSecond()
    {
        var timestamps = new List<DateTimeOffset>();
        var gate = new object();
        using var httpClient = new HttpClient(new DelegateHandler(_ =>
        {
            lock (gate)
            {
                timestamps.Add(DateTimeOffset.UtcNow);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        await using var apiClient = new PeopleApiClient(new Uri("http://localhost/people/batch"), httpClient: httpClient);
        var payload = Encoding.UTF8.GetBytes("<people />");

        await Task.WhenAll(Enumerable.Range(0, 11).Select(index => apiClient.SendBatchAsync(payload, $"key-{index}")));

        var ordered = timestamps.OrderBy(timestamp => timestamp).ToArray();
        Assert.Equal(11, ordered.Length);
        Assert.True(ordered[10] - ordered[0] >= TimeSpan.FromMilliseconds(900), "Expected the 11th request to be delayed by the rate limiter.");
    }

    [Fact]
    public async Task RetriesOnTooManyRequestsAndServiceUnavailable()
    {
        var delays = new List<TimeSpan>();
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        using var httpClient = new HttpClient(new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(responses.Dequeue()))));
        await using var apiClient = new PeopleApiClient(
            new Uri("http://localhost/people/batch"),
            httpClient: httpClient,
            delayAsync: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        using var response = await apiClient.SendBatchAsync(Encoding.UTF8.GetBytes("<people />"), "retry-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, delays.Count);
        Assert.True(delays[0] >= TimeSpan.FromMilliseconds(250));
        Assert.True(delays[1] >= TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task SendsIdempotencyKeyHeaderWithEachRequest()
    {
        string? observedHeader = null;
        using var httpClient = new HttpClient(new DelegateHandler(request =>
        {
            observedHeader = request.Headers.GetValues("Idempotency-Key").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        await using var apiClient = new PeopleApiClient(new Uri("http://localhost/people/batch"), httpClient: httpClient);
        using var response = await apiClient.SendBatchAsync(Encoding.UTF8.GetBytes("<people />"), "known-key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("known-key", observedHeader);
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}

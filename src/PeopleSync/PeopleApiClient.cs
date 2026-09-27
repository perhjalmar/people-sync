using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

namespace PeopleSync;

public class PeopleApiClient : IAsyncDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly RateLimiter _rateLimiter;
    private readonly bool _ownsRateLimiter;
    private readonly Uri _requestUri;

    public PeopleApiClient(
        Uri requestUri,
        HttpClient? httpClient = null,
        RateLimiter? rateLimiter = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        int maxRetries = 5,
        TimeSpan? requestTimeout = null)
    {
        _requestUri = requestUri;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _rateLimiter = rateLimiter ?? new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = int.MaxValue,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(110),
            TokensPerPeriod = 1,
            AutoReplenishment = true
        });
        _ownsRateLimiter = rateLimiter is null;
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        MaxRetries = maxRetries;
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
    }

    public int MaxRetries { get; }

    public TimeSpan RequestTimeout { get; }

    public virtual async Task<HttpResponseMessage> SendBatchAsync(byte[] xmlPayload, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xmlPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        for (var attempt = 0; ; attempt++)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            if (!lease.IsAcquired)
            {
                throw new InvalidOperationException("Unable to acquire API rate-limiter lease.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _requestUri)
            {
                Content = new ByteArrayContent(xmlPayload)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(RequestTimeout);

            try
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                if (IsRetriable(response.StatusCode) && attempt < MaxRetries)
                {
                    var delay = GetRetryDelay(response, attempt);
                    response.Dispose();
                    await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var statusCode = response.StatusCode;
                var reasonPhrase = response.ReasonPhrase;
                response.Dispose();
                throw new HttpRequestException($"System B returned {(int)statusCode} {reasonPhrase}.", null, statusCode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetries)
            {
                await _delayAsync(GetExponentialBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        if (_ownsRateLimiter)
        {
            return _rateLimiter.DisposeAsync();
        }

        return ValueTask.CompletedTask;
    }

    public static string CreateDeterministicIdempotencyKey(string fingerprint, int batchIndex)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{fingerprint}|{batchIndex}"));
        return Convert.ToHexString(bytes);
    }

    private static bool IsRetriable(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } retryAfterDate)
        {
            var delay = retryAfterDate - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        return GetExponentialBackoff(attempt);
    }

    private static TimeSpan GetExponentialBackoff(int attempt)
    {
        var milliseconds = Math.Min(250 * Math.Pow(2, attempt), 5_000);
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}

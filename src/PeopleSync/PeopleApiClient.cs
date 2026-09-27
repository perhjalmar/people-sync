using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.RateLimiting;

namespace PeopleSync;

public class PeopleApiClient : IAsyncDisposable
{
    private readonly Queue<DateTimeOffset> _dispatchTimes = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
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
        _rateLimiter = rateLimiter ?? new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = int.MaxValue
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

            await WaitForRateLimitSlotAsync(cancellationToken).ConfigureAwait(false);

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

    private async Task WaitForRateLimitSlotAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan? delay = null;

            await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                while (_dispatchTimes.Count > 0 && now - _dispatchTimes.Peek() >= TimeSpan.FromSeconds(1))
                {
                    _dispatchTimes.Dequeue();
                }

                if (_dispatchTimes.Count < 10)
                {
                    _dispatchTimes.Enqueue(now);
                    return;
                }

                delay = _dispatchTimes.Peek().AddSeconds(1) - now + TimeSpan.FromMilliseconds(1);
            }
            finally
            {
                _rateGate.Release();
            }

            if (delay.GetValueOrDefault() > TimeSpan.Zero)
            {
                await _delayAsync(delay.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

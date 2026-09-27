using System.Text.Json;

namespace PeopleSync;

public sealed class BadRecordLogger
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public BadRecordLogger(string path)
    {
        _path = path;
    }

    public async Task LogMalformedAsync(int lineNumber, string rawLine, string message, CancellationToken cancellationToken = default)
    {
        await LogAsync("error", lineNumber, rawLine, message, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogWarningAsync(int lineNumber, string rawLine, string message, CancellationToken cancellationToken = default)
    {
        await LogAsync("warning", lineNumber, rawLine, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task LogAsync(string level, int lineNumber, string rawLine, string message, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            level,
            lineNumber,
            message,
            rawLine
        };

        var json = JsonSerializer.Serialize(entry, SerializerOptions);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

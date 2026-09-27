namespace PeopleSync;

public sealed class CheckpointStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public CheckpointStore(string path)
    {
        _path = path;
    }

    public async Task<HashSet<int>> ReadConfirmedBatchIndexesAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        var indexes = new HashSet<int>();
        if (!File.Exists(_path))
        {
            return indexes;
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('|');
            if (parts.Length != 4 || !string.Equals(parts[0], fingerprint, StringComparison.Ordinal) || !int.TryParse(parts[1], out var batchIndex))
            {
                continue;
            }

            indexes.Add(batchIndex);
        }

        return indexes;
    }

    public async Task AppendConfirmedBatchAsync(string fingerprint, int batchIndex, string idempotencyKey, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = string.Join('|', fingerprint, batchIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), idempotencyKey, timestamp.ToString("O"));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

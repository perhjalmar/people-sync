using System.Security.Cryptography;

namespace PeopleSync;

public sealed class SyncRunner
{
    private readonly CheckpointStore _checkpointStore;
    private readonly PersonFileParser _parser;
    private readonly PeopleApiClient _peopleApiClient;
    private readonly XmlBatchWriter _xmlBatchWriter;
    private readonly SentPayloadWriter? _sentPayloadWriter;

    public SyncRunner(PersonFileParser parser, XmlBatchWriter xmlBatchWriter, PeopleApiClient peopleApiClient, CheckpointStore checkpointStore, SentPayloadWriter? sentPayloadWriter = null)
    {
        _sentPayloadWriter = sentPayloadWriter;
        _parser = parser;
        _xmlBatchWriter = xmlBatchWriter;
        _peopleApiClient = peopleApiClient;
        _checkpointStore = checkpointStore;
    }

    public async Task<SyncRunSummary> RunAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var fingerprint = await ComputeFingerprintAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var confirmedBatchIndexes = await _checkpointStore.ReadConfirmedBatchIndexesAsync(fingerprint, cancellationToken).ConfigureAwait(false);
        var batch = new List<Person>(capacity: 500);
        var batchIndex = 0;
        var parsedPeople = 0;
        var sentBatches = 0;
        var skippedBatches = 0;

        async Task FlushBatchAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            if (confirmedBatchIndexes.Contains(batchIndex))
            {
                skippedBatches++;
                batch.Clear();
                batchIndex++;
                return;
            }

            await using var stream = new MemoryStream();
            await _xmlBatchWriter.WriteBatchAsync(batch, stream, cancellationToken).ConfigureAwait(false);
            var payload = stream.ToArray();
            var idempotencyKey = PeopleApiClient.CreateDeterministicIdempotencyKey(fingerprint, batchIndex);
            using var response = await _peopleApiClient.SendBatchAsync(payload, idempotencyKey, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (_sentPayloadWriter is not null)
            {
                await _sentPayloadWriter.AppendBatchAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await _checkpointStore.AppendConfirmedBatchAsync(fingerprint, batchIndex, idempotencyKey, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            confirmedBatchIndexes.Add(batchIndex);
            sentBatches++;
            batch.Clear();
            batchIndex++;
        }

        await foreach (var person in _parser.ParseAsync(inputPath, cancellationToken).ConfigureAwait(false))
        {
            batch.Add(person);
            parsedPeople++;
            if (batch.Count == 500)
            {
                await FlushBatchAsync().ConfigureAwait(false);
            }
        }

        await FlushBatchAsync().ConfigureAwait(false);

        return new SyncRunSummary(fingerprint, parsedPeople, sentBatches, skippedBatches);
    }

    private static async Task<string> ComputeFingerprintAsync(string inputPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(inputPath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

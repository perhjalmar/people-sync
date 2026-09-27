using System.Diagnostics;
using System.Text;
using PeopleSync;
using PeopleSync.TestDouble;

namespace PeopleSync.Tests;

public sealed class EndToEndTests
{
    [Fact]
    public async Task EndToEndRunSendsBatchAndWritesCheckpoint()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        await using var fakeSystemB = await FakeSystemB.StartAsync(new FakeSystemBOptions());

        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "people.txt");
            var checkpointPath = Path.Combine(tempDirectory.FullName, "checkpoint.log");
            var errorsPath = Path.Combine(tempDirectory.FullName, "errors.jsonl");
            await File.WriteAllTextAsync(inputPath, ExampleInput, Encoding.UTF8);

            var summary = await RunSyncAsync(inputPath, checkpointPath, errorsPath, new Uri(fakeSystemB.BaseUri, "/people/batch"));

            Assert.Equal(2, summary.ParsedPeople);
            Assert.Equal(1, summary.SentBatches);
            Assert.Equal(2, fakeSystemB.State.ReceivedPeople.Count);
            var checkpointLines = await File.ReadAllLinesAsync(checkpointPath);
            Assert.Single(checkpointLines);
            Assert.Contains(summary.Fingerprint, checkpointLines[0]);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResumeAfterCrashDoesNotCreateDuplicatesOrLosePeople()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        await using var fakeSystemB = await FakeSystemB.StartAsync(new FakeSystemBOptions());

        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "people.txt");
            var checkpointPath = Path.Combine(tempDirectory.FullName, "checkpoint.log");
            var errorsPath = Path.Combine(tempDirectory.FullName, "errors.jsonl");
            await File.WriteAllTextAsync(inputPath, BuildPeopleFile(501), Encoding.UTF8);

            var logger = new BadRecordLogger(errorsPath);
            var parser = new PersonFileParser(logger);
            var checkpointStore = new CheckpointStore(checkpointPath);
            var writer = new XmlBatchWriter();
            var crashClient = new CrashOnceApiClient(new Uri(fakeSystemB.BaseUri, "/people/batch"));
            var runner = new SyncRunner(parser, writer, crashClient, checkpointStore);

            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(inputPath));

            var firstRunPeopleCount = fakeSystemB.State.ReceivedPeople.Count;
            Assert.Equal(501, firstRunPeopleCount);

            await using var apiClient = new PeopleApiClient(new Uri(fakeSystemB.BaseUri, "/people/batch"));
            var resumeRunner = new SyncRunner(parser, writer, apiClient, checkpointStore);
            var summary = await resumeRunner.RunAsync(inputPath);

            Assert.Equal(501, summary.ParsedPeople);
            Assert.Equal(1, summary.SentBatches);
            Assert.Equal(1, summary.SkippedBatches);
            Assert.Equal(501, fakeSystemB.State.ReceivedPeople.Count);
            Assert.Equal(501, fakeSystemB.State.ReceivedPeople.Select(person => $"{person.FirstName}-{person.LastName}").Distinct().Count());
            Assert.Equal(2, (await File.ReadAllLinesAsync(checkpointPath)).Length);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SyntheticMillionPersonFileStaysWithinMemoryBudget()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "million.txt");
            await using (var stream = File.Create(inputPath))
            await using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                for (var index = 0; index < 1_000_000; index++)
                {
                    await writer.WriteLineAsync($"P|First{index}|Last{index}");
                }
            }

            var parser = new PersonFileParser(new BadRecordLogger(Path.Combine(tempDirectory.FullName, "errors.jsonl")));
            var process = Process.GetCurrentProcess();
            var baseline = process.WorkingSet64;
            long peak = baseline;
            var count = 0;

            await foreach (var _ in parser.ParseAsync(inputPath))
            {
                count++;
                if (count % 50_000 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    peak = Math.Max(peak, process.WorkingSet64);
                }
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            peak = Math.Max(peak, process.WorkingSet64);

            Assert.Equal(1_000_000, count);
            Assert.True(peak - baseline < 256L * 1024 * 1024, $"Peak working set delta was {peak - baseline} bytes.");
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static async Task<SyncRunSummary> RunSyncAsync(string inputPath, string checkpointPath, string errorsPath, Uri endpoint)
    {
        var logger = new BadRecordLogger(errorsPath);
        var parser = new PersonFileParser(logger);
        var checkpointStore = new CheckpointStore(checkpointPath);
        var writer = new XmlBatchWriter();
        await using var apiClient = new PeopleApiClient(endpoint);
        var runner = new SyncRunner(parser, writer, apiClient, checkpointStore);
        return await runner.RunAsync(inputPath);
    }

    private static string BuildPeopleFile(int peopleCount)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < peopleCount; index++)
        {
            builder.Append("P|First").Append(index).Append("|Last").Append(index).Append('\n');
        }

        return builder.ToString();
    }

    private sealed class CrashOnceApiClient : PeopleApiClient
    {
        private int _requestCount;
        public CrashOnceApiClient(Uri endpoint) : base(endpoint)
        {
        }

        public override async Task<HttpResponseMessage> SendBatchAsync(byte[] xmlPayload, string idempotencyKey, CancellationToken cancellationToken = default)
        {
            _requestCount++;
            var response = await base.SendBatchAsync(xmlPayload, idempotencyKey, cancellationToken);
            if (_requestCount == 2)
            {
                response.Dispose();
                throw new InvalidOperationException("Simulated crash after the second batch was accepted but before checkpointing.");
            }

            return response;
        }
    }

    private const string ExampleInput = """
P|Victoria|Bernadotte
T|070-0101010|0459-123456
A|Haga Slott|Stockholm|101
F|Estelle|2012
A|Solliden|Öland|10002
P|Joe|Biden
A|White House|Washington, D.C|20500
""";
}

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PeopleSync;
using PeopleSync.TestDouble;

namespace PeopleSync.Tests;

public sealed class SentPayloadWriterTests
{
    [Fact]
    public void OutputFilenameContainsTimestampAndExtension()
    {
        var timestamp = new DateTimeOffset(2026, 10, 2, 10, 15, 30, TimeSpan.FromHours(1));

        Assert.Equal("people-output_20261002T091530Z.xml", SentPayloadWriter.BuildTimestampedPath("people-output.xml", timestamp));
        var nested = SentPayloadWriter.BuildTimestampedPath(Path.Combine("out", "people.xml"), timestamp);
        Assert.Equal(Path.Combine("out", "people_20261002T091530Z.xml"), nested);
        Assert.Matches(new Regex(@"_\d{8}T\d{6}Z\.xml$"), nested);
    }

    [Fact]
    public async Task OutputFileIsWellFormedAndMatchesAcceptedPeople()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        await using var fakeSystemB = await FakeSystemB.StartAsync(new FakeSystemBOptions());
        try
        {
            var outputPath = Path.Combine(tempDirectory.FullName, "nested", "out.xml");
            var input = await WriteInputAsync(tempDirectory.FullName, 501);

            var summary = await RunAsync(tempDirectory.FullName, input, fakeSystemB, outputPath, "checkpoint.log");

            Assert.Equal(2, summary.SentBatches);
            var bytes = await File.ReadAllBytesAsync(outputPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
            Assert.Equal("people", document.Root!.Name.LocalName);
            Assert.Equal(501, document.Root.Elements("person").Count());
            Assert.Equal(fakeSystemB.State.ReceivedPeople.Count, document.Root.Elements("person").Count());
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SkippedBatchesAreNotWrittenOnResume()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        await using var fakeSystemB = await FakeSystemB.StartAsync(new FakeSystemBOptions());
        try
        {
            var input = await WriteInputAsync(tempDirectory.FullName, 501);
            var first = Path.Combine(tempDirectory.FullName, "first.xml");
            var second = Path.Combine(tempDirectory.FullName, "second.xml");

            await RunAsync(tempDirectory.FullName, input, fakeSystemB, first, "checkpoint.log");
            var summary = await RunAsync(tempDirectory.FullName, input, fakeSystemB, second, "checkpoint.log");

            Assert.Equal(2, summary.SkippedBatches);
            var document = XDocument.Load(second);
            Assert.Empty(document.Root!.Elements("person"));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static async Task<string> WriteInputAsync(string directory, int count)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            builder.Append("P|First").Append(index).Append("|Last").Append(index).Append('\n');
        }

        var path = Path.Combine(directory, "people.txt");
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8);
        return path;
    }

    private static async Task<SyncRunSummary> RunAsync(string directory, string input, FakeSystemBHost host, string outputPath, string checkpointName)
    {
        var parser = new PersonFileParser(new BadRecordLogger(Path.Combine(directory, "errors.jsonl")));
        await using var apiClient = new PeopleApiClient(new Uri(host.BaseUri, "/people/batch"));
        await using var output = new SentPayloadWriter(outputPath);
        var runner = new SyncRunner(parser, new XmlBatchWriter(), apiClient, new CheckpointStore(Path.Combine(directory, checkpointName)), output);
        return await runner.RunAsync(input);
    }
}

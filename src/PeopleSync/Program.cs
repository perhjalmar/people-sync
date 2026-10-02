using System.CommandLine;
using System.Text;

namespace PeopleSync;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return await CreateCommand().Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    internal static RootCommand CreateCommand()
    {
        var inputOption = new Option<FileInfo>("--input")
        {
            Description = "Absolute or relative path to the System A people file",
            Required = true
        };
        var apiOption = new Option<string>("--api")
        {
            Description = "System B batch endpoint URL, for example http://localhost:5000/people/batch",
            Required = true
        };
        var checkpointOption = new Option<FileInfo>("--checkpoint")
        {
            Description = "Checkpoint log path",
            DefaultValueFactory = _ => new FileInfo("checkpoint.log")
        };
        var errorsOption = new Option<FileInfo>("--errors")
        {
            Description = "Bad-record JSONL log path",
            DefaultValueFactory = _ => new FileInfo("errors.jsonl")
        };
        var outputOption = new Option<FileInfo>("--output")
        {
            Description = "Path for the file with the people records sent to System B; a UTC timestamp is inserted before the extension",
            DefaultValueFactory = _ => new FileInfo("people-output.xml")
        };

        var command = new RootCommand("Synchronize people from a legacy export file into System B.");
        command.Add(inputOption);
        command.Add(apiOption);
        command.Add(checkpointOption);
        command.Add(errorsOption);
        command.Add(outputOption);

        command.SetAction(async parseResult =>
        {
            var input = parseResult.GetRequiredValue(inputOption);
            var apiText = parseResult.GetRequiredValue(apiOption);
            var checkpoint = parseResult.GetValue(checkpointOption) ?? new FileInfo("checkpoint.log");
            var errors = parseResult.GetValue(errorsOption) ?? new FileInfo("errors.jsonl");
            var output = parseResult.GetValue(outputOption) ?? new FileInfo("people-output.xml");
            if (!Uri.TryCreate(apiText, UriKind.Absolute, out var api))
            {
                Console.Error.WriteLine($"The --api value '{apiText}' is not a valid absolute URI.");
                return 1;
            }

            var outputPath = SentPayloadWriter.BuildTimestampedPath(output.FullName, DateTimeOffset.UtcNow);
            Console.WriteLine($"Output file: {outputPath}");

            var logger = new BadRecordLogger(errors.FullName);
            var parser = new PersonFileParser(logger);
            var writer = new XmlBatchWriter();
            var checkpointStore = new CheckpointStore(checkpoint.FullName);
            await using var apiClient = new PeopleApiClient(api);
            await using var sentPayloadWriter = new SentPayloadWriter(outputPath);
            var runner = new SyncRunner(parser, writer, apiClient, checkpointStore, sentPayloadWriter);
            var summary = await runner.RunAsync(input.FullName).ConfigureAwait(false);
            Console.WriteLine($"Fingerprint: {summary.Fingerprint}");
            Console.WriteLine($"Parsed people: {summary.ParsedPeople}");
            Console.WriteLine($"Sent batches: {summary.SentBatches}");
            Console.WriteLine($"Skipped batches: {summary.SkippedBatches}");
            return 0;
        });

        return command;
    }
}

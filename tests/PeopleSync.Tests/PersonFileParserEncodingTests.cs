using System.Text;
using PeopleSync;

namespace PeopleSync.Tests;

public sealed class PersonFileParserEncodingTests
{
    static PersonFileParserEncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private const string Content = "P|Åsa|Öberg\nA|Västra gatan|Malmö|123\n";

    private static async Task<List<Person>> ParseBytesAsync(byte[] bytes, string encoding = "auto")
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = Path.Combine(dir.FullName, "in.txt");
            await File.WriteAllBytesAsync(input, bytes);
            var parser = new PersonFileParser(new BadRecordLogger(Path.Combine(dir.FullName, "e.jsonl")), encoding);
            var list = new List<Person>();
            await foreach (var p in parser.ParseAsync(input))
            {
                list.Add(p);
            }

            return list;
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task Utf8WithBomIsReadWithoutBomArtifact()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Content)).ToArray();
        var people = await ParseBytesAsync(bytes);
        Assert.Equal("Åsa", Assert.Single(people).FirstName);
    }

    [Fact]
    public async Task Utf8WithoutBomAsciiIsRead()
    {
        var people = await ParseBytesAsync(Encoding.ASCII.GetBytes("P|Anna|Berg\n"));
        Assert.Equal("Anna", Assert.Single(people).FirstName);
    }

    [Fact]
    public async Task Windows1252FallbackReadsSwedishCharacters()
    {
        var people = await ParseBytesAsync(Encoding.GetEncoding(1252).GetBytes(Content));
        var person = Assert.Single(people);
        Assert.Equal("Åsa", person.FirstName);
        Assert.Equal("Öberg", person.LastName);
        Assert.Equal("Malmö", person.Address?.City);
    }

    [Fact]
    public async Task MultibyteCharacterSplitAtSampleBoundaryStaysUtf8()
    {
        var prefix = "P|Anna|" + new string('x', 64 * 1024 - 7 - 1) + "\n";
        var bytes = Encoding.UTF8.GetBytes(prefix + "P|Åsa|Ö\n");
        // 'Å' starts in the last byte of the sample region
        Assert.Equal(64 * 1024, prefix.Length);
        var people = await ParseBytesAsync(bytes);
        Assert.Equal(2, people.Count);
        Assert.Equal("Åsa", people[1].FirstName);

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 64 * 1024 - 1) + "å"));
        var enc = PersonFileParser.DetectEncoding(ms, out _);
        Assert.Equal("utf-8", enc.WebName);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public async Task ExplicitEncodingOverridesDetection()
    {
        // Valid UTF-8 bytes forced to be read as 1252 produce mojibake
        var people = await ParseBytesAsync(Encoding.UTF8.GetBytes(Content), "windows-1252");
        Assert.NotEqual("Åsa", Assert.Single(people).FirstName);
    }

    [Fact]
    public void EncodingOptionParsesAndRejectsInvalid()
    {
        var cmd = Program.CreateCommand();
        Assert.Empty(cmd.Parse("--input a --api http://x --encoding windows-1252").Errors);
        Assert.Empty(cmd.Parse("--input a --api http://x").Errors);
        Assert.NotEmpty(cmd.Parse("--input a --api http://x --encoding latin9").Errors);
    }
}

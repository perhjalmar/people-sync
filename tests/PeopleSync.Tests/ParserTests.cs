using System.Text;
using System.Text.Json;
using PeopleSync;

namespace PeopleSync.Tests;

public sealed class ParserTests
{
    static ParserTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public async Task ExampleInputParsesIntoTwoPeople()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "people.txt");
            var errorsPath = Path.Combine(tempDirectory.FullName, "errors.jsonl");
            await File.WriteAllTextAsync(inputPath, ExampleInput, Encoding.GetEncoding(1252));

            var parser = new PersonFileParser(new BadRecordLogger(errorsPath));
            var people = await ToListAsync(parser.ParseAsync(inputPath));

            Assert.Equal(2, people.Count);

            var victoria = people[0];
            Assert.Equal("Victoria", victoria.FirstName);
            Assert.Equal("Bernadotte", victoria.LastName);
            Assert.Equal("Haga Slott", victoria.Address?.Street);
            Assert.Equal("Stockholm", victoria.Address?.City);
            Assert.Equal("101", victoria.Address?.PostalCode);
            Assert.Equal("070-0101010", victoria.Phone?.Mobile);
            Assert.Equal("0459-123456", victoria.Phone?.Landline);
            Assert.Equal(2, victoria.FamilyMembers.Count);
            Assert.Equal("Estelle", victoria.FamilyMembers[0].Name);
            Assert.Equal("2012", victoria.FamilyMembers[0].Born);
            Assert.Equal("Solliden", victoria.FamilyMembers[0].Address?.Street);
            Assert.Equal("Öland", victoria.FamilyMembers[0].Address?.City);
            Assert.Equal("10002", victoria.FamilyMembers[0].Address?.PostalCode);
            Assert.Equal("Oscar", victoria.FamilyMembers[1].Name);
            Assert.Equal("0702-020202", victoria.FamilyMembers[1].Phone?.Mobile);
            Assert.Equal("02-202020", victoria.FamilyMembers[1].Phone?.Landline);

            var joe = people[1];
            Assert.Equal("Joe", joe.FirstName);
            Assert.Equal("Biden", joe.LastName);
            Assert.Equal("White House", joe.Address?.Street);
            Assert.Equal("Washington, D.C", joe.Address?.City);
            Assert.Equal(string.Empty, await ReadErrorsAsync(errorsPath));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task MalformedLinesAreLoggedAndParsingContinues()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "people.txt");
            var errorsPath = Path.Combine(tempDirectory.FullName, "errors.jsonl");
            await File.WriteAllTextAsync(
                inputPath,
                "P|Victoria|Bernadotte\nT|070-0101010\nA|Street|City\nP|Joe|Biden\nA|White House|Washington, D.C|20500\nBAD|oops\n",
                Encoding.UTF8);

            var parser = new PersonFileParser(new BadRecordLogger(errorsPath));
            var people = await ToListAsync(parser.ParseAsync(inputPath));

            Assert.Equal(2, people.Count);
            Assert.Equal("Victoria", people[0].FirstName);
            Assert.Equal("Joe", people[1].FirstName);

            var entries = await ReadEntriesAsync(errorsPath);
            Assert.Contains(entries, entry => entry.GetProperty("message").GetString() == "Malformed T record.");
            Assert.Contains(entries, entry => entry.GetProperty("message").GetString() == "Malformed A record.");
            Assert.Contains(entries, entry => entry.GetProperty("message").GetString() == "Unknown record type 'BAD'.");
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OrphanRecordsAreLogged()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "people.txt");
            var errorsPath = Path.Combine(tempDirectory.FullName, "errors.jsonl");
            await File.WriteAllTextAsync(inputPath, "T|1|2\nA|Street|City|1\nF|Chris|2001\nP|Joe|Biden\n", Encoding.UTF8);

            var parser = new PersonFileParser(new BadRecordLogger(errorsPath));
            var people = await ToListAsync(parser.ParseAsync(inputPath));

            Assert.Single(people);
            var entries = await ReadEntriesAsync(errorsPath);
            Assert.Contains(entries, entry => entry.GetProperty("message").GetString() == "Orphan T record without a person.");
            Assert.Contains(entries, entry => entry.GetProperty("message").GetString() == "Orphan A record without a person.");
            Assert.Contains(entries, entry => entry.GetProperty("message").GetString() == "Orphan F record without a person.");
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateOptionalBlocksLogWarningsAndKeepFirstOccurrence()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var inputPath = Path.Combine(tempDirectory.FullName, "people.txt");
            var errorsPath = Path.Combine(tempDirectory.FullName, "errors.jsonl");
            await File.WriteAllTextAsync(
                inputPath,
                "P|Victoria|Bernadotte\nD|Crown Princess\nD|Ignored\nA|Street 1|City 1|111\nA|Street 2|City 2|222\nT|1|2\nT|3|4\nF|Estelle|2012\nA|Family Street 1|Family City 1|333\nA|Family Street 2|Family City 2|444\nT|5|6\nT|7|8\n",
                Encoding.UTF8);

            var parser = new PersonFileParser(new BadRecordLogger(errorsPath));
            var people = await ToListAsync(parser.ParseAsync(inputPath));

            var person = Assert.Single(people);
            Assert.Equal("Crown Princess", person.Description);
            Assert.Equal("Street 1", person.Address?.Street);
            Assert.Equal("1", person.Phone?.Mobile);
            Assert.Equal("Family Street 1", person.FamilyMembers[0].Address?.Street);
            Assert.Equal("5", person.FamilyMembers[0].Phone?.Mobile);

            var entries = await ReadEntriesAsync(errorsPath);
            Assert.Equal(5, entries.Count);
            Assert.All(entries, entry => Assert.Equal("warning", entry.GetProperty("level").GetString()));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private static async Task<List<JsonElement>> ReadEntriesAsync(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path);
        return lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
    }

    private static async Task<string> ReadErrorsAsync(string path)
    {
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    }

    private static async Task<List<Person>> ToListAsync(IAsyncEnumerable<Person> people)
    {
        var list = new List<Person>();
        await foreach (var person in people)
        {
            list.Add(person);
        }

        return list;
    }

    private const string ExampleInput = """
P|Victoria|Bernadotte
T|070-0101010|0459-123456
A|Haga Slott|Stockholm|101
F|Estelle|2012
A|Solliden|Öland|10002
F|Oscar|2016
T|0702-020202|02-202020
P|Joe|Biden
A|White House|Washington, D.C|
""";
}

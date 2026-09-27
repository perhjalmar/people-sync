using System.Text;
using PeopleSync;

namespace PeopleSync.Tests;

public sealed class XmlWriterTests
{
    [Fact]
    public async Task VictoriaBatchMatchesDocumentedExampleByteForByte()
    {
        var person = new Person
        {
            FirstName = "Victoria",
            LastName = "Bernadotte",
            Address = new Address
            {
                Street = "Haga Slott",
                City = "Stockholm",
                PostalCode = "101"
            },
            Phone = new Phone
            {
                Mobile = "070-0101010",
                Landline = "0459-123456"
            }
        };

        person.FamilyMembers.Add(new FamilyMember
        {
            Name = "Estelle",
            Born = "2012",
            Address = new Address
            {
                Street = "Solliden",
                City = "Öland",
                PostalCode = "10002"
            }
        });
        person.FamilyMembers.Add(new FamilyMember
        {
            Name = "Oscar",
            Born = "2016",
            Phone = new Phone
            {
                Mobile = "0702-020202",
                Landline = "02-202020"
            }
        });

        await using var stream = new MemoryStream();
        var writer = new XmlBatchWriter();
        await writer.WriteBatchAsync([person], stream);

        var actualBytes = stream.ToArray();
        var expectedBytes = Encoding.UTF8.GetBytes(ExpectedXml);
        Assert.Equal(expectedBytes, actualBytes);
    }

    private const string ExpectedXml = """
<people>
  <person>
    <firstname>Victoria</firstname>
    <lastname>Bernadotte</lastname>
    <address>
      <street>Haga Slott</street>
      <city>Stockholm</city>
      <postcode>101</postcode>
    </address>
    <phone>
      <mobile>070-0101010</mobile>
      <landline>0459-123456</landline>
    </phone>
    <family>
      <name>Estelle</name>
      <born>2012</born>
      <address>
        <street>Solliden</street>
        <city>Öland</city>
        <postcode>10002</postcode>
      </address>
    </family>
    <family>
      <name>Oscar</name>
      <born>2016</born>
      <phone>
        <mobile>0702-020202</mobile>
        <landline>02-202020</landline>
      </phone>
    </family>
  </person>
</people>
""";
}

using System.Text;
using System.Xml;

namespace PeopleSync;

public sealed class XmlBatchWriter
{
    public async Task WriteBatchAsync(IReadOnlyCollection<Person> people, Stream output, CancellationToken cancellationToken = default)
    {
        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = true
        };

        using var writer = XmlWriter.Create(output, settings);

        await writer.WriteStartElementAsync(prefix: null, localName: "people", ns: null).ConfigureAwait(false);

        foreach (var person in people)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteStartElementAsync(null, "person", null).ConfigureAwait(false);
            await WriteElementAsync(writer, "firstname", person.FirstName).ConfigureAwait(false);
            await WriteElementAsync(writer, "lastname", person.LastName).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(person.Description))
            {
                await WriteElementAsync(writer, "description", person.Description).ConfigureAwait(false);
            }

            await WriteAddressAsync(writer, person.Address).ConfigureAwait(false);
            await WritePhoneAsync(writer, person.Phone).ConfigureAwait(false);

            foreach (var familyMember in person.FamilyMembers)
            {
                await writer.WriteStartElementAsync(null, "family", null).ConfigureAwait(false);
                await WriteElementAsync(writer, "name", familyMember.Name).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(familyMember.Born))
                {
                    await WriteElementAsync(writer, "born", familyMember.Born).ConfigureAwait(false);
                }

                await WriteAddressAsync(writer, familyMember.Address).ConfigureAwait(false);
                await WritePhoneAsync(writer, familyMember.Phone).ConfigureAwait(false);
                await writer.WriteEndElementAsync().ConfigureAwait(false);
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteAddressAsync(XmlWriter writer, Address? address)
    {
        if (address is null)
        {
            return;
        }

        await writer.WriteStartElementAsync(null, "address", null).ConfigureAwait(false);
        await WriteElementAsync(writer, "street", address.Street).ConfigureAwait(false);
        await WriteElementAsync(writer, "city", address.City).ConfigureAwait(false);
        if (address.PostalCode is not null)
        {
            await WriteElementAsync(writer, "postcode", address.PostalCode).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WritePhoneAsync(XmlWriter writer, Phone? phone)
    {
        if (phone is null)
        {
            return;
        }

        await writer.WriteStartElementAsync(null, "phone", null).ConfigureAwait(false);
        await WriteElementAsync(writer, "mobile", phone.Mobile).ConfigureAwait(false);
        await WriteElementAsync(writer, "landline", phone.Landline).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static Task WriteElementAsync(XmlWriter writer, string name, string? value)
    {
        return writer.WriteElementStringAsync(null, name, null, value ?? string.Empty);
    }
}

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace PeopleSync;

public sealed class PersonFileParser
{
    private static readonly ConcurrentDictionary<string, byte> EncodingProviderInitialized = new();
    private readonly BadRecordLogger _badRecordLogger;

    public PersonFileParser(BadRecordLogger badRecordLogger)
    {
        _badRecordLogger = badRecordLogger;
    }

    public async IAsyncEnumerable<Person> ParseAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        using var reader = CreateReader(stream);

        Person? currentPerson = null;
        FamilyMember? currentFamily = null;
        var lineNumber = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (rawLine is null)
            {
                break;
            }

            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('|');
            var recordType = parts[0].Trim();

            switch (recordType)
            {
                case "P":
                    if (parts.Length != 3)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Malformed P record.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (currentPerson is not null)
                    {
                        yield return currentPerson;
                    }

                    currentPerson = new Person
                    {
                        FirstName = parts[1].Trim(),
                        LastName = parts[2].Trim()
                    };
                    currentFamily = null;
                    break;

                case "D":
                    if (parts.Length != 2)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Malformed D record.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (currentPerson is null)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Orphan D record without a person.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (currentFamily is not null)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Description records cannot be attached to a family member.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (currentPerson.Description is not null)
                    {
                        await _badRecordLogger.LogWarningAsync(lineNumber, rawLine, "Duplicate D record ignored for person.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    currentPerson.Description = parts[1].Trim();
                    break;

                case "T":
                    if (parts.Length != 3)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Malformed T record.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (currentPerson is null)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Orphan T record without a person.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var phone = new Phone
                    {
                        Mobile = parts[1].Trim(),
                        Landline = parts[2].Trim()
                    };

                    if (currentFamily is not null)
                    {
                        if (currentFamily.Phone is not null)
                        {
                            await _badRecordLogger.LogWarningAsync(lineNumber, rawLine, "Duplicate T record ignored for family member.", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        currentFamily.Phone = phone;
                        break;
                    }

                    if (currentPerson.Phone is not null)
                    {
                        await _badRecordLogger.LogWarningAsync(lineNumber, rawLine, "Duplicate T record ignored for person.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    currentPerson.Phone = phone;
                    break;

                case "A":
                    if (parts.Length != 4)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Malformed A record.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (currentPerson is null)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Orphan A record without a person.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var address = new Address
                    {
                        Street = parts[1].Trim(),
                        City = parts[2].Trim(),
                        PostalCode = parts[3].Trim()
                    };

                    if (currentFamily is not null)
                    {
                        if (currentFamily.Address is not null)
                        {
                            await _badRecordLogger.LogWarningAsync(lineNumber, rawLine, "Duplicate A record ignored for family member.", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        currentFamily.Address = address;
                        break;
                    }

                    if (currentPerson.Address is not null)
                    {
                        await _badRecordLogger.LogWarningAsync(lineNumber, rawLine, "Duplicate A record ignored for person.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    currentPerson.Address = address;
                    break;

                case "F":
                    if (parts.Length != 3)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Malformed F record.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (currentPerson is null)
                    {
                        await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, "Orphan F record without a person.", cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    currentFamily = new FamilyMember
                    {
                        Name = parts[1].Trim(),
                        Born = parts[2].Trim()
                    };
                    currentPerson.FamilyMembers.Add(currentFamily);
                    break;

                default:
                    await _badRecordLogger.LogMalformedAsync(lineNumber, rawLine, $"Unknown record type '{recordType}'.", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (currentPerson is not null)
        {
            yield return currentPerson;
        }
    }

    private static StreamReader CreateReader(FileStream stream)
    {
        EnsureEncodingProvider();

        Span<byte> preamble = stackalloc byte[3];
        var bytesRead = stream.Read(preamble);
        var hasUtf8Bom = bytesRead >= 3 && preamble[0] == 0xEF && preamble[1] == 0xBB && preamble[2] == 0xBF;
        var encoding = hasUtf8Bom
            ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            : Encoding.GetEncoding(1252);

        stream.Position = hasUtf8Bom ? 3 : 0;
        return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: false);
    }

    private static void EnsureEncodingProvider()
    {
        if (EncodingProviderInitialized.TryAdd(nameof(CodePagesEncodingProvider), 0))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}

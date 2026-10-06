using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;

namespace PeopleSync;

public sealed class PersonFileParser
{
    private static readonly ConcurrentDictionary<string, byte> EncodingProviderInitialized = new();
    private const int EncodingSampleSize = 64 * 1024;
    private readonly BadRecordLogger _badRecordLogger;
    private readonly string? _encodingOption;

    public PersonFileParser(BadRecordLogger badRecordLogger, string? encodingOption = "auto")
    {
        _badRecordLogger = badRecordLogger;
        _encodingOption = encodingOption;
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

    private StreamReader CreateReader(FileStream stream)
    {
        EnsureEncodingProvider();

        Encoding encoding;
        string reason;
        if (TryGetExplicitEncoding(_encodingOption, out var explicitEncoding))
        {
            encoding = explicitEncoding;
            reason = "specified with --encoding";
        }
        else
        {
            encoding = DetectEncoding(stream, out reason);
        }

        Console.WriteLine($"Input encoding: {encoding.WebName} ({reason})");

        // BOM detection stays on so a UTF-8 BOM is consumed instead of leaking into the first field.
        return new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16, leaveOpen: false);
    }

    internal static bool TryGetExplicitEncoding(string? option, out Encoding encoding)
    {
        EnsureEncodingProvider();
        switch (option?.Trim().ToLowerInvariant())
        {
            case "utf-8":
            case "utf8":
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                return true;
            case "windows-1252":
            case "cp1252":
            case "1252":
                encoding = Encoding.GetEncoding(1252);
                return true;
            case null:
            case "":
            case "auto":
                encoding = Encoding.UTF8;
                return false;
            default:
                throw new ArgumentException($"Unsupported encoding '{option}'. Use utf-8, windows-1252 or auto.", nameof(option));
        }
    }

    internal static Encoding DetectEncoding(Stream stream, out string reason)
    {
        EnsureEncodingProvider();

        var sample = new byte[EncodingSampleSize];
        var read = 0;
        int n;
        while (read < sample.Length && (n = stream.Read(sample, read, sample.Length - read)) > 0)
        {
            read += n;
        }

        stream.Position = 0;

        if (read >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
        {
            reason = "UTF-8 BOM";
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            // flush: false tolerates a multibyte character cut off at the end of the sample.
            strictUtf8.GetDecoder().GetCharCount(sample, 0, read, flush: false);
            reason = "valid UTF-8";
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (DecoderFallbackException)
        {
            reason = "not valid UTF-8, falling back to Windows-1252";
            return Encoding.GetEncoding(1252);
        }
    }

    private static void EnsureEncodingProvider()
    {
        if (EncodingProviderInitialized.TryAdd(nameof(CodePagesEncodingProvider), 0))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}

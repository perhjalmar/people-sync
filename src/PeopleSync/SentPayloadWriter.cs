using System.Text;
using System.Xml;

namespace PeopleSync;

/// <summary>
/// Streams every accepted batch into one well-formed &lt;people&gt; document. The closing tag is
/// rewritten after each batch, so the file is valid XML even if the process crashes.
/// </summary>
public sealed class SentPayloadWriter : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Header = Utf8NoBom.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<people>\n");
    private static readonly byte[] Footer = Utf8NoBom.GetBytes("</people>\n");

    private readonly FileStream _stream;

    public SentPayloadWriter(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 4096, useAsync: true);
        _stream.Write(Header);
        _stream.Write(Footer);
        _stream.Flush();
    }

    public string Path { get; }

    public static string BuildTimestampedPath(string path, DateTimeOffset timestamp)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);
        var stamped = $"{name}_{timestamp.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}{extension}";
        return string.IsNullOrEmpty(directory) ? stamped : System.IO.Path.Combine(directory, stamped);
    }

    public async Task AppendBatchAsync(byte[] batchXml, CancellationToken cancellationToken = default)
    {
        _stream.Seek(-Footer.Length, SeekOrigin.End);

        var settings = new XmlWriterSettings
        {
            Async = true,
            Encoding = Utf8NoBom,
            ConformanceLevel = ConformanceLevel.Fragment,
            OmitXmlDeclaration = true,
            CloseOutput = false,
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace
        };

        using (var source = new MemoryStream(batchXml, writable: false))
        using (var reader = XmlReader.Create(source, new XmlReaderSettings { Async = true }))
        await using (var writer = XmlWriter.Create(_stream, settings))
        {
            reader.MoveToContent();
            reader.ReadStartElement("people");
            while (reader.MoveToContent() == XmlNodeType.Element)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteNodeAsync(reader, defattr: true).ConfigureAwait(false);
                await writer.WriteWhitespaceAsync("\n").ConfigureAwait(false);
            }
        }

        await _stream.WriteAsync(Footer, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

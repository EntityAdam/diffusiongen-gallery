using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Gallery.Core.Services.Processors;

internal static class TagCsvProcessor
{
    public static IReadOnlyList<string> Read(Stream content)
    {
        try
        {
            using var reader = new StreamReader(content, new UTF8Encoding(true, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
            parser.SetDelimiters(",");
            var header = parser.ReadFields();
            if (header is not { Length: 1 } || !header[0].Trim().Equals("tag", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The CSV must have one column with the header 'tag'.");
            var tags = new List<string>();
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields is not { Length: 1 })
                    throw new InvalidOperationException("Each CSV row must contain exactly one tag column.");
                tags.Add(fields[0]);
            }
            return tags;
        }
        catch (MalformedLineException exception)
        {
            throw new InvalidOperationException($"Invalid CSV quoting near line {exception.LineNumber}.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException("The tag CSV must contain valid UTF-8 text.", exception);
        }
    }

    public static byte[] Write(IEnumerable<string> tags)
    {
        var csv = new StringBuilder("tag\r\n");
        foreach (var tag in tags) csv.Append('"').Append(tag.Replace("\"", "\"\"")).Append("\"\r\n");
        return [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(csv.ToString())];
    }
}

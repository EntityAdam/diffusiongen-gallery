using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Gallery.Core.Features.Tags.TagNameValidators;

namespace Gallery.Core.Features.Tags;

internal static class TagCsvProcessor
{
    public static IReadOnlyList<string> Read(Stream content)
    {
        try
        {
            using var streamReader = new StreamReader(content, new UTF8Encoding(true, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var textFieldParser = CreateParser(streamReader);
            ReadHeader(textFieldParser);
            return ReadTagRows(textFieldParser);
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

    public static string[] ReadAndClear(MemoryStream content)
    {
        try
        {
            content.Position = 0;
            return Read(content).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(content.GetBuffer()); }
    }

    public static string[] ValidateRows(IReadOnlyList<string> rows, IReadOnlyCollection<string> existingNames) =>
        rows.Select((name, index) =>
        {
            try { return TagNameValidator.ValidateTagName(name); }
            catch (InvalidOperationException exception)
            {
                var existingName = name.Trim();
                if (existingNames.Contains(existingName, StringComparer.OrdinalIgnoreCase)) return existingName;
                throw new InvalidOperationException($"CSV tag {index + 1}: {exception.Message}", exception);
            }
        }).ToArray();

    private static TextFieldParser CreateParser(TextReader textReader)
    {
        var textFieldParser = new TextFieldParser(textReader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        textFieldParser.SetDelimiters(",");
        return textFieldParser;
    }

    private static void ReadHeader(TextFieldParser textFieldParser)
    {
        if (!IsTagHeader(textFieldParser.ReadFields()))
            throw new InvalidOperationException("The CSV must have one column with the header 'tag'.");
    }

    private static bool IsTagHeader(string[]? header) =>
        header is { Length: 1 } && header[0].Trim().Equals("tag", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadTagRows(TextFieldParser textFieldParser)
    {
        var tags = new List<string>();
        while (!textFieldParser.EndOfData)
        {
            var fields = textFieldParser.ReadFields();
            if (fields is not { Length: 1 })
                throw new InvalidOperationException("Each CSV row must contain exactly one tag column.");
            tags.Add(fields[0]);
        }
        return tags;
    }

    public static byte[] Write(IEnumerable<string> tags)
    {
        var csvText = FormatCsv(tags);
        return [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(csvText)];
    }

    private static string FormatCsv(IEnumerable<string> tags)
    {
        var csvText = new StringBuilder("tag\r\n");
        foreach (var tag in tags) csvText.Append('"').Append(tag.Replace("\"", "\"\"")).Append("\"\r\n");
        return csvText.ToString();
    }
}

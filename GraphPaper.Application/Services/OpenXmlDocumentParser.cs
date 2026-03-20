using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

namespace GraphPaper.Application.Services;

/// <summary>
/// Parses DOCX files natively using DocumentFormat.OpenXml.
/// Handles numPr-based list items at all indent levels (ilvl 0, 1, 2...)
/// which Docling silently drops when parsing DOCX via markdown fallback.
/// </summary>
public sealed class OpenXmlDocumentParser
{
    private static readonly Dictionary<string, (string Prefix, int Level)> HeadingMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Heading1"] = ("## ", 1),
            ["Heading2"] = ("## ", 2),
            ["Heading3"] = ("### ", 3),
            ["Heading4"] = ("#### ", 4),
        };

    /// <summary>
    /// Parses a DOCX byte array into a markdown string, preserving
    /// heading hierarchy and list indent levels.
    /// </summary>
    public string ParseToMarkdown(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes);
        using var wordDoc = WordprocessingDocument.Open(stream, isEditable: false);

        var body = wordDoc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Invalid DOCX: body not found.");

        var sb = new StringBuilder();

        foreach (var para in body.Elements<Paragraph>())
        {
            var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
            var text = ExtractText(para);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (HeadingMap.TryGetValue(styleId, out var heading))
            {
                if (sb.Length > 0)
                    sb.Append('\n');

                sb.Append(heading.Prefix).Append(text).Append('\n');
            }
            else
            {
                var indent = GetIndentLevel(para);
                sb.Append(GetBulletPrefix(indent)).Append(text).Append('\n');
            }
        }

        return sb.ToString().Trim();
    }

    private static int GetIndentLevel(Paragraph para)
    {
        var numPr = para.ParagraphProperties?.NumberingProperties;
        if (numPr is null)
            return -1;

        var ilvl = numPr.NumberingLevelReference?.Val?.Value;
        return ilvl.HasValue ? (int)ilvl.Value : 0;
    }

    private static string GetBulletPrefix(int indent) => indent switch
    {
        -1 => string.Empty,
        0 => "- ",
        1 => "  - ",
        2 => "    - ",
        _ => new string(' ', indent * 2) + "- "
    };

    private static string ExtractText(Paragraph para)
    {
        var sb = new StringBuilder();

        foreach (var run in para.Elements<Run>())
        {
            var sym = run.Elements<SymbolChar>().FirstOrDefault();
            if (sym is not null)
            {
                sb.Append(MapSymbol(sym));
                continue;
            }

            foreach (var t in run.Elements<Text>())
            {
                if (!string.IsNullOrEmpty(t.Text))
                    sb.Append(t.Text);
            }
        }

        return sb.ToString();
    }

    private static string MapSymbol(SymbolChar sym)
    {
        var font = sym.Font?.Value ?? string.Empty;
        var ch = sym.Char?.Value?.ToUpperInvariant() ?? string.Empty;

        if (!string.Equals(font, "Symbol", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        return ch switch
        {
            "F06D" => "m",
            "F063" => "c",
            "F076" => "v",
            "F070" => "p",
            _ => $"({ch})"
        };
    }
}

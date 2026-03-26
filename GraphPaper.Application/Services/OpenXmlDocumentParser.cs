using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

/// <summary>
/// Parses DOCX files natively using DocumentFormat.OpenXml.
/// Inline images are ignored.
/// </summary>
public sealed class OpenXmlDocumentParser
{
    private static readonly Regex FootnoteMarkerRegex =
        new(@"(?m)^\s*\d{1,3}\s*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, (string Prefix, int Level)> HeadingMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Heading1"] = ("## ", 1),
            ["Heading2"] = ("## ", 2),
            ["Heading3"] = ("### ", 3),
            ["Heading4"] = ("#### ", 4),
        };

    public OpenXmlDocumentParser()
    {
    }

    public Task<string> ParseToMarkdownAsync(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes);
        using var wordDoc = WordprocessingDocument.Open(stream, isEditable: false);

        var mainPart = wordDoc.MainDocumentPart
            ?? throw new InvalidOperationException("Invalid DOCX: MainDocumentPart not found.");

        var body = mainPart.Document?.Body
            ?? throw new InvalidOperationException("Invalid DOCX: body not found.");

        var sb = new StringBuilder();

        foreach (var element in body.Elements<OpenXmlElement>())
        {
            switch (element)
            {
                case DocumentFormat.OpenXml.Wordprocessing.Paragraph para:
                    AppendParagraph(sb, para);
                    break;
                case Table table:
                    AppendTable(sb, table);
                    break;
            }
        }

        var raw = sb.ToString().Trim();
        raw = FootnoteMarkerRegex.Replace(raw, string.Empty).Trim();
        raw = Regex.Replace(raw, @"\n{3,}", "\n\n");
        return Task.FromResult(raw);
    }

    private static void AppendParagraph(
        StringBuilder sb, DocumentFormat.OpenXml.Wordprocessing.Paragraph para)
    {
        var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
        var text = ExtractParagraphText(para);

        if (string.IsNullOrWhiteSpace(text))
            return;

        if (HeadingMap.TryGetValue(styleId, out var heading))
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(heading.Prefix).Append(text).Append('\n');
        }
        else
        {
            var indent = GetIndentLevel(para);
            sb.Append(GetBulletPrefix(indent)).Append(text).Append('\n');
        }
    }

    private static void AppendTable(
        StringBuilder sb, Table table)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0) return;

        if (sb.Length > 0) sb.Append('\n');

        var allRows = new List<List<string>>();
        foreach (var row in rows)
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<TableCell>())
            {
                var parts = new List<string>();
                foreach (var p in cell.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
                {
                    var t = ExtractParagraphText(p).Trim();
                    if (!string.IsNullOrEmpty(t)) parts.Add(t);
                }
                cells.Add(string.Join(" ", parts));
            }
            allRows.Add(cells);
        }

        var colCount = allRows.Max(r => r.Count);
        foreach (var row in allRows)
            while (row.Count < colCount) row.Add(string.Empty);

        AppendTableRow(sb, allRows[0]);
        sb.Append('|');
        for (var c = 0; c < colCount; c++) sb.Append(" --- |");
        sb.Append('\n');
        for (var r = 1; r < allRows.Count; r++) AppendTableRow(sb, allRows[r]);
        sb.Append('\n');
    }

    private static void AppendTableRow(StringBuilder sb, List<string> cells)
    {
        sb.Append("| ");
        sb.Append(string.Join(" | ", cells.Select(c => c.Replace("|", "\\|"))));
        sb.Append(" |\n");
    }

    private static string ExtractParagraphText(
        DocumentFormat.OpenXml.Wordprocessing.Paragraph para)
    {
        var sb = new StringBuilder();

        foreach (var child in para.ChildElements)
        {
            switch (child)
            {
                case DocumentFormat.OpenXml.Math.OfficeMath oMath:
                    sb.Append(ExtractEquationText(oMath));
                    break;
                case DocumentFormat.OpenXml.Wordprocessing.Run run:
                    AppendRunContent(sb, run);
                    break;
                case Hyperlink hyperlink:
                    foreach (var innerRun in hyperlink.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>())
                        AppendRunContent(sb, innerRun);
                    break;
            }
        }

        return StripInlineCitations(sb.ToString());
    }

    private static void AppendRunContent(
        StringBuilder sb, DocumentFormat.OpenXml.Wordprocessing.Run run)
    {
        var sym = run.Elements<SymbolChar>().FirstOrDefault();
        if (sym is not null)
        {
            sb.Append(MapSymbol(sym));
            return;
        }

        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case DocumentFormat.OpenXml.Wordprocessing.Text t when !string.IsNullOrEmpty(t.Text):
                    sb.Append(t.Text);
                    break;
                case DocumentFormat.OpenXml.Wordprocessing.Drawing:
                case Picture:
                    break;
            }
        }
    }

    private static string ExtractEquationText(DocumentFormat.OpenXml.Math.OfficeMath oMath)
    {
        var tokens = oMath
            .Descendants<DocumentFormat.OpenXml.Math.Text>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => !string.IsNullOrWhiteSpace(t));
        var result = string.Join("", tokens).Trim();
        return string.IsNullOrEmpty(result) ? string.Empty : $"[{result}]";
    }

    private static string StripInlineCitations(string text) =>
        Regex.Replace(text, @"(?<=[^\s\d.,;:!?])(\d{1,2})(?=[\s.,;:!?]|$)", string.Empty);

    private static int GetIndentLevel(DocumentFormat.OpenXml.Wordprocessing.Paragraph para)
    {
        var numPr = para.ParagraphProperties?.NumberingProperties;
        if (numPr is null) return -1;
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

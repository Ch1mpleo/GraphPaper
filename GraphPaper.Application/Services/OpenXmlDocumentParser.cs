using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

/// <summary>
/// Parses DOCX files natively using DocumentFormat.OpenXml.
/// Handles:
///   - Heading hierarchy (Heading1–Heading4 ? ##/###/####)
///   - numPr-based list items at all indent levels (ilvl 0, 1, 2…)
///   - Tables ? GitHub-flavored markdown tables
///   - OMML equations ? plain-text approximation
///   - Symbol font characters ? ASCII mapping
///   - Standalone footnote markers (e.g. "1\n") ? stripped
/// </summary>
public sealed class OpenXmlDocumentParser
{
    // Strips standalone footnote/endnote reference numbers that appear as
    // a lone digit (or short digit sequence) on their own line.
    // Pattern: line that is ONLY digits (optionally surrounded by whitespace).
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

    /// <summary>
    /// Parses a DOCX byte array into a markdown string, preserving
    /// heading hierarchy, list indent levels, tables, and equations.
    /// </summary>
    public string ParseToMarkdown(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes);
        using var wordDoc = WordprocessingDocument.Open(stream, isEditable: false);

        var body = wordDoc.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Invalid DOCX: body not found.");

        var sb = new StringBuilder();

        // Iterate top-level block elements in document order.
        // This preserves the interleaving of paragraphs and tables.
        foreach (var element in body.Elements<OpenXmlElement>())
        {
            switch (element)
            {
                case Paragraph para:
                    AppendParagraph(sb, para);
                    break;

                case Table table:
                    AppendTable(sb, table);
                    break;

                    // Ignore other block-level elements (bookmarks, custom xml, etc.)
            }
        }

        var raw = sb.ToString().Trim();

        // Fix 3: Strip standalone footnote marker lines (e.g. lone "1" left by
        // footnote reference runs that have no useful text content).
        return FootnoteMarkerRegex.Replace(raw, string.Empty).Trim();
    }

    // ??????????????????????????????????????????????????????????????????????????
    // Paragraph
    // ??????????????????????????????????????????????????????????????????????????

    private static void AppendParagraph(StringBuilder sb, Paragraph para)
    {
        var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
        var text = ExtractParagraphText(para);

        if (string.IsNullOrWhiteSpace(text))
            return;

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

    // ??????????????????????????????????????????????????????????????????????????
    // Fix 1: Table ? GFM markdown table
    // ??????????????????????????????????????????????????????????????????????????

    private static void AppendTable(StringBuilder sb, Table table)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0)
            return;

        if (sb.Length > 0)
            sb.Append('\n');

        var allRows = new List<List<string>>();

        foreach (var row in rows)
        {
            var cells = row.Elements<TableCell>()
                .Select(ExtractCellText)
                .ToList();
            allRows.Add(cells);
        }

        // Normalize column count across all rows
        var colCount = allRows.Max(r => r.Count);
        foreach (var row in allRows)
            while (row.Count < colCount)
                row.Add(string.Empty);

        // Emit header row (first row)
        AppendTableRow(sb, allRows[0]);

        // Emit GFM separator row
        sb.Append('|');
        for (var c = 0; c < colCount; c++)
            sb.Append(" --- |");
        sb.Append('\n');

        // Emit data rows
        for (var r = 1; r < allRows.Count; r++)
            AppendTableRow(sb, allRows[r]);

        sb.Append('\n');
    }

    private static void AppendTableRow(StringBuilder sb, List<string> cells)
    {
        sb.Append("| ");
        sb.Append(string.Join(" | ", cells.Select(c => c.Replace("|", "\\|"))));
        sb.Append(" |\n");
    }

    private static string ExtractCellText(TableCell cell)
    {
        var parts = cell.Elements<Paragraph>()
            .Select(p => ExtractParagraphText(p).Trim())
            .Where(t => !string.IsNullOrEmpty(t));
        return string.Join(" ", parts);
    }

    // ??????????????????????????????????????????????????????????????????????????
    // Text extraction helpers
    // ??????????????????????????????????????????????????????????????????????????

    private static string ExtractParagraphText(Paragraph para)
    {
        var sb = new StringBuilder();

        foreach (var child in para.Elements<OpenXmlElement>())
        {
            switch (child)
            {
                // Fix 2: OMML equation block — extract readable approximation
                case DocumentFormat.OpenXml.Math.OfficeMath oMath:
                    sb.Append(ExtractEquationText(oMath));
                    break;

                case Run run:
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
                    break;

                // Hyperlink text
                case Hyperlink hyperlink:
                    foreach (var run in hyperlink.Elements<Run>())
                        foreach (var t in run.Elements<Text>())
                            if (!string.IsNullOrEmpty(t.Text))
                                sb.Append(t.Text);
                    break;
            }
        }

        return sb.ToString();
    }

    // ??????????????????????????????????????????????????????????????????????????
    // Fix 2: OMML equation ? plain text
    // Walks <m:r><m:t> elements inside the equation block and concatenates
    // the text tokens, separated by spaces to avoid "m=c+v" ? "m=c+v" (OK)
    // but "E=mc²" ? "E=mc" losing the exponent. We preserve base text only.
    // ??????????????????????????????????????????????????????????????????????????

    private static string ExtractEquationText(DocumentFormat.OpenXml.Math.OfficeMath oMath)
    {
        // Collect all <m:t> (math text) elements in document order
        var tokens = oMath
            .Descendants<DocumentFormat.OpenXml.Math.Text>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => !string.IsNullOrWhiteSpace(t));

        var result = string.Join("", tokens).Trim();
        return string.IsNullOrEmpty(result) ? string.Empty : $"[{result}]";
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

    // ??????????????????????????????????????????????????????????????????????????
    // Symbol font mapping
    // ??????????????????????????????????????????????????????????????????????????

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

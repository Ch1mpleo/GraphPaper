using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Math;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GraphPaper.Application.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphPaper.Application.Services;

/// <summary>
/// Parses DOCX files natively using DocumentFormat.OpenXml.
/// Inline image formulas are described by IImageDescriptionService (Gemini Vision)
/// with SHA-256 cache — duplicate images cost 0 extra API calls.
/// </summary>
public sealed class OpenXmlDocumentParser
{
    private readonly IImageDescriptionService _visionService;

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

    public OpenXmlDocumentParser(IImageDescriptionService visionService)
    {
        _visionService = visionService;
    }

    public async Task<string> ParseToMarkdownAsync(byte[] fileBytes)
    {
        using var stream = new MemoryStream(fileBytes);
        using var wordDoc = WordprocessingDocument.Open(stream, isEditable: false);

        var mainPart = wordDoc.MainDocumentPart
            ?? throw new InvalidOperationException("Invalid DOCX: MainDocumentPart not found.");

        var body = mainPart.Document?.Body
            ?? throw new InvalidOperationException("Invalid DOCX: body not found.");

        var ridToImage = BuildRidToImageMap(mainPart);
        var sb = new StringBuilder();

        foreach (var element in body.Elements<OpenXmlElement>())
        {
            switch (element)
            {
                case DocumentFormat.OpenXml.Wordprocessing.Paragraph para:
                    await AppendParagraphAsync(sb, para, ridToImage);
                    break;
                case Table table:
                    await AppendTableAsync(sb, table, ridToImage);
                    break;
            }
        }

        var raw = sb.ToString().Trim();
        raw = FootnoteMarkerRegex.Replace(raw, string.Empty).Trim();
        raw = Regex.Replace(raw, @"\n{3,}", "\n\n");
        return raw;
    }

    private static Dictionary<string, (string MimeType, byte[] Bytes)> BuildRidToImageMap(
        MainDocumentPart mainPart)
    {
        var map = new Dictionary<string, (string, byte[])>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in mainPart.Parts)
        {
            if (rel.OpenXmlPart is not ImagePart imagePart)
                continue;
            try
            {
                using var imgStream = imagePart.GetStream();
                using var ms = new MemoryStream();
                imgStream.CopyTo(ms);
                var mimeType = imagePart.ContentType ?? InferMimeType(imagePart.Uri.ToString());
                map[rel.RelationshipId] = (mimeType, ms.ToArray());
            }
            catch { }
        }

        return map;
    }

    private static string InferMimeType(string uri) =>
        Path.GetExtension(uri).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/png"
        };

    private async Task AppendParagraphAsync(
        StringBuilder sb, DocumentFormat.OpenXml.Wordprocessing.Paragraph para,
        Dictionary<string, (string MimeType, byte[] Bytes)> ridToImage)
    {
        var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
        var text = await ExtractParagraphTextAsync(para, ridToImage);

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

    private async Task AppendTableAsync(
        StringBuilder sb, Table table,
        Dictionary<string, (string MimeType, byte[] Bytes)> ridToImage)
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
                    var t = (await ExtractParagraphTextAsync(p, ridToImage)).Trim();
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

    private async Task<string> ExtractParagraphTextAsync(
        DocumentFormat.OpenXml.Wordprocessing.Paragraph para,
        Dictionary<string, (string MimeType, byte[] Bytes)> ridToImage)
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
                    await AppendRunContentAsync(sb, run, ridToImage);
                    break;
                case Hyperlink hyperlink:
                    foreach (var innerRun in hyperlink.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>())
                        await AppendRunContentAsync(sb, innerRun, ridToImage);
                    break;
            }
        }

        return StripInlineCitations(sb.ToString());
    }

    private async Task AppendRunContentAsync(
        StringBuilder sb, DocumentFormat.OpenXml.Wordprocessing.Run run,
        Dictionary<string, (string MimeType, byte[] Bytes)> ridToImage)
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
                case DocumentFormat.OpenXml.Wordprocessing.Drawing drawing:
                    sb.Append(await ResolveDrawingAsync(drawing, ridToImage));
                    break;
                case Picture pict:
                    sb.Append(await ResolvePictureAsync(pict, ridToImage));
                    break;
            }
        }
    }

    private async Task<string> ResolveDrawingAsync(
        DocumentFormat.OpenXml.Wordprocessing.Drawing drawing,
        Dictionary<string, (string MimeType, byte[] Bytes)> ridToImage)
    {
        foreach (var blip in drawing.Descendants<DocumentFormat.OpenXml.Drawing.Blip>())
        {
            var rEmbed = blip.Embed?.Value;
            if (!string.IsNullOrEmpty(rEmbed) && ridToImage.TryGetValue(rEmbed, out var img))
                return await DescribeImageAsync(img.MimeType, img.Bytes);
        }
        return string.Empty;
    }

    private async Task<string> ResolvePictureAsync(
        Picture pict,
        Dictionary<string, (string MimeType, byte[] Bytes)> ridToImage)
    {
        foreach (var blip in pict.Descendants<DocumentFormat.OpenXml.Drawing.Blip>())
        {
            var rEmbed = blip.Embed?.Value;
            if (!string.IsNullOrEmpty(rEmbed) && ridToImage.TryGetValue(rEmbed, out var img))
                return await DescribeImageAsync(img.MimeType, img.Bytes);
        }
        return string.Empty;
    }

    private async Task<string> DescribeImageAsync(string mimeType, byte[] bytes)
    {
        var description = await _visionService.DescribeAsync(bytes, mimeType);
        if (string.IsNullOrWhiteSpace(description))
            return "[hình]";
        return description.Length > 2 ? $"[{description.Trim('[', ']')}]" : description;
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

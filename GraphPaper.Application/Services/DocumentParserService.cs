using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Utils;
using UglyToad.PdfPig;

namespace GraphPaper.Application.Services;

public class DocumentParserService : IDocumentParserService
{
    private static readonly HashSet<string> SupportedExtensions = [".pdf", ".docx"];
    private const int WordPageCharLimit = 3000;

    public List<ParsedPage> Parse(Stream fileStream, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (!SupportedExtensions.Contains(extension))
            throw ErrorHelper.BadRequest($"Unsupported file format: {extension}. Only .pdf and .docx are supported.");

        return extension switch
        {
            ".pdf" => ParsePdf(fileStream),
            ".docx" => ParseWord(fileStream),
            _ => []
        };
    }

    private static List<ParsedPage> ParsePdf(Stream stream)
    {
        var pages = new List<ParsedPage>();

        using var document = PdfDocument.Open(stream);
        foreach (var page in document.GetPages())
        {
            var text = page.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                pages.Add(new ParsedPage
                {
                    PageNumber = page.Number,
                    Content = text
                });
            }
        }

        return pages;
    }

    private static List<ParsedPage> ParseWord(Stream stream)
    {
        var pages = new List<ParsedPage>();

        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return pages;

        var paragraphs = body.Elements<Paragraph>()
            .Select(p => p.InnerText?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        // Word has no physical pages without rendering, so group paragraphs by character limit
        var buffer = new List<string>();
        int currentLength = 0;
        int pageNumber = 1;

        foreach (var para in paragraphs)
        {
            buffer.Add(para!);
            currentLength += para!.Length;

            if (currentLength >= WordPageCharLimit)
            {
                pages.Add(new ParsedPage
                {
                    PageNumber = pageNumber++,
                    Content = string.Join("\n", buffer)
                });
                buffer.Clear();
                currentLength = 0;
            }
        }

        if (buffer.Count > 0)
        {
            pages.Add(new ParsedPage
            {
                PageNumber = pageNumber,
                Content = string.Join("\n", buffer)
            });
        }

        return pages;
    }
}

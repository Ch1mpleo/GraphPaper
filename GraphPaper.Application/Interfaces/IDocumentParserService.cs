namespace GraphPaper.Application.Interfaces;

public interface IDocumentParserService
{
    /// <summary>
    /// Extracts text from a PDF or Word file, returning content grouped by page.
    /// </summary>
    List<ParsedPage> Parse(Stream fileStream, string fileName);
}

public class ParsedPage
{
    public int PageNumber { get; set; }
    public string Content { get; set; } = string.Empty;
}

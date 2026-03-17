using GraphPaper.Application.DTOs.DoclingDTO;

namespace GraphPaper.Application.Interfaces;

/// <summary>
/// Provides document parsing through Docling service.
/// </summary>
public interface IDoclingClient
{
    /// <summary>
    /// Parses a document and returns structured Docling output.
    /// </summary>
    /// <param name="fileBytes">Raw file content.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="contentType">File MIME type.</param>
    /// <returns>Structured parse result from Docling.</returns>
    Task<DoclingResult> ParseAsync(byte[] fileBytes, string fileName, string? contentType = null);
}

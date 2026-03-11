using GraphPaper.Application.DTOs.DocumentDTO;
using GraphPaper.Infrastructure.Commons;

namespace GraphPaper.Application.Interfaces;

public interface IDocumentReviewService
{
    /// <summary>
    /// Get all documents belonging to the current user (paginated).
    /// </summary>
    Task<Pagination<DocumentSummaryDto>> GetMyDocumentsAsync(int pageNumber = 1, int pageSize = 10);

    /// <summary>
    /// Get full detail of a document: chunks, entities, relationships.
    /// </summary>
    Task<DocumentDetailDto> GetDocumentDetailAsync(Guid documentId);
}

using GraphPaper.Application.DTOs.DocumentDTO;
using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Utils;
using GraphPaper.Infrastructure.Commons;
using GraphPaper.Infrastructure.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GraphPaper.Application.Services;

public class DocumentReviewService : IDocumentReviewService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClaimsService _claimsService;

    public DocumentReviewService(IUnitOfWork unitOfWork, IClaimsService claimsService)
    {
        _unitOfWork = unitOfWork;
        _claimsService = claimsService;
    }

    public async Task<Pagination<DocumentSummaryDto>> GetMyDocumentsAsync(int pageNumber = 1, int pageSize = 10)
    {
        var userId = _claimsService.GetCurrentUserId;

        var query = _unitOfWork.Documents
            .GetQueryable()
            .Where(d => d.UserId == userId && !d.IsDeleted)
            .OrderByDescending(d => d.CreatedAt);

        var totalCount = await query.CountAsync();

        var items = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new DocumentSummaryDto
            {
                Id = d.Id,
                Title = d.Title,
                Status = d.Status,
                TotalChunks = d.Chunks.Count(c => !c.IsDeleted),
                TotalEntities = d.Chunks
                    .Where(c => !c.IsDeleted)
                    .SelectMany(c => c.ExtractedEntities)
                    .Count(e => !e.IsDeleted),
                CreatedAt = d.CreatedAt
            })
            .ToListAsync();

        return new Pagination<DocumentSummaryDto>(items, totalCount, pageNumber, pageSize);
    }

    public async Task<DocumentDetailDto> GetDocumentDetailAsync(Guid documentId)
    {
        var userId = _claimsService.GetCurrentUserId;

        var document = await _unitOfWork.Documents
            .GetQueryable()
            .Where(d => d.Id == documentId && d.UserId == userId && !d.IsDeleted)
            .Select(d => new DocumentDetailDto
            {
                Id = d.Id,
                Title = d.Title,
                Status = d.Status,
                CreatedAt = d.CreatedAt,
                Chunks = d.Chunks
                    .Where(c => !c.IsDeleted)
                    .OrderBy(c => c.ChunkIndex)
                    .Select(c => new ChunkDto
                    {
                        Id = c.Id,
                        ChunkIndex = c.ChunkIndex,
                        PageNumber = c.PageNumber,
                        Content = c.Content,
                        HasEmbedding = c.Embedding != null,
                        Entities = c.ExtractedEntities
                            .Where(e => !e.IsDeleted)
                            .Select(e => new EntityDto
                            {
                                Id = e.Id,
                                Name = e.Name,
                                EntityType = e.EntityType,
                                Description = e.Description
                            }).ToList()
                    }).ToList()
            })
            .FirstOrDefaultAsync()
            ?? throw ErrorHelper.NotFound("Document not found.");

        // Collect all entity IDs for this document to query relationships
        var entityIds = document.Chunks
            .SelectMany(c => c.Entities)
            .Select(e => e.Id)
            .ToHashSet();

        // Build a name lookup from already-loaded entities
        var entityNameMap = document.Chunks
            .SelectMany(c => c.Entities)
            .ToDictionary(e => e.Id, e => e.Name);

        // Query relationships where both source and target belong to this document
        var relationships = await _unitOfWork.ExtractedRelationships
            .GetQueryable()
            .Where(r => !r.IsDeleted
                        && entityIds.Contains(r.SourceEntityId)
                        && entityIds.Contains(r.TargetEntityId))
            .Select(r => new RelationshipDto
            {
                Id = r.Id,
                SourceEntity = r.SourceEntity.Name,
                TargetEntity = r.TargetEntity.Name,
                RelationType = r.RelationType,
                ConfidenceScore = r.ConfidenceScore
            })
            .ToListAsync();

        // Flatten all entities for the top-level view
        document.Entities = document.Chunks
            .SelectMany(c => c.Entities)
            .DistinctBy(e => e.Id)
            .ToList();

        document.Relationships = relationships;

        document.Stats = new DocumentStatsDto
        {
            TotalChunks = document.Chunks.Count,
            TotalEntities = document.Entities.Count,
            TotalRelationships = document.Relationships.Count
        };

        return document;
    }
}

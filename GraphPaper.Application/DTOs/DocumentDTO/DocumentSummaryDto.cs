using GraphPaper.Domain.Enums;

namespace GraphPaper.Application.DTOs.DocumentDTO;

public class DocumentSummaryDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public DocumentStatus Status { get; set; }
    public int TotalChunks { get; set; }
    public int TotalEntities { get; set; }
    public DateTime CreatedAt { get; set; }
}

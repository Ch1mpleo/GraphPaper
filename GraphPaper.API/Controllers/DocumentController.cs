using GraphPaper.Application.DTOs.DocumentDTO;
using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace GraphPaper.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentController : ControllerBase
{
    private readonly IDocumentProcessingService _processingService;
    private readonly IDocumentReviewService _reviewService;

    public DocumentController(
        IDocumentProcessingService processingService,
        IDocumentReviewService reviewService)
    {
        _processingService = processingService;
        _reviewService = reviewService;
    }

    /// <summary>
    /// Upload a PDF or Word (.docx) document.
    /// </summary>
    [HttpPost("upload")]
    [SwaggerOperation(Summary = "Upload document", Description = "Upload PDF/Word, extract text, create chunks, generate embeddings, and build knowledge graph.")]
    [ProducesResponseType(typeof(ApiResult<Guid>), 200)]
    [ProducesResponseType(typeof(ApiResult), 400)]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResult.Failure("400", "No file uploaded."));

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not ".pdf" and not ".docx")
            return BadRequest(ApiResult.Failure("400", "Only .pdf and .docx files are supported."));

        var document = await _processingService.IngestAsync(file);

        return Ok(ApiResult<Guid>.Success(document.Id, "200", "Document uploaded and queued for processing."));
    }

    /// <summary>
    /// Get all documents of the current user (paginated).
    /// </summary>
    [HttpGet]
    [SwaggerOperation(Summary = "Get my documents", Description = "Returns a paginated summary list of all documents belonging to the current user.")]
    [ProducesResponseType(typeof(ApiResult<Pagination<DocumentSummaryDto>>), 200)]
    public async Task<IActionResult> GetMyDocuments([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
    {
        var documents = await _reviewService.GetMyDocumentsAsync(pageNumber, pageSize);
        return Ok(ApiResult<Pagination<DocumentSummaryDto>>.Success(documents));
    }

    /// <summary>
    /// Get full detail of a document: chunks, entities, relationships.
    /// </summary>
    [HttpGet("{id:guid}")]
    [SwaggerOperation(Summary = "Get document detail", Description = "Returns full document detail including chunks, extracted entities, and knowledge graph relationships.")]
    [ProducesResponseType(typeof(ApiResult<DocumentDetailDto>), 200)]
    [ProducesResponseType(typeof(ApiResult), 404)]
    public async Task<IActionResult> GetDocumentDetail(Guid id)
    {
        var detail = await _reviewService.GetDocumentDetailAsync(id);
        return Ok(ApiResult<DocumentDetailDto>.Success(detail));
    }
}

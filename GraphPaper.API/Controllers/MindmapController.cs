using GraphPaper.Application.DTOs.MindmapDTO;
using GraphPaper.Application.Interfaces;
using GraphPaper.Application.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;

namespace GraphPaper.API.Controllers;

[ApiController]
[Route("api/document/{documentId:guid}/mindmap")]
[Authorize]
public class MindmapController : ControllerBase
{
    private readonly IMindmapService _mindmapService;

    public MindmapController(IMindmapService mindmapService)
    {
        _mindmapService = mindmapService;
    }

    /// <summary>
    /// Get the Mermaid mindmap for a document.
    /// </summary>
    [HttpGet]
    [SwaggerOperation(
        Summary = "Get document mindmap",
        Description = "Returns the stored Mermaid graph code for the document's knowledge graph. Returns 404 if the mindmap has not been generated yet.")]
    [ProducesResponseType(typeof(ApiResult<MindmapDto>), 200)]
    [ProducesResponseType(typeof(ApiResult), 404)]
    public async Task<IActionResult> GetMindmap(Guid documentId)
    {
        var mindmap = await _mindmapService.GetByDocumentIdAsync(documentId);

        if (mindmap is null)
            return NotFound(ApiResult.Failure("404", "Mindmap not found. The document may still be processing."));

        return Ok(ApiResult<MindmapDto>.Success(new MindmapDto
        {
            Id = mindmap.Id,
            DocumentId = mindmap.DocumentId,
            MermaidCode = mindmap.MermaidCode,
            NodeCount = mindmap.NodeCount,
            EdgeCount = mindmap.EdgeCount,
            CreatedAt = mindmap.CreatedAt,
            UpdatedAt = mindmap.UpdatedAt
        }));
    }

    /// <summary>
    /// Regenerate the mindmap for a document.
    /// </summary>
    [HttpPost("regenerate")]
    [SwaggerOperation(
        Summary = "Regenerate document mindmap",
        Description = "Forces a fresh Mermaid mindmap to be built from the current state of the document's knowledge graph.")]
    [ProducesResponseType(typeof(ApiResult<MindmapDto>), 200)]
    public async Task<IActionResult> RegenerateMindmap(Guid documentId)
    {
        var mindmap = await _mindmapService.GenerateAndSaveAsync(documentId);

        return Ok(ApiResult<MindmapDto>.Success(new MindmapDto
        {
            Id = mindmap.Id,
            DocumentId = mindmap.DocumentId,
            MermaidCode = mindmap.MermaidCode,
            NodeCount = mindmap.NodeCount,
            EdgeCount = mindmap.EdgeCount,
            CreatedAt = mindmap.CreatedAt,
            UpdatedAt = mindmap.UpdatedAt
        }, "200", "Mindmap regenerated successfully."));
    }
}
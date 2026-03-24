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
public sealed class MindmapController : ControllerBase
{
    private readonly IMindmapService _mindmapService;

    public MindmapController(IMindmapService mindmapService)
    {
        _mindmapService = mindmapService;
    }

    [HttpGet]
    [SwaggerOperation(Summary = "Get document mindmap")]
    [ProducesResponseType(typeof(ApiResult<MindmapDto>), 200)]
    [ProducesResponseType(typeof(ApiResult), 404)]
    public async Task<IActionResult> GetMindmap(Guid documentId)
    {
        var mindmap = await _mindmapService.GetByDocumentIdAsync(documentId);
        if (mindmap is null)
            return NotFound(ApiResult.Failure("404", "Mindmap not found."));

        var entityIndex = await _mindmapService.BuildEntityIndexAsync(documentId);
        return Ok(ApiResult<MindmapDto>.Success(ToDto(mindmap, entityIndex)));
    }

    [HttpPost("regenerate")]
    [SwaggerOperation(Summary = "Regenerate document mindmap")]
    [ProducesResponseType(typeof(ApiResult<MindmapDto>), 200)]
    public async Task<IActionResult> RegenerateMindmap(Guid documentId)
    {
        var mindmap = await _mindmapService.GenerateAndSaveAsync(documentId);
        var entityIndex = await _mindmapService.BuildEntityIndexAsync(documentId);
        return Ok(ApiResult<MindmapDto>.Success(
            ToDto(mindmap, entityIndex), "200", "Mindmap regenerated successfully."));
    }

    private static MindmapDto ToDto(
        Domain.Entities.DocumentMindmap mindmap,
        Dictionary<string, MindmapEntityDto> entityIndex) => new()
    {
        Id = mindmap.Id,
        DocumentId = mindmap.DocumentId,
        MermaidCode = mindmap.MermaidCode,
        NodeCount = mindmap.NodeCount,
        EdgeCount = mindmap.EdgeCount,
        CreatedAt = mindmap.CreatedAt,
        UpdatedAt = mindmap.UpdatedAt,
        EntityIndex = entityIndex
    };
}
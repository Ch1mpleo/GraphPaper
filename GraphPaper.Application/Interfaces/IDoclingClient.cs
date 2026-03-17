using GraphPaper.Application.DTOs.DoclingDTO;
using Microsoft.AspNetCore.Http;

namespace GraphPaper.Application.Interfaces;

public interface IDoclingClient
{
    Task<DoclingResult> ParseAsync(IFormFile file);
}

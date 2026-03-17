using System.Net.Http.Json;
using GraphPaper.Application.DTOs.DoclingDTO;
using GraphPaper.Application.Interfaces;
using Microsoft.AspNetCore.Http;

namespace GraphPaper.Application.Services;

public sealed class DoclingClient : IDoclingClient
{
    private readonly HttpClient _httpClient;

    public DoclingClient(IHttpClientFactory factory)
    {
        _httpClient = factory.CreateClient("Docling");
    }

    public async Task<DoclingResult> ParseAsync(IFormFile file)
    {
        using var form = new MultipartFormDataContent();
        using var stream = file.OpenReadStream();
        form.Add(new StreamContent(stream), "files", file.FileName);

        var response = await _httpClient.PostAsync("/v1alpha/convert/file", form);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<DoclingResult>()
               ?? throw new InvalidOperationException("Empty response from Docling");
    }
}

using GraphPaper.Application.DTOs.DoclingDTO;
using GraphPaper.Application.Interfaces;
using System.Net.Http.Json;

namespace GraphPaper.Application.Services;

/// <summary>
/// HTTP client wrapper for Docling convert endpoint.
/// </summary>
public sealed class DoclingClient : IDoclingClient
{
    private readonly HttpClient _httpClient;
    private static readonly string[] ConvertEndpoints = ["/v1/convert/file", "/v1alpha/convert/file"];

    /// <summary>
    /// Creates a Docling client using the named HttpClient factory.
    /// </summary>
    /// <param name="factory">HttpClient factory instance.</param>
    public DoclingClient(IHttpClientFactory factory)
    {
        _httpClient = factory.CreateClient("Docling");
    }

    /// <inheritdoc />
    public async Task<DoclingResult> ParseAsync(byte[] fileBytes, string fileName, string? contentType = null)
    {
        ValidateInputs(fileBytes, fileName);

        string? notFoundDetail = null;

        foreach (var endpoint in ConvertEndpoints)
        {
            var endpointResult = await TryParseFromEndpointAsync(endpoint, fileBytes, fileName, contentType);

            if (endpointResult.Result is not null)
                return endpointResult.Result;

            if (endpointResult.IsNotFound)
            {
                notFoundDetail = endpointResult.ErrorBody;
                continue;
            }

            throw new HttpRequestException($"Docling API returned {endpointResult.StatusCode} on '{endpoint}': {endpointResult.ErrorBody}");
        }

        throw new HttpRequestException(
            $"Docling convert endpoint was not found. Tried: {string.Join(", ", ConvertEndpoints)}. Last response: {notFoundDetail}");
    }

    private async Task<EndpointParseResult> TryParseFromEndpointAsync(
        string endpoint,
        byte[] fileBytes,
        string fileName,
        string? contentType)
    {
        using var form = CreateMultipartContent(fileBytes, fileName, contentType);
        using var response = await _httpClient.PostAsync(endpoint, form);

        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<DoclingResult>()
                ?? throw new InvalidOperationException("Empty response from Docling.");

            return EndpointParseResult.Success(result);
        }

        var errorBody = await response.Content.ReadAsStringAsync();
        return EndpointParseResult.Failure((int)response.StatusCode, errorBody, response.StatusCode == System.Net.HttpStatusCode.NotFound);
    }

    private static void ValidateInputs(byte[] fileBytes, string fileName)
    {
        if (fileBytes.Length == 0)
            throw new ArgumentException("File content is empty.", nameof(fileBytes));

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));
    }

    private static MultipartFormDataContent CreateMultipartContent(byte[] fileBytes, string fileName, string? contentType)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);

        if (!string.IsNullOrWhiteSpace(contentType))
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        form.Add(fileContent, "files", fileName);
        return form;
    }

    private sealed class EndpointParseResult
    {
        public DoclingResult? Result { get; init; }

        public int StatusCode { get; init; }

        public string ErrorBody { get; init; } = string.Empty;

        public bool IsNotFound { get; init; }

        public static EndpointParseResult Success(DoclingResult result)
            => new()
            {
                Result = result
            };

        public static EndpointParseResult Failure(int statusCode, string errorBody, bool isNotFound)
            => new()
            {
                StatusCode = statusCode,
                ErrorBody = errorBody,
                IsNotFound = isNotFound
            };
    }
}

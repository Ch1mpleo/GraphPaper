namespace GraphPaper.Application.Interfaces;

public interface IImageDescriptionService
{
    /// <summary>
    /// Returns plain-text description of image content (formula, symbol, etc.).
    /// Results are cached by SHA-256 hash — identical images cost 0 extra API calls.
    /// Returns empty string if image has no mathematical content.
    /// </summary>
    Task<string> DescribeAsync(byte[] imageBytes, string mimeType = "image/png");
}

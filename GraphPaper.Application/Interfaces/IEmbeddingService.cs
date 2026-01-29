namespace GraphPaper.Application.Interfaces
{
    public interface IEmbeddingService
    {
        /// <summary>
        /// Transforms a single text chunk into a vector (dimension varies by model).
        /// Gemini text-embedding-004 produces 768-dimension vectors.
        /// </summary>
        Task<float[]> GetEmbeddingAsync(string text);

        /// <summary>
        /// Processes multiple chunks in one request (Batching). 
        /// Faster for large PDFs.
        /// </summary>
        Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts);
    }
}

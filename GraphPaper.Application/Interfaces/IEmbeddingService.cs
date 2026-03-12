namespace GraphPaper.Application.Interfaces
{
    public interface IEmbeddingService
    {
        /// <summary>
        /// Transforms a single text chunk into a vector.
        /// gemini-embedding-exp-03-07 configured to produce 1536-dimension vectors.
        /// </summary>
        Task<float[]> GetEmbeddingAsync(string text);

        /// <summary>
        /// Processes multiple chunks in one request (Batching). 
        /// Faster for large PDFs.
        /// </summary>
        Task<List<float[]>> GetBatchEmbeddingsAsync(List<string> texts);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Gemini
{
    public class MemoryManager : IDisposable
    {
        private readonly Logger _logger;
        private readonly SQLiteMemoryStore _memoryStore;
        private readonly GeminiClient _geminiClient;
        private bool _disposed;

        public MemoryManager(Logger logger, GeminiClient geminiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _geminiClient = geminiClient ?? throw new ArgumentNullException(nameof(geminiClient));

            // Define the path for the SQLite database
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(homeDir, ".gemini-memories.db");

            // Ensure the directory exists for the SQLite database
            var directoryName = Path.GetDirectoryName(dbPath);
            if (directoryName != null)
            {
                Directory.CreateDirectory(directoryName);
            }

            _memoryStore = new SQLiteMemoryStore(dbPath, _logger);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _memoryStore.Dispose();
                }
                _disposed = true;
            }
        }

        ~MemoryManager()
        {
            Dispose(false);
        }

        public async Task<long> StoreMemory(string content)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            try
            {
                if (string.IsNullOrEmpty(content))
                {
                    throw new ArgumentNullException(nameof(content));
                }

                // Generate a summary using the LLM
                var summary = await _geminiClient.GenerateSummary(content);
                if (string.IsNullOrEmpty(summary))
                {
                    summary = content.Length > 100 ? content.Substring(0, 100) + "..." : content;
                    _logger.Log("Failed to generate summary, using truncated content as summary");
                }

                // Generate embedding for the content
                var embedding = Embeddings.ComputeEmbedding(content);
                if (embedding == null || embedding.Length == 0)
                {
                    _logger.Log("Failed to generate embedding, using default embedding");
                    embedding = new float[1] { 0f }; // Use a default embedding
                }

                // Store in SQLite and return the ID
                var id = _memoryStore.StoreMemory(summary, content, embedding);
                _logger.Log($"Stored memory in SQLite database with ID: {id}");
                return id;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error storing memory: {ex.Message}");
                throw;
            }
        }

        public List<(string content, float score)> SearchMemories(string query, int maxResults = 3)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    _logger.Log("SearchMemories: Query is empty");
                    return new List<(string, float)>();
                }

                // First try exact text search
                var summaryResults = _memoryStore.SearchSummaries(query, maxResults * 2);
                var results = _memoryStore.SearchFullContent(query, summaryResults.Select(r => r.id).ToList(), maxResults);

                // If no exact matches found, perform semantic search
                if (!results.Any())
                {
                    _logger.Log("No exact matches found, performing semantic search");
                    
                    // Get query embedding
                    var queryEmbedding = Embeddings.ComputeEmbedding(query);
                    
                    // Get memories with embeddings
                    var memoriesWithEmbeddings = _memoryStore.GetAllMemoriesWithEmbeddings();
                    
                    // Process sequentially since these are CPU-bound operations
                    var semanticResults = memoriesWithEmbeddings
                        .Select(memory => (
                            content: memory.content,
                            score: Embeddings.CosineSimilarity(queryEmbedding, memory.embedding)
                        ))
                        .Where(r => r.score > 0.1f)
                        .OrderByDescending(r => r.score)
                        .Take(maxResults)
                        .ToList();

                    results = semanticResults;
                }

                _logger.Log($"SearchMemories: Found {results.Count} relevant memories for query '{query}'");
                return results;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error searching memories: {ex.Message}");
                throw;
            }
        }
    }
}
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

            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(homeDir, ".gemini-memories.db");

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

                // Generate a summary using GeminiClient.
                var summary = await SummaryFunctions.GenerateSummary(_geminiClient, content);
                if (string.IsNullOrEmpty(summary))
                {
                    summary = content.Length > 100 ? content.Substring(0, 100) + "..." : content;
                    _logger.Log("Failed to generate summary, using truncated content as summary");
                }

                // Embeddings are no longer required, so we pass an empty embedding.
                var id = _memoryStore.StoreMemory(summary, content, new float[0]);
                _logger.Log($"Stored memory in SQLite database with ID: {id}");
                return id;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error storing memory: {ex.Message}");
                throw;
            }
        }

        public List<(long id, string summary, float score, DateTime createdAt)> SearchMemorySummaries(string query, int maxResults = 5)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            // Call SearchSummaries directly, it already handles empty queries by returning recent summaries
            return _memoryStore.SearchSummaries(query, maxResults);
        }

        public List<string> FetchMemoryContent(List<long> ids)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            return _memoryStore.FetchFullContent(ids);
        }

        public static async Task CreateMemoryFromSearchResults(Logger logger, MemoryManager memoryManager, List<(string content, string url)> results)
        {
            try
            {
                var contentWithUrls = string.Join("\n\n", results.Select(p => $"Content from: {p.url}\n\n{p.content}"));
                await memoryManager.StoreMemory(contentWithUrls);
                logger.Log("Successfully created and stored memory from search results");
            }
            catch (Exception ex)
            {
                logger.Log($"Error creating memory from search results: {ex.Message}");
            }
        }
    }
}
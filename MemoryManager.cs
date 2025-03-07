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

        public async Task<long> StoreMemory(string content, string? url = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            try
            {
                if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));

                var summary = await SummaryFunctions.GenerateSummary(_geminiClient, content);
                if (string.IsNullOrEmpty(summary))
                {
                    summary = content.Length > 100 ? content.Substring(0, 100) + "..." : content;
                    _logger.Log("Failed to generate summary, using truncated content as summary");
                }

                var id = _memoryStore.StoreMemory(summary, content, url);
                _logger.Log($"Stored memory with ID: {id}, URL: {url}");
                return id;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error storing memory: {ex.Message}");
                throw;
            }
        }

        public List<(long id, string summary, float score, DateTime createdAt)> SearchMemorySummaries(string query, int maxResults = 20)
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
                foreach (var (content, url) in results)
                {
                    if (string.IsNullOrEmpty(content)) continue;
                    await memoryManager.StoreMemory(content, url);
                    logger.Log($"Stored memory from search result with URL: {url}");
                }
            }
            catch (Exception ex)
            {
                logger.Log($"Error creating memory from search results: {ex.Message}");
            }
        }
        public void DeleteMemories(List<long> ids)
        {
            if (ids == null || ids.Count == 0)
                return;
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            _memoryStore.DeleteMemories(ids);
            _logger.Log("Deleted memories with IDs: " + string.Join(", ", ids));
        }
    }
}
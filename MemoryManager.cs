using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Gemini
{
    public class MemoryManager : IDisposable
    {
        private readonly Logger _logger;
        private readonly GeminiClient _client;
        private readonly SQLiteMemoryStore _store;
        private bool _disposed;
        private const int ExpectedEmbeddingDimension = 768;
        private const int MaxContentLengthPerChunk = 32000; // Rough token limit for storage

        public MemoryManager(Logger logger, GeminiClient client)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _client = client ?? throw new ArgumentNullException(nameof(client));

            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(homeDir, ".gemini-memories.db");
            EnsureDirectoryExists(dbPath);

            _store = new SQLiteMemoryStore(dbPath, _logger);
            _logger.Log("MemoryManager initialized.");
        }

        private void EnsureDirectoryExists(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                _logger.Log($"Created directory: {dir}");
            }
        }

        public async Task<List<long>> StoreMemory(string content, string? url = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));

            try
            {
                var chunks = ChunkContent(content);
                var ids = new List<long>();

                foreach (var chunk in chunks)
                {
                    float[] embedding = await _client.Embed(chunk);
                    if (embedding.Length != ExpectedEmbeddingDimension)
                    {
                        _logger.Log($"Embedding mismatch: expected {ExpectedEmbeddingDimension}, got {embedding.Length}");
                        throw new InvalidOperationException("Invalid embedding dimension");
                    }
                    long id = _store.StoreMemory(chunk, embedding, url);
                    ids.Add(id);
                    _logger.Log($"Stored memory chunk with ID: {id}, URL: {url}");
                }

                return ids;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error storing memory: {ex.Message}");
                throw;
            }
        }

        public async Task<List<(long id, string content, float score, DateTime createdAt)>> SearchMemory(string query, int maxResults = 3)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            if (string.IsNullOrEmpty(query)) throw new ArgumentNullException(nameof(query));

            _logger.Log($"Searching memory for: '{query}'");
            return await _store.SearchMemory(query, _client, maxResults);
        }

        public void DeleteMemories(List<long> ids)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            if (ids == null || !ids.Any())
            {
                _logger.Log("No memory IDs provided for deletion");
                return;
            }

            _store.DeleteMemories(ids);
        }

        public async Task StoreSearchResults(List<(string content, string url)> results)
        {
            foreach (var (content, url) in results)
            {
                if (string.IsNullOrEmpty(content)) continue;
                try
                {
                    var ids = await StoreMemory(content, url);
                    _logger.Log($"Stored search result memory with IDs: {string.Join(", ", ids)}, URL: {url}");
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed to store search result for URL {url}: {ex.Message}");
                }
            }
        }

        private List<string> ChunkContent(string content)
        {
            if (content.Length <= MaxContentLengthPerChunk)
                return new List<string> { content };

            var chunks = new List<string>();
            int start = 0;
            while (start < content.Length)
            {
                int length = Math.Min(MaxContentLengthPerChunk, content.Length - start);
                int end = start + length;

                // Try to split at a natural boundary (e.g., period or newline)
                if (end < content.Length)
                {
                    int lastPeriod = content.LastIndexOf('.', end - 1, length);
                    int lastNewline = content.LastIndexOf('\n', end - 1, length);
                    int splitPoint = Math.Max(lastPeriod, lastNewline);
                    if (splitPoint > start) end = splitPoint + 1;
                }

                chunks.Add(content.Substring(start, end - start).Trim());
                start = end;
            }

            _logger.Log($"Chunked content into {chunks.Count} parts");
            return chunks;
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
                    _store.Dispose();
                _disposed = true;
                _logger.Log("MemoryManager disposed.");
            }
        }

        ~MemoryManager()
        {
            Dispose(false);
        }
    }
}
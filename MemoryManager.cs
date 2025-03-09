namespace Gemini
{
    public class MemoryManager : IDisposable
    {
        private readonly Logger _logger;
        private readonly GeminiClient _client;
        private readonly SQLiteMemoryStore _store;
        private bool _disposed;
        private const int ExpectedEmbeddingDimension = 768;
        private const int MaxCharsForEmbedding = 8000;

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

        public async Task<List<long>> StoreMemory(string content, string url, string searchterms)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));

            try
            {
                var chunks = ChunkContentForEmbedding(content);
                var ids = new List<long>();

                for (int i = 0; i < chunks.Count; i++)
                {
                    string currentChunk = chunks[i].ToString();
                    currentChunk = $"Content based on search terms : {searchterms} and retrieved from {url}:\n\n" + currentChunk;

                    string chunkUrl = chunks.Count > 1 && url != null ? $"{url}#chunk{i + 1}" : url ?? "";

                    var embeddingTask = _client.Embed(currentChunk);
                    var topicTask = _client.ExtractKeywords(currentChunk);
                    await Task.WhenAll(embeddingTask, topicTask);
                    float[] embedding = embeddingTask.Result;
                    
                    if (embedding.Length != ExpectedEmbeddingDimension)
                    {
                        _logger.Log($"Embedding mismatch: expected {ExpectedEmbeddingDimension}, got {embedding.Length}");
                        throw new InvalidOperationException("Invalid embedding dimension");
                    }
                    
                    string topic = topicTask.Result;

                    long id = _store.StoreMemory(currentChunk, embedding, chunkUrl, topic);
                    ids.Add(id);
                    _logger.Log($"Stored memory chunk with ID: {id}, URL: {chunkUrl}, Topic: {topic}");
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

        public async Task StoreSearchResults(List<(string content, string url, string searchterms)> results)
        {
            foreach (var (content, url, searchterms) in results)
            {
                if (string.IsNullOrEmpty(content)) continue;
                try
                {
                    var ids = await StoreMemory(content, url, searchterms);
                    _logger.Log($"Stored search result memory with IDs: {string.Join(", ", ids)}, URL: {url}");
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed to store search result for URL {url}: {ex.Message}");
                }
            }
        }

        private List<string> ChunkContentForEmbedding(string content)
        {
            if (content.Length <= MaxCharsForEmbedding)
                return new List<string> { content };

            var chunks = new List<string>();
            int start = 0;
            while (start < content.Length)
            {
                int length = Math.Min(MaxCharsForEmbedding, content.Length - start);
                int end = start + length;

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

            _logger.Log($"Chunked content for embedding into {chunks.Count} parts (max {MaxCharsForEmbedding} chars each)");
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
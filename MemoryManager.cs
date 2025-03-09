namespace Gemini
{
    public class MemoryManager : IDisposable
    {
        private readonly Logger _logger;
        private readonly GeminiClient _client;
        private readonly SQLiteMemoryStore _store;
        private bool _disposed;

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

        public async Task<List<long>> StoreMemory(string content, string url)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MemoryManager));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));

            try
            {
                var chunks = Utils.ChunkText(content, maxLength: Utils.MaxCharsForEmbedding); // Using your updated call
                var ids = new List<long>();

                for (int i = 0; i < chunks.Count; i++)
                {
                    string currentChunk = chunks[i];
                    string chunkUrl = chunks.Count > 1 && url != null ? $"{url}#chunk{i + 1}" : url ?? "";

                    float[] embedding = await _client.Embed(currentChunk);
                    if (embedding.Length != Utils.ExpectedEmbeddingDimension)
                    {
                        _logger.Log($"Embedding mismatch: expected {Utils.ExpectedEmbeddingDimension}, got {embedding.Length}");
                        throw new InvalidOperationException("Invalid embedding dimension");
                    }

                    long id = _store.StoreMemory(currentChunk, embedding, chunkUrl);
                    ids.Add(id);
                    _logger.Log($"Stored memory chunk with ID: {id}, URL: {chunkUrl}");
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
using Microsoft.Data.Sqlite;

namespace Gemini
{
    public class SQLiteMemoryStore : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _dbPath;
        private readonly Logger _logger;
        private bool _disposed;
        private const int ExpectedEmbeddingDimension = 768;
        private const float RelevanceThreshold = 0.5f;

        public SQLiteMemoryStore(string dbPath, Logger logger)
        {
            _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            InitializeDatabase();
            _logger.Log($"SQLiteMemoryStore initialized at: {_dbPath}");
        }

        private void InitializeDatabase()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_content (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL,
                    embedding BLOB NOT NULL,
                    url TEXT,
                    topic TEXT,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();
            _logger.Log("Memory content table initialized.");
        }

        public long StoreMemory(string content, float[] embedding, string? url = null, string? topic = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));
            if (embedding == null || embedding.Length != ExpectedEmbeddingDimension)
                throw new ArgumentException($"Embedding must be {ExpectedEmbeddingDimension} dimensions", nameof(embedding));

            using var transaction = _connection.BeginTransaction();
            try
            {
                if (!string.IsNullOrEmpty(url))
                {
                    long? existingId = CheckExistingUrl(url);
                    if (existingId.HasValue)
                    {
                        _logger.Log($"Memory with URL '{url}' already exists as ID: {existingId}");
                        transaction.Rollback();
                        return existingId.Value;
                    }
                }

                byte[] embeddingBytes = embedding.SelectMany(BitConverter.GetBytes).ToArray();
                long id = InsertMemory(content, embeddingBytes, url, topic);

                transaction.Commit();
                _logger.Log($"Stored memory ID: {id}, URL: {url}");
                return id;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.Log($"Error storing memory: {ex.Message}");
                throw;
            }
        }

        private long? CheckExistingUrl(string url)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id FROM memory_content WHERE url = $url LIMIT 1";
            command.Parameters.AddWithValue("$url", url);
            return command.ExecuteScalar() as long?;
        }

        private long InsertMemory(string content, byte[] embeddingBytes, string? url, string? topic)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO memory_content (content, embedding, url, topic) VALUES ($content, $embedding, $url, $topic)";
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$embedding", embeddingBytes);
            command.Parameters.AddWithValue("$url", url ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$topic", topic ?? (object)DBNull.Value);
            command.ExecuteNonQuery();

            using var idCommand = _connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid()";
            return Convert.ToInt64(idCommand.ExecuteScalar() ?? throw new InvalidOperationException("Failed to get last insert ID"));
        }

        public async Task<List<(long id, string content, float score, DateTime createdAt)>> SearchMemory(string query, GeminiClient client, int maxResults = 3)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (string.IsNullOrEmpty(query)) throw new ArgumentNullException(nameof(query));
            if (client == null) throw new ArgumentNullException(nameof(client));

            try
            {
                float[] queryEmbedding = await client.Embed(query);
                if (queryEmbedding.Length != ExpectedEmbeddingDimension)
                {
                    _logger.Log($"Query embedding mismatch: expected {ExpectedEmbeddingDimension}, got {queryEmbedding.Length}");
                    return new List<(long, string, float, DateTime)>();
                }

                var results = FetchAllMemories(queryEmbedding);
                results = results.Where(r => r.score > RelevanceThreshold).ToList(); 
                return results.OrderByDescending(r => r.score).Take(maxResults).ToList();
            }
            catch (Exception ex)
            {
                _logger.Log($"SearchMemory error: {ex.Message}");
                return new List<(long, string, float, DateTime)>();
            }
        }

        private List<(long id, string content, float score, DateTime createdAt)> FetchAllMemories(float[]? queryEmbedding)
        {
            var results = new List<(long id, string content, float score, DateTime createdAt)>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, content, embedding, created_at FROM memory_content ORDER BY created_at DESC";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string content = reader.GetString(1);

                DateTime createdAt = reader.GetDateTime(3);

                float score = 1;

                if (queryEmbedding != null)
                {
                    byte[] embeddingBytes = (byte[])reader.GetValue(2);
                    float[] embedding = new float[ExpectedEmbeddingDimension];
                    Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
                    score = CosineSimilarity(embedding, queryEmbedding);
                }

                results.Add((id, content, score, createdAt));
            }

            _logger.Log($"Fetched {results.Count} memories for scoring");
            return results;
        }

        public void DeleteMemories(List<long> ids)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (ids == null || !ids.Any())
            {
                _logger.Log("No IDs provided for deletion");
                return;
            }

            using var transaction = _connection.BeginTransaction();
            try
            {
                string placeholders = string.Join(",", ids.Select((_, i) => $"${i}"));
                using var command = _connection.CreateCommand();
                command.CommandText = $"DELETE FROM memory_content WHERE id IN ({placeholders})";
                for (int i = 0; i < ids.Count; i++)
                    command.Parameters.AddWithValue($"${i}", ids[i]);

                int affected = command.ExecuteNonQuery();
                transaction.Commit();
                _logger.Log($"Deleted {affected} memories with IDs: {string.Join(", ", ids)}");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.Log($"Delete error for IDs {string.Join(", ", ids)}: {ex.Message}");
                throw;
            }
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < ExpectedEmbeddingDimension; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            magA = (float)Math.Sqrt(magA);
            magB = (float)Math.Sqrt(magB);
            return magA * magB == 0 ? 0 : dot / (magA * magB);
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
                    _connection?.Dispose();
                _disposed = true;
                _logger.Log("SQLiteMemoryStore disposed.");
            }
        }

        ~SQLiteMemoryStore()
        {
            Dispose(false);
        }
    }
}
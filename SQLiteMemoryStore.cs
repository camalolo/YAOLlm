using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks; // Added for async

namespace Gemini
{
    public class SQLiteMemoryStore : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _dbPath;
        private readonly Logger _logger;
        private bool _disposed;
        private const int ExpectedEmbeddingDimension = 768; // Added constant for consistency

        public SQLiteMemoryStore(string dbPath, Logger logger)
        {
            _dbPath = dbPath;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_content (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL,
                    embedding BLOB NOT NULL, -- Store embeddings as binary data
                    url TEXT,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();
        }

        public long StoreMemory(string content, float[] embedding, string? url = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));
            if (embedding == null || embedding.Length == 0) throw new ArgumentNullException(nameof(embedding));

            using var transaction = _connection.BeginTransaction();
            try
            {
                // Check if URL already exists
                if (!string.IsNullOrEmpty(url))
                {
                    using var checkCmd = _connection.CreateCommand();
                    checkCmd.CommandText = "SELECT id FROM memory_content WHERE url = $url";
                    checkCmd.Parameters.AddWithValue("$url", url);
                    var existingId = checkCmd.ExecuteScalar();
                    if (existingId != null)
                    {
                        _logger.Log($"Memory with URL '{url}' already exists with ID: {existingId}, rolling back transaction");
                        transaction.Rollback();
                        return Convert.ToInt64(existingId);
                    }
                }

                // Convert embedding to byte array
                byte[] embeddingBytes = embedding.SelectMany(BitConverter.GetBytes).ToArray();

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "INSERT INTO memory_content (content, embedding, url) VALUES ($content, $embedding, $url)";
                cmd.Parameters.AddWithValue("$content", content);
                cmd.Parameters.AddWithValue("$embedding", embeddingBytes);
                cmd.Parameters.AddWithValue("$url", url != null ? (object)url : DBNull.Value);
                cmd.ExecuteNonQuery();

                using var idCmd = _connection.CreateCommand();
                idCmd.CommandText = "SELECT last_insert_rowid()";
                long id = Convert.ToInt64(idCmd.ExecuteScalar() ?? throw new InvalidOperationException("Failed to retrieve last inserted row ID"));

                transaction.Commit();
                _logger.Log($"Stored new memory with ID: {id}, URL: {url}");
                return id;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.Log($"Error storing memory in SQLite: {ex.Message}");
                throw;
            }
        }

        public async Task<List<(long id, string content, float score, DateTime createdAt)>> SearchMemory(string query, GeminiClient geminiClient, int maxResults = 3) // Made async
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (string.IsNullOrEmpty(query)) throw new ArgumentNullException(nameof(query));
            if (geminiClient == null) throw new ArgumentNullException(nameof(geminiClient));

            var results = new List<(long id, string content, float score, DateTime createdAt)>();
            try
            {
                _logger.Log($"Searching memory for query: '{query}'");

                // Generate query embedding asynchronously
                float[] queryEmbedding = await geminiClient.Embed(query); // Await instead of .Result
                if (queryEmbedding.Length != ExpectedEmbeddingDimension) // Use constant
                {
                    _logger.Log($"Query embedding dimension mismatch: expected {ExpectedEmbeddingDimension}, got {queryEmbedding.Length}");
                    return results;
                }

                // Fetch all memories
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT id, content, embedding, created_at FROM memory_content ORDER BY created_at DESC";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    string content = reader.GetString(1);
                    byte[] embeddingBytes = (byte[])reader.GetValue(2);
                    DateTime createdAt = reader.GetDateTime(3);

                    // Convert byte array back to float array
                    float[] embedding = new float[embeddingBytes.Length / sizeof(float)];
                    Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);

                    // Compute cosine similarity
                    float score = CosineSimilarity(embedding, queryEmbedding);
                    results.Add((id, content, score, createdAt));
                }

                // Sort by score and take top maxResults
                results = results.OrderByDescending(r => r.score).Take(maxResults).ToList();
                _logger.Log($"Found {results.Count} top matches for query '{query}'");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in SearchMemory: {ex.Message}");
            }
            return results;
        }

        public void DeleteMemories(List<long> ids)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (ids == null || ids.Count == 0)
            {
                _logger.Log("DeleteMemories: No IDs provided, nothing to delete.");
                return;
            }

            using var transaction = _connection.BeginTransaction();
            try
            {
                string idPlaceholder = string.Join(",", ids.Select((_, i) => $"${i}"));
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"DELETE FROM memory_content WHERE id IN ({idPlaceholder})";
                for (int i = 0; i < ids.Count; i++)
                    cmd.Parameters.AddWithValue($"${i}", ids[i]);
                int rowsAffected = cmd.ExecuteNonQuery();

                transaction.Commit();
                _logger.Log($"Deleted {rowsAffected} memories with IDs: {string.Join(", ", ids)}");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.Log($"Error deleting memories with IDs [{string.Join(", ", ids)}]: {ex.Message}");
                throw;
            }
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            int length = Math.Min(a.Length, b.Length);
            if (length == 0) return 0;

            float dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < length; i++)
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
                {
                    _connection?.Dispose();
                }
                _disposed = true;
            }
        }

        ~SQLiteMemoryStore()
        {
            Dispose(false);
        }
    }
}
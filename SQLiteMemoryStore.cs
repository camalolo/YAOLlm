using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Gemini
{
    public class SQLiteMemoryStore : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _dbPath;
        private readonly Logger _logger;
        private bool _disposed;

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
            // Create table for summaries
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_summaries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    summary TEXT NOT NULL
                )";
            command.ExecuteNonQuery();

            // Create table for full content
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_content (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL
                )";
            command.ExecuteNonQuery();

            // Create table for embeddings
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_embeddings (
                    id INTEGER PRIMARY KEY,
                    embedding BLOB NOT NULL,
                    FOREIGN KEY (id) REFERENCES memory_summaries(id)
                )";
            command.ExecuteNonQuery();

            // Create mapping table
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_mapping (
                    id INTEGER PRIMARY KEY
                )";
            command.ExecuteNonQuery();

            // Create FTS table
            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_summaries_fts USING fts5(
                    summary,
                    tokenize = 'unicode61'
                )";
            command.ExecuteNonQuery();

            // Populate FTS table with existing summaries
            command.CommandText = @"
                INSERT OR IGNORE INTO memory_summaries_fts (rowid, summary)
                SELECT id, summary FROM memory_summaries";
            command.ExecuteNonQuery();
        }

        public long StoreMemory(string summary, string content, float[] embedding)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (string.IsNullOrEmpty(summary)) throw new ArgumentNullException(nameof(summary));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));
            if (embedding == null || embedding.Length == 0) throw new ArgumentNullException(nameof(embedding));

            using var transaction = _connection.BeginTransaction();
            try
            {
                // Insert into memory_summaries
                using var summaryCmd = _connection.CreateCommand();
                summaryCmd.CommandText = "INSERT INTO memory_summaries (summary) VALUES ($summary)";
                summaryCmd.Parameters.AddWithValue("$summary", summary);
                summaryCmd.ExecuteNonQuery();

                // Get the last inserted ID
                using var idCmd = _connection.CreateCommand();
                idCmd.CommandText = "SELECT last_insert_rowid()";
                long id = Convert.ToInt64(idCmd.ExecuteScalar() ?? throw new InvalidOperationException("Failed to retrieve last inserted row ID"));

                // Insert into memory_content
                using var contentCmd = _connection.CreateCommand();
                contentCmd.CommandText = "INSERT INTO memory_content (id, content) VALUES ($id, $content)";
                contentCmd.Parameters.AddWithValue("$id", id);
                contentCmd.Parameters.AddWithValue("$content", content);
                contentCmd.ExecuteNonQuery();

                // Insert into memory_embeddings
                using var embeddingCmd = _connection.CreateCommand();
                embeddingCmd.CommandText = "INSERT INTO memory_embeddings (id, embedding) VALUES ($id, $embedding)";
                embeddingCmd.Parameters.AddWithValue("$id", id);
                embeddingCmd.Parameters.AddWithValue("$embedding", embedding.SelectMany(BitConverter.GetBytes).ToArray());
                embeddingCmd.ExecuteNonQuery();

                // Insert into memory_mapping
                using var mappingCmd = _connection.CreateCommand();
                mappingCmd.CommandText = "INSERT INTO memory_mapping (id) VALUES ($id)";
                mappingCmd.Parameters.AddWithValue("$id", id);
                mappingCmd.ExecuteNonQuery();

                // Update FTS table
                using var ftsCmd = _connection.CreateCommand();
                ftsCmd.CommandText = "INSERT INTO memory_summaries_fts (rowid, summary) VALUES ($id, $summary)";
                ftsCmd.Parameters.AddWithValue("$id", id);
                ftsCmd.Parameters.AddWithValue("$summary", summary);
                ftsCmd.ExecuteNonQuery();

                transaction.Commit();
                return id;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.Log($"Error storing memory in SQLite: {ex.Message}");
                throw;
            }
        }

        public List<(string content, float[] embedding)> GetAllMemoriesWithEmbeddings()
        {
            var results = new List<(string content, float[] embedding)>();
            using var command = _connection.CreateCommand();
            try
            {
                command.CommandText = @"
                    SELECT mc.content, me.embedding
                    FROM memory_content mc
                    JOIN memory_embeddings me ON mc.id = me.id";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var content = reader.GetString(0);
                    var embeddingBlob = (byte[])reader.GetValue(1);
                    
                    var embedding = new float[embeddingBlob.Length / sizeof(float)];
                    Buffer.BlockCopy(embeddingBlob, 0, embedding, 0, embeddingBlob.Length);
                    
                    results.Add((content, embedding));
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error retrieving memories with embeddings: {ex.Message}");
                throw; // Rethrow to let caller handle the error
            }
            return results;
        }

        public List<(long id, string summary, float score)> SearchSummaries(string query, int maxResults = 5)
        {
            var results = new List<(long id, string summary, float score)>();
            try
            {
                // Log the query and maxResults for debugging
                _logger.Log($"SearchSummaries: Executing query '{query}' with maxResults={maxResults}");

                // Search the summaries using the FTS5 table
                using var command = _connection.CreateCommand();
                // Escape double quotes in the query and enclose it in double quotes
                string escapedQuery = query.Replace("\"", "\"\"");
                command.CommandText = @"
                    SELECT ms.id, ms.summary, msfts.rank
                    FROM memory_summaries ms
                    JOIN memory_summaries_fts msfts ON ms.id = msfts.rowid
                    WHERE msfts.summary MATCH $query
                    ORDER BY msfts.rank LIMIT $maxResults";
                command.Parameters.AddWithValue("$query", "\"" + escapedQuery + "\"");
                command.Parameters.AddWithValue("$maxResults", maxResults);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add((
                        reader.GetInt64(0),
                        reader.GetString(1),
                        (float)reader.GetDouble(2) // Rank is negative in FTS5 (BM25), so you might want to normalize it
                    ));
                }

                _logger.Log($"SearchSummaries: Found {results.Count} summaries for query '{query}'");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in SearchSummaries: {ex.Message}");
            }
            return results;
        }

        public List<(string content, float score)> SearchFullContent(string query, List<long> ids, int maxResults = 3)
        {
            var results = new List<(string content, float score)>();
            if (ids.Count == 0)
            {
                _logger.Log("SearchFullContent: No IDs provided, returning empty results");
                return results;
            }

            try
            {
                // Log the query and IDs for debugging
                _logger.Log($"SearchFullContent: Executing query '{query}' for IDs [{string.Join(", ", ids)}] with maxResults={maxResults}");

                using var command = _connection.CreateCommand();
                var idList = string.Join(",", ids);

                // If query is empty, return all contents for the given IDs
                if (string.IsNullOrWhiteSpace(query))
                {
                    command.CommandText = @"
                        SELECT content, 0.0 as rank
                        FROM memory_content 
                        WHERE rowid IN (" + idList + @")
                        LIMIT $maxResults";
                }
                else
                {
                    // Otherwise do normal FTS search
                    string escapedQuery = query.Replace("\"", "\"\"");
                    command.CommandText = @"
                        SELECT content, rank
                        FROM memory_content
                        WHERE rowid IN (" + idList + @")
                        AND content MATCH $query
                        ORDER BY rank LIMIT $maxResults";
                    command.Parameters.AddWithValue("$query", "\"" + escapedQuery + "\"");
                }
                
                command.Parameters.AddWithValue("$maxResults", maxResults);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add((
                        reader.GetString(0),
                        (float)reader.GetDouble(1)
                    ));
                }

                _logger.Log($"SearchFullContent: Found {results.Count} full content matches for query '{query}'");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in SearchFullContent: {ex.Message}");
            }
            return results;
        }

        public List<(long id, string content)> GetAllMemories()
        {
            var results = new List<(long id, string content)>();
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = @"
                    SELECT mc.rowid, mc.content
                    FROM memory_content mc";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add((
                        reader.GetInt64(0),
                        reader.GetString(1)
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error retrieving all memories: {ex.Message}");
            }
            return results;
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
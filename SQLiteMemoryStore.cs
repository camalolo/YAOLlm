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

            // Removed: Creation of memory_embeddings and memory_mapping tables

            // Create FTS table for summaries
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

            // Create FTS table for content
            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_content_fts USING fts5(
                    content,
                    tokenize = 'unicode61'
                )";
            command.ExecuteNonQuery();

            // Initialize content FTS with existing data
            command.CommandText = @"
                INSERT OR IGNORE INTO memory_content_fts (rowid, content)
                SELECT id, content FROM memory_content";
            command.ExecuteNonQuery();
        }

        public long StoreMemory(string summary, string content, float[] embedding)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (string.IsNullOrEmpty(summary)) throw new ArgumentNullException(nameof(summary));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));
            // The embedding parameter is no longer used.

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

                // Insert into memory_content using the same ID
                using var contentCmd = _connection.CreateCommand();
                contentCmd.CommandText = "INSERT INTO memory_content (id, content) VALUES ($id, $content)";
                contentCmd.Parameters.AddWithValue("$id", id);
                contentCmd.Parameters.AddWithValue("$content", content);
                contentCmd.ExecuteNonQuery();

                // Update FTS table for summaries
                using var ftsCmd = _connection.CreateCommand();
                ftsCmd.CommandText = "INSERT INTO memory_summaries_fts (rowid, summary) VALUES ($id, $summary)";
                ftsCmd.Parameters.AddWithValue("$id", id);
                ftsCmd.Parameters.AddWithValue("$summary", summary);
                ftsCmd.ExecuteNonQuery();

                // Update FTS table for content
                using var ftsContentCmd = _connection.CreateCommand();
                ftsContentCmd.CommandText = "INSERT INTO memory_content_fts (rowid, content) VALUES ($id, $content)";
                ftsContentCmd.Parameters.AddWithValue("$id", id);
                ftsContentCmd.Parameters.AddWithValue("$content", content);
                ftsContentCmd.ExecuteNonQuery();

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

        // This function is kept for compatibility but now simply returns an empty list.
        public List<(string content, float[] embedding)> GetAllMemoriesWithEmbeddings()
        {
            return new List<(string content, float[] embedding)>();
        }

        public List<(long id, string summary, float score)> SearchSummaries(string query, int maxResults = 5)
        {
            var results = new List<(long id, string summary, float score)>();
            try
            {
                _logger.Log($"SearchSummaries: Executing query '{query}' with maxResults={maxResults}");

                // Search the summaries using the FTS5 table with BM25 ranking.
                using var command = _connection.CreateCommand();
                // Escape double quotes in the query and enclose it in double quotes.
                string escapedQuery = query.Replace("\"", "\"\"");
                command.CommandText = @"
                    SELECT ms.id, ms.summary, bm25(memory_summaries_fts) as rank
                    FROM memory_summaries ms
                    JOIN memory_summaries_fts msfts ON ms.id = msfts.rowid
                    WHERE msfts.summary MATCH $query
                    ORDER BY rank LIMIT $maxResults";
                command.Parameters.AddWithValue("$query", "\"" + escapedQuery + "\"");
                command.Parameters.AddWithValue("$maxResults", maxResults);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add((
                        reader.GetInt64(0),
                        reader.GetString(1), 
                        (float)reader.GetDouble(2)
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
                _logger.Log($"SearchFullContent: Executing query '{query}' for IDs [{string.Join(", ", ids)}] with maxResults={maxResults}");

                using var command = _connection.CreateCommand();
                var idList = string.Join(",", ids);

                // If query is empty, return all contents for the given IDs.
                if (string.IsNullOrWhiteSpace(query))
                {
                    command.CommandText = @"
                        SELECT content, 0.0 as rank
                        FROM memory_content mc
                        WHERE mc.rowid IN (" + idList + @")
                        LIMIT $maxResults";
                }
                else
                {
                    // Otherwise, perform an FTS search on the content.
                    string escapedQuery = query.Replace("\"", "\"\"");
                    // Note: Use the FTS table for content ranking.
                    command.CommandText = @"
                        SELECT mc.content, bm25(memory_content_fts) as rank
                        FROM memory_content mc
                        JOIN memory_content_fts mcfts ON mc.id = mcfts.rowid
                        WHERE mc.rowid IN (" + idList + @")
                        AND mcfts.content MATCH $query
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
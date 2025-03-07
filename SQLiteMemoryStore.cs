using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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

            // Updated table with url column
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_summaries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    summary TEXT NOT NULL,
                    url TEXT,
                    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
                )";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_summaries_fts USING fts5(
                    summary,
                    tokenize = 'unicode61'
                )";
            command.ExecuteNonQuery();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_content (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL
                )";
            command.ExecuteNonQuery();

        }

        public long StoreMemory(string summary, string content, string? url = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteMemoryStore));
            if (string.IsNullOrEmpty(summary)) throw new ArgumentNullException(nameof(summary));
            if (string.IsNullOrEmpty(content)) throw new ArgumentNullException(nameof(content));

            using var transaction = _connection.BeginTransaction();
            try
            {
                // Check if URL already exists
                if (!string.IsNullOrEmpty(url))
                {
                    using var checkCmd = _connection.CreateCommand();
                    checkCmd.CommandText = "SELECT id FROM memory_summaries WHERE url = $url";
                    checkCmd.Parameters.AddWithValue("$url", url);
                    var existingId = checkCmd.ExecuteScalar();
                    if (existingId != null)
                    {
                        _logger.Log($"Memory with URL '{url}' already exists with ID: {existingId}");
                        transaction.Rollback();
                        return Convert.ToInt64(existingId); // Return existing ID
                    }
                }

                // Insert into memory_summaries with URL
                using var summaryCmd = _connection.CreateCommand();
                summaryCmd.CommandText = "INSERT INTO memory_summaries (summary, url) VALUES ($summary, $url)";
                summaryCmd.Parameters.AddWithValue("$summary", summary);
                summaryCmd.Parameters.AddWithValue("$url", url != null ? (object)url : DBNull.Value);
                summaryCmd.ExecuteNonQuery();

                using var idCmd = _connection.CreateCommand();
                idCmd.CommandText = "SELECT last_insert_rowid()";
                long id = Convert.ToInt64(idCmd.ExecuteScalar() ?? throw new InvalidOperationException("Failed to retrieve last inserted row ID"));

                using var contentCmd = _connection.CreateCommand();
                contentCmd.CommandText = "INSERT INTO memory_content (id, content) VALUES ($id, $content)";
                contentCmd.Parameters.AddWithValue("$id", id);
                contentCmd.Parameters.AddWithValue("$content", content);
                contentCmd.ExecuteNonQuery();

                using var ftsCmd = _connection.CreateCommand();
                ftsCmd.CommandText = "INSERT INTO memory_summaries_fts (rowid, summary) VALUES ($id, $summary)";
                ftsCmd.Parameters.AddWithValue("$id", id);
                ftsCmd.Parameters.AddWithValue("$summary", summary);
                ftsCmd.ExecuteNonQuery();

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

        public List<(long id, string summary, float score, DateTime createdAt)> SearchSummaries(string query, int maxResults = 20)
        {
            var results = new List<(long id, string summary, float score, DateTime createdAt)>();

            try
            {
                _logger.Log($"SearchSummaries: Executing query '{query}' with maxResults={maxResults}");

                if (string.IsNullOrWhiteSpace(query))
                {
                    _logger.Log("SearchSummaries: Query is empty, returning most recent summaries");
                    using var emptyQueryCommand = _connection.CreateCommand();
                    emptyQueryCommand.CommandText = @"
                SELECT id, summary, 1.0 AS rank, created_at
                FROM memory_summaries
                ORDER BY created_at DESC
                LIMIT $maxResults";
                    emptyQueryCommand.Parameters.AddWithValue("$maxResults", maxResults);

                    using var emptyQueryReader = emptyQueryCommand.ExecuteReader();
                    while (emptyQueryReader.Read())
                    {
                        results.Add((
                            emptyQueryReader.GetInt64(0),
                            emptyQueryReader.GetString(1),
                            1.0f,
                            emptyQueryReader.GetDateTime(3)
                        ));
                    }
                    _logger.Log($"SearchSummaries: Returning {results.Count} most recent summaries");
                    return results;
                }

                string sanitizedQuery = Regex.Replace(query.ToLower(), @"[:\?\!]", " ");
                string[] queryWords = sanitizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < queryWords.Length; i++)
                {
                    queryWords[i] = queryWords[i].Replace("?", "");
                }
                string matchQuery = string.Join(" OR ", queryWords);

                using var searchCommand = _connection.CreateCommand();
                searchCommand.CommandText = @"
            SELECT ms.id, ms.summary, bm25(memory_summaries_fts) AS rank, ms.created_at
            FROM memory_summaries ms
            JOIN memory_summaries_fts msfts ON ms.id = msfts.rowid
            WHERE msfts.summary MATCH $matchQuery
            ORDER BY rank ASC, created_at DESC
            LIMIT $maxResults";
                searchCommand.Parameters.AddWithValue("$matchQuery", matchQuery);
                searchCommand.Parameters.AddWithValue("$maxResults", maxResults);

                using var searchReader = searchCommand.ExecuteReader();
                while (searchReader.Read())
                {
                    float bm25Score = (float)searchReader.GetDouble(2);
                    float normalizedScore = -bm25Score / 10.0f;
                    results.Add((
                        searchReader.GetInt64(0),
                        searchReader.GetString(1),
                        normalizedScore > 1.0f ? 1.0f : normalizedScore,
                        searchReader.GetDateTime(3)
                    ));
                }

                if (results.Count < maxResults)
                {
                    _logger.Log($"SearchSummaries: Found {results.Count} matches, filling with recent summaries");
                    int remaining = maxResults - results.Count;
                    var existingIds = results.Select(r => r.id).ToList();

                    using var fallbackCommand = _connection.CreateCommand();
                    fallbackCommand.CommandText = @"
                SELECT id, summary, 0.0 AS rank, created_at
                FROM memory_summaries
                WHERE id NOT IN (" + (existingIds.Any() ? string.Join(",", existingIds) : "0") + @")
                ORDER BY created_at DESC
                LIMIT $remaining";
                    fallbackCommand.Parameters.AddWithValue("$remaining", remaining);

                    using var fallbackReader = fallbackCommand.ExecuteReader();
                    while (fallbackReader.Read())
                    {
                        results.Add((
                            fallbackReader.GetInt64(0),
                            fallbackReader.GetString(1),
                            0.0f,
                            fallbackReader.GetDateTime(3)
                        ));
                    }
                }

                _logger.Log($"SearchSummaries: Returning {results.Count} summaries for query '{query}'");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in SearchSummaries: {ex.Message}");
            }

            return results;
        }

        public List<string> FetchFullContent(List<long> ids)
        {
            var results = new List<string>();
            if (ids.Count == 0)
            {
                _logger.Log("FetchFullContent: No IDs provided, returning empty results");
                return results;
            }

            try
            {
                _logger.Log($"FetchFullContent: Fetching for IDs [{string.Join(", ", ids)}]");

                using var command = _connection.CreateCommand();
                var idList = string.Join(",", ids);

                // Note: Use the FTS table for content ranking.
                command.CommandText = @"
                    SELECT mc.content
                    FROM memory_content mc
                    WHERE mc.rowid IN (" + idList + @");";


                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(reader.GetString(0));
                }

                _logger.Log($"FetchFullContent: Found {results.Count} full content matches");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error in FetchFullContent: {ex.Message}");
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
                // Prepare a parameterized IN clause for the IDs
                string idPlaceholder = string.Join(",", ids.Select((_, i) => $"${i}"));
                int rowsAffected = 0;

                // Delete from memory_summaries
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = $"DELETE FROM memory_summaries WHERE id IN ({idPlaceholder})";
                    for (int i = 0; i < ids.Count; i++)
                        cmd.Parameters.AddWithValue($"${i}", ids[i]);
                    rowsAffected += cmd.ExecuteNonQuery();
                }

                // Delete from memory_content
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = $"DELETE FROM memory_content WHERE id IN ({idPlaceholder})";
                    for (int i = 0; i < ids.Count; i++)
                        cmd.Parameters.AddWithValue($"${i}", ids[i]);
                    rowsAffected += cmd.ExecuteNonQuery();
                }

                // Delete from memory_summaries_fts
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = $"DELETE FROM memory_summaries_fts WHERE rowid IN ({idPlaceholder})";
                    for (int i = 0; i < ids.Count; i++)
                        cmd.Parameters.AddWithValue($"${i}", ids[i]);
                    rowsAffected += cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                _logger.Log($"Deleted {rowsAffected / 4} memories with IDs: {string.Join(", ", ids)} (total rows affected: {rowsAffected})");
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.Log($"Error deleting memories with IDs [{string.Join(", ", ids)}]: {ex.Message}");
                throw; // Rethrow to let the caller handle it
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
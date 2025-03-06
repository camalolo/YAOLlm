using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;

namespace Gemini
{
    public class SQLiteMemoryStore : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _dbPath;
        private readonly Logger _logger;

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
            // Create FTS5 table for summaries
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_summaries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    summary TEXT NOT NULL
                )";
            command.ExecuteNonQuery();

            // Create FTS5 table for full content
            command.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS memory_content USING fts5(
                    content,
                    tokenize = 'unicode61'
                )";
            command.ExecuteNonQuery();

            // Create a mapping table (optional now, can be removed if not needed)
            // For now, we'll keep it but remove file_path
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS memory_mapping (
                    id INTEGER PRIMARY KEY
                )";
            command.ExecuteNonQuery();
        }

        public long StoreMemory(string summary, string content)
        {
            try
            {
                // Insert into memory_summaries
                using var summaryCmd = _connection.CreateCommand();
                summaryCmd.CommandText = "INSERT INTO memory_summaries (summary) VALUES ($summary)";
                summaryCmd.Parameters.AddWithValue("$summary", summary);
                summaryCmd.ExecuteNonQuery();

                // Retrieve the last inserted ID
                using var idCmd = _connection.CreateCommand();
                idCmd.CommandText = "SELECT last_insert_rowid()";
                object? result = idCmd.ExecuteScalar();
                long id = result != null ? Convert.ToInt64(result) : throw new InvalidOperationException("Failed to retrieve last inserted row ID");

                // Insert into memory_content
                using var contentCmd = _connection.CreateCommand();
                contentCmd.CommandText = "INSERT INTO memory_content (rowid, content) VALUES ($id, $content)";
                contentCmd.Parameters.AddWithValue("$id", id);
                contentCmd.Parameters.AddWithValue("$content", content);
                contentCmd.ExecuteNonQuery();

                // Insert into memory_mapping (just the ID, no file_path)
                using var mappingCmd = _connection.CreateCommand();
                mappingCmd.CommandText = "INSERT INTO memory_mapping (id) VALUES ($id)";
                mappingCmd.Parameters.AddWithValue("$id", id);
                mappingCmd.ExecuteNonQuery();

                return id;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error storing memory in SQLite: {ex.Message}");
                throw;
            }
        }

        public List<(long id, string summary, float score)> SearchSummaries(string query, int maxResults = 5)
        {
            var results = new List<(long id, string summary, float score)>();
            try
            {
                // Create a virtual FTS5 table for summaries if not exists
                using var createFtsCmd = _connection.CreateCommand();
                createFtsCmd.CommandText = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS memory_summaries_fts USING fts5(
                        summary,
                        tokenize = 'unicode61'
                    )";
                createFtsCmd.ExecuteNonQuery();

                // Populate the FTS5 table if not already populated
                using var populateFtsCmd = _connection.CreateCommand();
                populateFtsCmd.CommandText = @"
                    INSERT OR IGNORE INTO memory_summaries_fts (rowid, summary)
                    SELECT id, summary FROM memory_summaries";
                populateFtsCmd.ExecuteNonQuery();

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
                string escapedQuery = query.Replace("\"", "\"\"");
                command.CommandText = @"
                    SELECT mc.content, mc.rank
                    FROM memory_content mc
                    WHERE mc.rowid IN (" + idList + @")
                    AND mc.content MATCH $query
                    ORDER BY mc.rank LIMIT $maxResults";
                command.Parameters.AddWithValue("$query", "\"" + escapedQuery + "\"");
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

        public void Dispose()
        {
            _connection.Close();
            _connection.Dispose();
        }
    }
}
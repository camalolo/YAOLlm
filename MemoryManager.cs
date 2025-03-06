using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Gemini
{
    public class MemoryManager
    {
        private readonly Logger _logger;
        private readonly SQLiteMemoryStore _memoryStore;
        private readonly GeminiClient _geminiClient;

        public MemoryManager(Logger logger, GeminiClient geminiClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _geminiClient = geminiClient ?? throw new ArgumentNullException(nameof(geminiClient));

            // Define the path for the SQLite database
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dbPath = Path.Combine(homeDir, ".gemini-memories.db");

            // Ensure the directory exists for the SQLite database
            var directoryName = Path.GetDirectoryName(dbPath);
            if (directoryName != null)
            {
                Directory.CreateDirectory(directoryName);
            }

            _memoryStore = new SQLiteMemoryStore(dbPath, _logger);
            // No need to load existing memories from files anymore
        }

        // Remove LoadExistingMemories since we don't need to load from text files
        private void LoadExistingMemories()
        {
            // No-op: All existing memories are already in SQLite
            // If you need to initialize anything, you can do it here
            _logger.Log("No existing memory files to load; using SQLite database.");
        }

        public async Task StoreMemory(string content)
        {
            try
            {
                // Generate a summary using the LLM
                var summary = await _geminiClient.GenerateSummary(content);
                if (string.IsNullOrEmpty(summary))
                {
                    summary = content.Length > 100 ? content.Substring(0, 100) + "..." : content;
                    _logger.Log("Failed to generate summary, using truncated content as summary");
                }

                // Store directly in SQLite without creating a text file
                _memoryStore.StoreMemory(summary, content);
                _logger.Log("Stored memory in SQLite database");
            }
            catch (Exception ex)
            {
                _logger.Log($"Error storing memory: {ex.Message}");
            }
        }

        public List<(string content, float score)> SearchMemories(string query, int maxResults = 3)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    _logger.Log("SearchMemories: Query is empty");
                    return new List<(string, float)>();
                }

                // Stage 1: Search summaries
                var summaryResults = _memoryStore.SearchSummaries(query, maxResults: 5);
                if (!summaryResults.Any())
                {
                    _logger.Log($"No relevant summaries found for query '{query}'");
                    return new List<(string, float)>();
                }

                // Stage 2: Search full content of promising memories
                var promisingIds = summaryResults.Select(r => r.id).ToList();
                var fullResults = _memoryStore.SearchFullContent(query, promisingIds, maxResults);

                // Keep all results, sort by BM25 score (lower is better), and take the top maxResults
                var results = fullResults
                    .Select(r => (content: r.content, score: -r.score)) // Convert to positive score for consistency
                    .OrderByDescending(r => r.score) // Higher positive score (lower BM25) means better match
                    .Take(maxResults)
                    .ToList();

                _logger.Log($"SearchMemories: Found {results.Count} relevant memories for query '{query}'");
                return results;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error searching memories: {ex.Message}");
                return new List<(string, float)>();
            }
        }
    }
}
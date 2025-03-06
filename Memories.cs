using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Gemini
{
    public class MemoryManager
    {
        private readonly string _memoriesDir;
        private readonly Logger _logger;
        private readonly Dictionary<string, (string content, float[] embedding)> _memoryCache;

        public MemoryManager(Logger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _memoriesDir = Path.Combine(homeDir, ".gemini.memories");
            Directory.CreateDirectory(_memoriesDir);
            _memoryCache = new Dictionary<string, (string, float[])>();
            LoadMemories();
        }

        private void LoadMemories()
        {
            try
            {
                var memoryFiles = Directory.GetFiles(_memoriesDir, "memory_*.txt");
                foreach (var file in memoryFiles)
                {
                    var content = File.ReadAllText(file);
                    var embeddingFile = Path.ChangeExtension(file, ".embedding.json");
                    float[] embedding;

                    if (File.Exists(embeddingFile))
                    {
                        var json = File.ReadAllText(embeddingFile);
                        embedding = JsonSerializer.Deserialize<float[]>(json) ?? Array.Empty<float>();
                    }
                    else
                    {
                        embedding = Embeddings.ComputeEmbedding(content);
                        File.WriteAllText(embeddingFile, JsonSerializer.Serialize(embedding));
                    }

                    _memoryCache[file] = (content, embedding);
                    _logger.Log($"Loaded memory: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading memories: {ex.Message}");
            }
        }

        public void StoreMemory(string content)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var filePath = Path.Combine(_memoriesDir, $"memory_{timestamp}.txt");
                File.WriteAllText(filePath, content);

                var embedding = Embeddings.ComputeEmbedding(content);
                var embeddingFile = Path.ChangeExtension(filePath, ".embedding.json");
                File.WriteAllText(embeddingFile, JsonSerializer.Serialize(embedding));

                _memoryCache[filePath] = (content, embedding);
                _logger.Log($"Stored memory: {Path.GetFileName(filePath)}");
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

                var queryEmbedding = Embeddings.ComputeEmbedding(query);
                if (queryEmbedding.Length == 0)
                {
                    _logger.Log("SearchMemories: Failed to compute query embedding");
                    return new List<(string, float)>();
                }

                var results = _memoryCache
                    .Select(kv => (content: kv.Value.content, score: Embeddings.CosineSimilarity(kv.Value.embedding, queryEmbedding)))
                    .Where(x => x.score > 0.1f) // Minimum relevance threshold
                    .OrderByDescending(x => x.score)
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
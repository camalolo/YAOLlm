using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RestSharp;
using HtmlAgilityPack;
using System.Collections.Concurrent;

namespace Gemini
{
    public class Search
    {
        private readonly Logger _logger;
        private readonly string _googleSearchApiKey;
        private readonly string _googleSearchEngineId;
        private const int GoogleSearchNumItems = 10;
        private const double RelevanceThreshold = 0.45;
        private readonly Dictionary<string, List<string>> _searchResultsCache = new();
        private readonly Dictionary<string, int> _searchStartIndices = new();
        private readonly Dictionary<string, int> _searchResultsIndex = new();
        private readonly Dictionary<string, (string, float[])> _scrapedContentCache = new();
        private readonly ConcurrentDictionary<string, int> _vocabulary = new();
        private readonly object _vocabularyLock = new();

        public Search(Logger logger, string googleSearchApiKey, string googleSearchEngineId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _googleSearchApiKey = googleSearchApiKey ?? throw new ArgumentNullException(nameof(googleSearchApiKey));
            _googleSearchEngineId = googleSearchEngineId ?? throw new ArgumentNullException(nameof(googleSearchEngineId));
        }

        public async Task<List<(string content, string url)>> PerformSearch(string searchTerms, string originalUserQuery, Action<string, string> updateChat, Action<Status> updateStatus)
        {
            if (string.IsNullOrWhiteSpace(searchTerms))
            {
                _logger.Log("Error: Search terms are empty");
                updateChat($"System: Cannot perform search with empty search terms.\n", "system");
                return new List<(string, string)>();
            }

            updateChat($"System: Searching for '{searchTerms}'...\n", "system");
            updateStatus(Status.Searching);

            List<string> urls = await GetSearchResults(searchTerms);
            if (!urls.Any())
            {
                _logger.Log($"No URLs found for search terms: {searchTerms}");
                updateChat($"System: No results found for '{searchTerms}'.\n", "system");
                return new List<(string, string)>();
            }

            updateStatus(Status.Scraping);
            var scrapedData = await ScrapeUrls(urls);

            updateStatus(Status.Processing);
            var results = ProcessScrapedData(scrapedData, searchTerms, urls);

            if (!results.Any())
            {
                updateChat($"System: No relevant results found for '{searchTerms}'.\n", "system");
            }

            return results;
        }

        private async Task<List<string>> GetSearchResults(string searchTerms)
        {
            if (_searchResultsCache.ContainsKey(searchTerms))
            {
                var cachedUrls = _searchResultsCache[searchTerms];
                int startIndex = _searchResultsIndex.GetValueOrDefault(searchTerms, 0);
                var urls = cachedUrls.Skip(startIndex).Take(3).ToList();
                _searchResultsIndex[searchTerms] = startIndex + 3;

                if (!urls.Any())
                {
                    urls = await SearchGoogle(searchTerms);
                    _searchResultsIndex[searchTerms] = 0;
                }
                _logger.Log($"Using cached search results for '{searchTerms}', URLs: {string.Join(", ", urls)}");
                return urls;
            }

            var newUrls = await SearchGoogle(searchTerms);
            _searchResultsIndex[searchTerms] = 0;
            _logger.Log($"New search results for '{searchTerms}', URLs: {string.Join(", ", newUrls)}");
            return newUrls;
        }

        private async Task<List<string>> SearchGoogle(string searchTerms)
        {
            try
            {
                _logger.Log($"Search request: {searchTerms}");
                if (!_searchStartIndices.ContainsKey(searchTerms))
                    _searchStartIndices[searchTerms] = 1;

                var client = new RestClient("https://www.googleapis.com/customsearch/v1");
                var request = new RestRequest { Method = Method.Get };
                request.AddParameter("key", _googleSearchApiKey);
                request.AddParameter("cx", _googleSearchEngineId);
                request.AddParameter("q", searchTerms);
                request.AddParameter("num", GoogleSearchNumItems);
                request.AddParameter("start", _searchStartIndices[searchTerms]);

                var response = await client.ExecuteAsync(request);
                response.ThrowIfError();

                var data = response.Content != null ? JsonSerializer.Deserialize<JsonElement>(response.Content) : default;
                if (data.TryGetProperty("items", out var items))
                {
                    _searchStartIndices[searchTerms] += GoogleSearchNumItems;
                    var urls = items.EnumerateArray()
                                    .Select(item => item.GetProperty("link").GetString() ?? string.Empty)
                                    .Where(url => !string.IsNullOrEmpty(url))
                                    .ToList();
                    _searchResultsCache[searchTerms] = urls;
                    _logger.Log($"Google search returned {urls.Count} URLs");
                    return urls;
                }
                _logger.Log("Google search returned no items");
                return new List<string>();
            }
            catch (Exception ex)
            {
                _logger.Log($"Google Search error: {ex.Message}");
                return new List<string>();
            }
        }

        private async Task<(string, float[])[]> ScrapeUrls(List<string> urls)
        {
            return await Task.WhenAll(urls.Select(async url =>
            {
                if (_scrapedContentCache.TryGetValue(url, out var cached))
                {
                    _logger.Log($"Using cached content for: {url}");
                    return cached;
                }

                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.36");
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var html = await client.GetStringAsync(url);
                    var doc = new HtmlAgilityPack.HtmlDocument();
                    doc.LoadHtml(html);

                    // Remove unwanted elements: scripts, styles, navigation, and common web artifacts
                    foreach (var node in doc.DocumentNode.SelectNodes("//script | //style | //nav | //footer | //*[contains(@class, 'advert')] | //*[contains(@class, 'popup')]") ?? new HtmlNodeCollection(null))
                        node.Remove();

                    // Extract main content (heuristic: prioritize article, main, or body tags)
                    var contentNode = doc.DocumentNode.SelectSingleNode("//article") ??
                                      doc.DocumentNode.SelectSingleNode("//main") ??
                                      doc.DocumentNode.SelectSingleNode("//body");
                    var text = contentNode != null ? HtmlEntity.DeEntitize(contentNode.InnerText) : doc.DocumentNode.InnerText;

                    // Basic cleanup: remove excessive whitespace, common error messages, and boilerplate
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"(Something went wrong|Try again|Please enable Javascript|Subscribe to our newsletter)", "", RegexOptions.IgnoreCase);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _logger.Log($"No meaningful content scraped from {url}");
                        return (string.Empty, new float[0]);
                    }

                    var embedding = ComputeEmbedding(text);
                    _scrapedContentCache[url] = (text, embedding);
                    _logger.Log($"Scraped content from {url}, length: {text.Length} chars");
                    return (text, embedding);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error scraping {url}: {ex.Message}");
                    return (string.Empty, new float[0]);
                }
            }));
        }

        private float[] ComputeEmbedding(string text)
        {
            try
            {
                // Improved text preprocessing
                var words = text.ToLower()
                    .Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\' }, 
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2 && !IsStopWord(w))
                    .ToList();

                // Update vocabulary
                lock (_vocabularyLock)
                {
                    foreach (var word in words.Distinct())
                    {
                        if (!_vocabulary.ContainsKey(word))
                        {
                            _vocabulary.TryAdd(word, _vocabulary.Count);
                        }
                    }
                }

                // Compute TF-IDF vector with improved weighting
                var vector = new float[_vocabulary.Count];
                var wordCounts = words.GroupBy(w => w)
                    .ToDictionary(g => g.Key, g => g.Count());

                foreach (var (word, count) in wordCounts)
                {
                    if (_vocabulary.TryGetValue(word, out int index))
                    {
                        // TF = logarithmically scaled term frequency
                        float tf = 1 + (float)Math.Log(count);
                        vector[index] = tf;
                    }
                }

                // L2 normalization
                float magnitude = (float)Math.Sqrt(vector.Sum(x => x * x));
                if (magnitude > 0)
                {
                    for (int i = 0; i < vector.Length; i++)
                    {
                        vector[i] /= magnitude;
                    }
                }

                return vector;
            }
            catch (Exception ex)
            {
                _logger.Log($"Error computing embeddings: {ex.Message}");
                return Array.Empty<float>();
            }
        }

        private bool IsStopWord(string word)
        {
            // Common English stop words that don't contribute to meaning
            var stopWords = new HashSet<string>
            {
                "the", "be", "to", "of", "and", "a", "in", "that", "have",
                "for", "not", "on", "with", "he", "as", "you", "do", "at",
                "this", "but", "his", "by", "from", "they", "we", "say", "her",
                "she", "or", "an", "will", "my", "one", "all", "would", "there",
                "their", "what", "so", "up", "out", "if", "about", "who", "get",
                "which", "go", "me", "when", "make", "can", "like", "time", "no",
                "just", "him", "know", "take", "into", "your", "some", "could",
                "them", "see", "other", "than", "then", "now", "look", "only",
                "come", "its", "over", "think", "also", "back", "after", "use",
                "two", "how", "our", "work", "first", "well", "way", "even",
                "new", "want", "because", "any", "these", "give", "day", "most"
            };
            return stopWords.Contains(word);
        }

        private List<(string content, string url)> ProcessScrapedData((string, float[])[] scrapedData, string searchTerms, List<string> urls)
        {
            if (string.IsNullOrWhiteSpace(searchTerms))
            {
                _logger.Log("Error: Search terms are empty");
                return new List<(string, string)>();
            }

            var docs = scrapedData.Where(d => !string.IsNullOrEmpty(d.Item1) && d.Item2.Length > 0)
                                  .Select(d => d.Item1)
                                  .ToList();
            var embeddings = scrapedData.Where(d => !string.IsNullOrEmpty(d.Item1) && d.Item2.Length > 0)
                                        .Select(d => d.Item2)
                                        .ToList();

            if (!docs.Any())
            {
                _logger.Log($"No valid scraped documents found for search terms: '{searchTerms}'");
                return new List<(string, string)>();
            }

            var queryEmbedding = ComputeEmbedding(searchTerms);
            if (queryEmbedding.Length == 0)
            {
                _logger.Log($"Failed to compute embeddings for search terms: '{searchTerms}'");
                return new List<(string, string)>();
            }
            
            // Calculate scores with content length penalty
            var scores = embeddings.Select((e, i) => {
                var similarity = CosineSimilarity(e, queryEmbedding);
                var lengthPenalty = Math.Min(1.0f, docs[i].Length / 10000.0f);
                return similarity * lengthPenalty;
            }).ToList();

            _logger.Log($"Processing search results for terms: '{searchTerms}'");
            _logger.Log($"Found {docs.Count} documents with scores: {string.Join(", ", scores)}");

            // Lower threshold and ensure we get at least some results
            const float MinThreshold = 0.1f;
            var threshold = Math.Min(RelevanceThreshold, 
                scores.OrderByDescending(s => s).Skip(2).FirstOrDefault() > 0 ? scores.OrderByDescending(s => s).Skip(2).First() : MinThreshold);

            // Get top 3 most relevant results
            var relevantDocs = docs.Zip(scores, (d, s) => (content: d, score: s))
                                   .Where(x => x.score > threshold)
                                   .OrderByDescending(x => x.score)
                                   .Take(3)
                                   .ToList();

            if (!relevantDocs.Any())
            {
                _logger.Log($"No relevant documents found above threshold {threshold} for search terms: '{searchTerms}'");
                return new List<(string, string)>();
            }

            _logger.Log($"Returning {relevantDocs.Count} relevant documents with scores: {string.Join(", ", relevantDocs.Select(d => d.score))}");

            return relevantDocs.Zip(urls.Take(relevantDocs.Count), (content, url) => (content.content, url)).ToList();
        }

        private float CosineSimilarity(float[] a, float[] b)
        {
            int length = Math.Min(a.Length, b.Length);
            if (length == 0) return 0;

            var aAdjusted = a.Length > length ? a.Take(length).ToArray() : a.Concat(Enumerable.Repeat(0f, length - a.Length)).ToArray();
            var bAdjusted = b.Length > length ? b.Take(length).ToArray() : b.Concat(Enumerable.Repeat(0f, length - b.Length)).ToArray();

            float dot = aAdjusted.Zip(bAdjusted, (x, y) => x * y).Sum();
            float magA = (float)Math.Sqrt(aAdjusted.Sum(x => x * x));
            float magB = (float)Math.Sqrt(bAdjusted.Sum(x => x * x));
            return magA * magB == 0 ? 0 : dot / (magA * magB);
        }
    }
}
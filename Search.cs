using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using RestSharp;
using HtmlAgilityPack;

namespace Gemini
{
    public class Search
    {
        private readonly Logger _logger;
        private readonly string _googleSearchApiKey;
        private readonly string _googleSearchEngineId;
        private const int GoogleSearchNumItems = 10;
        private const double RelevanceThreshold = 0.33;
        private readonly Dictionary<string, List<string>> _searchResultsCache = new();
        private readonly Dictionary<string, int> _searchStartIndices = new();
        private readonly Dictionary<string, int> _searchResultsIndex = new();
        private readonly Dictionary<string, (string, float[])> _scrapedContentCache = new();

        public Search(Logger logger, string googleSearchApiKey, string googleSearchEngineId)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _googleSearchApiKey = googleSearchApiKey ?? throw new ArgumentNullException(nameof(googleSearchApiKey));
            _googleSearchEngineId = googleSearchEngineId ?? throw new ArgumentNullException(nameof(googleSearchEngineId));
        }

        public async Task<List<(string content, string url)>> PerformSearch(string searchTerms, string originalUserQuery, Action<string, string> updateChat, Action<Status> updateStatus)
        {
            updateChat($"System: Searching for '{searchTerms}'...\n", "system");
            updateStatus(Status.Searching);

            List<string> urls = await GetSearchResults(searchTerms);
            if (!urls.Any())
            {
                _logger.Log($"No URLs found for search terms: {searchTerms}");
                return new List<(string, string)>();
            }

            updateStatus(Status.Scraping);
            var scrapedData = await ScrapeUrls(urls);

            updateStatus(Status.Processing);
            return ProcessScrapedData(scrapedData, searchTerms, urls);
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

        private List<(string content, string url)> ProcessScrapedData((string, float[])[] scrapedData, string searchTerms, List<string> urls)
        {
            var docs = scrapedData.Where(d => !string.IsNullOrEmpty(d.Item1) && d.Item2.Length > 0)
                                  .Select(d => d.Item1)
                                  .ToList();
            var embeddings = scrapedData.Where(d => !string.IsNullOrEmpty(d.Item1) && d.Item2.Length > 0)
                                        .Select(d => d.Item2)
                                        .ToList();

            if (!docs.Any())
            {
                _logger.Log("No valid scraped documents found");
                return new List<(string, string)>();
            }

            var queryEmbedding = ComputeEmbedding(searchTerms);
            var scores = embeddings.Select(e => CosineSimilarity(e, queryEmbedding)).ToList();

            // Filter and refine documents based on relevance
            var relevantDocs = docs.Zip(scores, (d, s) => (content: d, score: s))
                                   .Where(x => x.score > RelevanceThreshold && x.content.Length > 50) // Ensure minimum length for substance
                                   .OrderByDescending(x => x.score)
                                   .Select(x =>
                                   {
                                       // Trim content to remove remaining noise while keeping substance
                                       var lines = x.content.Split('\n')
                                                   .Select(line => line.Trim())
                                                   .Where(line => line.Length > 20 && // Avoid short, meaningless lines
                                                                  !line.Contains("login") && // Remove login prompts
                                                                  !line.Contains("cookie") && // Remove cookie notices
                                                                  !System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+$")); // Remove standalone numbers
                                       return string.Join("\n", lines);
                                   })
                                   .Take(3)
                                   .ToList();

            return relevantDocs.Zip(urls, (content, url) => (content, url)).ToList();
        }

        private float[] ComputeEmbedding(string text)
        {
            const int maxWords = 100;
            var words = text.Split().Take(maxWords).Select(w => (float)w.GetHashCode()).ToArray();
            return words.Length == 0 ? new float[] { 0 } : words;
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
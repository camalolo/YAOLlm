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
        private readonly GeminiClient _geminiClient;
        private readonly Logger _logger;
        private readonly string _googleSearchApiKey;
        private readonly string _googleSearchEngineId;
        private const int GoogleSearchNumItems = 10;
        private const double RelevanceThreshold = 0.5;
        private readonly Dictionary<string, List<string>> _searchResultsCache = new();
        private readonly Dictionary<string, int> _searchStartIndices = new();
        private readonly Dictionary<string, (string, float[])> _scrapedContentCache = new();
        private readonly Action<List<(string content, string url)>>? _onSearchComplete; // Callback for memory creation

        public Search(GeminiClient client, string googleSearchApiKey, string googleSearchEngineId, Action<List<(string content, string url)>>? onSearchComplete = null)
        {
            _geminiClient = client;
            _logger = client.Logger ?? throw new ArgumentNullException(nameof(client));
            _googleSearchApiKey = googleSearchApiKey ?? throw new ArgumentNullException(nameof(googleSearchApiKey));
            _googleSearchEngineId = googleSearchEngineId ?? throw new ArgumentNullException(nameof(googleSearchEngineId));
            Action<List<(string content, string url)>>? localOnSearchComplete = onSearchComplete;
            _onSearchComplete = localOnSearchComplete ?? (_ => { });
        }

        public async Task<List<(string content, string url)>> PerformSearch(string searchTerms, string originalUserQuery, Action<string, string> updateChat, Action<Status> updateStatus)
        {
            if (string.IsNullOrWhiteSpace(searchTerms))
            {
                _logger.Log("Error: Search terms are empty");
                updateChat($"System: Cannot perform search with empty search terms.\n", "system");
                return new List<(string, string)>();
            }

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
            var results = await ProcessScrapedData(scrapedData, searchTerms, urls);

            if (!results.Any())
            {
                _logger.Log($"No relevant results after processing for '{searchTerms}'");
                updateChat($"System: No relevant results found for '{searchTerms}'.\n", "system");
            }
            else
            {
                _logger.Log($"Search completed, invoking memory creation for {results.Count} results");
                _onSearchComplete?.Invoke(results); // Ensure this runs
            }

            return results;
        }

        private async Task<List<string>> GetSearchResults(string searchTerms)
        {
            if (_searchResultsCache.TryGetValue(searchTerms, out var cachedUrls))
            {
                _logger.Log($"Using cached search results for '{searchTerms}', URLs: {string.Join(", ", cachedUrls)}");
                return cachedUrls; // Simplified: no pagination
            }

            var newUrls = await SearchGoogle(searchTerms);
            _searchResultsCache[searchTerms] = newUrls;
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
            var results = await Task.WhenAll(urls.Select(async url =>
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

                    foreach (var node in doc.DocumentNode.SelectNodes("//script | //style | //nav | //footer | //*[contains(@class, 'advert')] | //*[contains(@class, 'popup')]") ?? new HtmlNodeCollection(null))
                        node.Remove();

                    var contentNode = doc.DocumentNode.SelectSingleNode("//article") ?? doc.DocumentNode.SelectSingleNode("//main") ?? doc.DocumentNode.SelectSingleNode("//body");
                    var text = contentNode != null ? HtmlEntity.DeEntitize(contentNode.InnerText) : doc.DocumentNode.InnerText;

                    text = Regex.Replace(text, @"\s+", " ").Trim();
                    text = Regex.Replace(text, @"(Something went wrong|Try again|Please enable Javascript|Subscribe to our newsletter)", "", RegexOptions.IgnoreCase);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _logger.Log($"No meaningful content scraped from {url}");
                        return (string.Empty, new float[0]);
                    }

                    var embedding = await _geminiClient.Embed(text);
                    _scrapedContentCache[url] = (text, embedding);
                    _logger.Log($"Scraped content from {url}, length: {text.Length} chars");
                    return (text, embedding);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.Log($"Forbidden (403) error scraping {url}");
                    return (string.Empty, new float[0]);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Error scraping {url}: {ex.Message}");
                    return (string.Empty, new float[0]);
                }
            }));
            return results;
        }


        private async Task<List<(string content, string url)>> ProcessScrapedData((string, float[])[] scrapedData, string searchTerms, List<string> urls)
        {
            var validData = scrapedData.Select((data, idx) => (content: data.Item1, embedding: data.Item2, url: urls[idx]))
                                       .Where(d => !string.IsNullOrEmpty(d.content) && d.embedding.Length == 768) // Assuming 768 dimensions
                                       .ToList();
            var docs = validData.Select(d => d.content).ToList();
            var embeddings = validData.Select(d => d.embedding).ToList();
            var validUrls = validData.Select(d => d.url).ToList();

            if (!docs.Any())
            {
                _logger.Log("No valid data after filtering (content or embedding dimension mismatch)");
                return new List<(string, string)>();
            }

            var queryEmbedding = await _geminiClient.Embed(searchTerms);
            if (queryEmbedding.Length != 768) // Match expected dimension
            {
                _logger.Log($"Query embedding dimension mismatch: expected 768, got {queryEmbedding.Length}");
                return new List<(string, string)>();
            }

            var scores = embeddings.Select(e => CosineSimilarity(e, queryEmbedding)).ToList();
            // Rest of the function remains unchanged...

            var allResults = docs.Zip(validUrls, (d, u) => (content: d, url: u))
                                 .Zip(scores, (r, s) => $"URL: {r.url}, Score: {s:F3}");
            _logger.Log($"All {docs.Count} processed results for '{searchTerms}': [{string.Join("; ", allResults)}]");

            var filteredResults = docs.Zip(scores, (d, s) => (content: d, score: s))
                                      .Zip(validUrls, (cs, u) => (content: cs.content, score: cs.score, url: u))
                                      .Where(x => x.score > RelevanceThreshold)
                                      .OrderByDescending(x => x.score)
                                      .Take(3)
                                      .Select(x => (x.content, x.url))
                                      .ToList();

            var filteredDetails = filteredResults.Zip(scores.Where(s => s > RelevanceThreshold).OrderByDescending(s => s).Take(3),
                (r, s) => $"URL: {r.url}, Score: {s:F3}");
            _logger.Log($"Filtered {filteredResults.Count} relevant results for '{searchTerms}' (threshold {RelevanceThreshold}): [{string.Join("; ", filteredDetails)}]");

            return filteredResults;
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
    }
}
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using RestSharp;
using HtmlAgilityPack;
using System.Net.Http;

namespace Gemini
{
    public class SearchService
    {
        private readonly GeminiClient _client;
        private readonly Logger _logger;
        private readonly string _googleSearchApiKey;
        private readonly string _googleSearchEngineId;
        private readonly Func<List<(string content, string url)>, Task> _onSearchComplete;
        private static readonly HttpClient _httpClient = new();
        private const int MaxResultsPerSearch = 10;
        private const double RelevanceThreshold = 0.5;

        static SearchService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            _httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        public SearchService(GeminiClient client, string googleSearchApiKey, string googleSearchEngineId, Func<List<(string content, string url)>, Task> onSearchComplete)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = client.Logger ?? throw new ArgumentNullException(nameof(client.Logger));
            _googleSearchApiKey = googleSearchApiKey ?? throw new ArgumentNullException(nameof(googleSearchApiKey));
            _googleSearchEngineId = googleSearchEngineId ?? throw new ArgumentNullException(nameof(googleSearchEngineId));
            _onSearchComplete = onSearchComplete ?? (_ => Task.CompletedTask);
        }

        public async Task<List<(string content, string url)>> PerformSearch(string searchTerms, string originalQuery)
        {
            if (string.IsNullOrWhiteSpace(searchTerms))
            {
                _logger.Log("Search terms are empty");
                _client.UpdateChat("System: Cannot search with empty terms.\n", "system");
                return new List<(string, string)>();
            }

            _client.UpdateStatus(Status.Searching);
            var urls = await PerformGoogleSearch(searchTerms);
            if (!urls.Any())
            {
                _logger.Log($"No URLs found for: {searchTerms}");
                _client.UpdateChat($"System: No results for '{searchTerms}'.\n", "system");
                return new List<(string, string)>();
            }

            _client.UpdateStatus(Status.Scraping);
            var scrapedData = await ScrapeUrls(urls, searchTerms);

            _client.UpdateStatus(Status.Processing);
            var results = await ProcessScrapedData(scrapedData, searchTerms, urls, originalQuery);
            if (results.Any())
            {
                _logger.Log($"Search completed with {results.Count} results");
                await _onSearchComplete(results); // Store in memory
            }
            else
            {
                _logger.Log($"No relevant results for: {searchTerms}");
                _client.UpdateChat($"System: No relevant results for '{searchTerms}'.\n", "system");
            }

            return results;
        }

        private async Task<List<string>> PerformGoogleSearch(string searchTerms)
        {
            try
            {
                _logger.Log($"Google search: {searchTerms}");
                var client = new RestClient("https://www.googleapis.com/customsearch/v1");
                var request = new RestRequest { Method = Method.Get };
                request.AddParameter("key", _googleSearchApiKey);
                request.AddParameter("cx", _googleSearchEngineId);
                request.AddParameter("q", searchTerms);
                request.AddParameter("num", MaxResultsPerSearch);

                var response = await client.ExecuteAsync(request);
                response.ThrowIfError();

                if (string.IsNullOrEmpty(response.Content))
                {
                    _logger.Log("Google search returned empty content");
                    return new List<string>();
                }

                var data = JsonSerializer.Deserialize<JsonElement>(response.Content);
                if (data.TryGetProperty("items", out var items))
                {
                    var urls = items.EnumerateArray()
                        .Select(item => item.GetProperty("link").GetString() ?? string.Empty)
                        .Where(url => !string.IsNullOrEmpty(url))
                        .ToList();
                    _logger.Log($"Fetched {urls.Count} URLs");
                    return urls;
                }

                _logger.Log("No items in Google search response");
                return new List<string>();
            }
            catch (Exception ex)
            {
                _logger.Log($"Google search error: {ex.Message}");
                return new List<string>();
            }
        }

        private async Task<(string content, float[] embedding)[]> ScrapeUrls(List<string> urls, string searchterms)
        {
            return await Task.WhenAll(urls.Select(async url =>
            {
                try
                {
                    var html = await _httpClient.GetStringAsync(url);
                    var doc = new HtmlAgilityPack.HtmlDocument();
                    doc.LoadHtml(html);

                    RemoveUnwantedNodes(doc);
                    var contentNode = GetContentNode(doc);
                    var text = CleanText(contentNode?.InnerText ?? doc.DocumentNode.InnerText);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _logger.Log($"No content scraped from: {url}");
                        return (string.Empty, new float[0]);
                    }

                    var embedding = await _client.Embed(text);
                    text = $"Content based on search terms: {searchterms} and retrieved from {url}:\n\n" + text;

                    _logger.Log($"Scraped {text.Length} chars from: {url}");
                    return (text, embedding);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.Log($"403 Forbidden: {url}");
                    return (string.Empty, new float[0]);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Scraping error for {url}: {ex.Message}");
                    return (string.Empty, new float[0]);
                }
            }));
        }

        private void RemoveUnwantedNodes(HtmlAgilityPack.HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//script | //style | //nav | //footer | //*[contains(@class, 'advert')] | //*[contains(@class, 'popup')]");
            if (nodes != null)
                foreach (var node in nodes)
                    node.Remove();
        }

        private HtmlNode? GetContentNode(HtmlAgilityPack.HtmlDocument doc)
        {
            return doc.DocumentNode.SelectSingleNode("//article") ??
                   doc.DocumentNode.SelectSingleNode("//main") ??
                   doc.DocumentNode.SelectSingleNode("//body");
        }

        private string CleanText(string text)
        {
            text = HtmlEntity.DeEntitize(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private async Task<List<(string content, string url)>> ProcessScrapedData(
            (string content, float[] embedding)[] scrapedData, string searchTerms, List<string> urls, string originalQuery)
        {
            var validData = scrapedData
                .Select((data, i) => (data.content, data.embedding, url: urls[i], searchterms: searchTerms))
                .Where(d => !string.IsNullOrEmpty(d.content) && d.embedding.Length == Utils.ExpectedEmbeddingDimension)
                .ToList();

            if (!validData.Any())
            {
                _logger.Log("No valid scraped data after filtering");
                return new List<(string, string)>();
            }

            var queryEmbedding = await _client.Embed(searchTerms);
            if (queryEmbedding.Length != Utils.ExpectedEmbeddingDimension)
            {
                _logger.Log($"Query embedding mismatch: expected {Utils.ExpectedEmbeddingDimension}, got {queryEmbedding.Length}");
                return new List<(string, string)>();
            }

            var maxLength = validData.Max(d => d.content.Length);
            const float LengthWeight = 0.3f;

            var scores = validData.Select(d =>
            {
                float similarity = Utils.CosineSimilarity(d.embedding, queryEmbedding);
                float lengthFactor = maxLength > 0 ? (float)d.content.Length / maxLength : 0;
                return (1 - LengthWeight) * similarity + LengthWeight * lengthFactor;
            }).ToList();

            var results = validData
                .Zip(scores, (d, s) => (d.content, d.url, s))
                .Where(x => x.s > RelevanceThreshold)
                .OrderByDescending(x => x.s)
                .Take(3)
                .Select(x => (x.content, x.url))
                .ToList();

            _logger.Log($"Processed {results.Count} relevant results for '{searchTerms}'");
            return results;
        }
    }
}
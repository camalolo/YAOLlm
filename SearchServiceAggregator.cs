using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace YAOLlm;

public class SearchServiceAggregator : ISearchService, IDisposable
{
    private readonly List<ISearchService> _services;
    private readonly Logger _logger;

    public SearchServiceAggregator(List<ISearchService> services, Logger? logger = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? new Logger();
    }

    public async Task<string> SearchAsync(string query, int maxResults = 5, string searchDepth = "basic")
    {
        string? lastError = null;

        foreach (var service in _services)
        {
            var result = await service.SearchAsync(query, maxResults, searchDepth);

            if (!result.StartsWith("Error:"))
                return result;

            lastError = result;
            _logger.Log($"[SearchAggregator] Service returned error, trying next: {result}");
        }

        return lastError ?? "Error: All search services failed";
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            if (service is IDisposable disposable)
                disposable.Dispose();
        }
    }
}

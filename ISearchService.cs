using System.Threading.Tasks;

namespace YAOLlm;

public interface ISearchService
{
    /// <summary>
    /// Perform a search. Returns formatted results on success.
    /// On failure, returns a string starting with "Error:" — callers use this
    /// convention to detect failures and fall through to alternate services.
    /// </summary>
    Task<string> SearchAsync(string query, int maxResults = 5, string searchDepth = "basic");
}

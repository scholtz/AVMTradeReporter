using AVMTradeReporter.Model.Configuration;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace AVMTradeReporter.Services.ScamRating
{
    /// <summary>
    /// HTTP client for the ARC-56 program hash registry. A hash is registered when
    /// <c>{baseUrl}/approval-programs/{hash[:3]}/{hash}.txt</c> exists (HTTP 200); a 404 means the
    /// registry has not indexed a spec for that program. Results are cached in memory: positive answers
    /// forever (a hash never leaves the registry), negative answers for
    /// <see cref="ScamRatingConfiguration.NotRegisteredCacheHours"/> because the registry is regenerated
    /// daily and a contract may publish its spec later.
    /// </summary>
    public class Arc56RegistryClient : IArc56RegistryClient
    {
        private static readonly Regex Sha256HexRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

        private readonly HttpClient _httpClient;
        private readonly ScamRatingConfiguration _config;
        private readonly ILogger<Arc56RegistryClient> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly ConcurrentDictionary<string, (bool Registered, DateTimeOffset CheckedAt)> _cache = new();

        public Arc56RegistryClient(
            HttpClient httpClient,
            IOptions<AppConfiguration> appConfig,
            ILogger<Arc56RegistryClient> logger,
            TimeProvider? timeProvider = null)
        {
            _httpClient = httpClient;
            _config = appConfig.Value.ScamRating;
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Builds the registry lookup URL for an approval program hash.
        /// </summary>
        public static string BuildApprovalProgramUrl(string baseUrl, string approvalProgramHash)
        {
            var hash = approvalProgramHash.Trim().ToLowerInvariant();
            return $"{baseUrl.TrimEnd('/')}/approval-programs/{hash[..3]}/{hash}.txt";
        }

        public async Task<bool?> IsApprovalProgramRegisteredAsync(string approvalProgramHash, CancellationToken cancellationToken = default)
        {
            if (!_config.RegistryLookupEnabled) return null;
            if (string.IsNullOrWhiteSpace(approvalProgramHash)) return null;

            var hash = approvalProgramHash.Trim().ToLowerInvariant();
            if (!Sha256HexRegex.IsMatch(hash))
            {
                _logger.LogWarning("ARC-56 registry lookup skipped - {hash} is not a hex SHA-256 hash", approvalProgramHash);
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            if (_cache.TryGetValue(hash, out var cached))
            {
                if (cached.Registered) return true;
                if (now - cached.CheckedAt < TimeSpan.FromHours(_config.NotRegisteredCacheHours)) return false;
            }

            var url = BuildApprovalProgramUrl(_config.Arc56RegistryBaseUrl, hash);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _cache[hash] = (false, now);
                    return false;
                }
                if (response.IsSuccessStatusCode)
                {
                    _cache[hash] = (true, now);
                    return true;
                }

                _logger.LogWarning("ARC-56 registry lookup of {url} returned unexpected status {status}", url, (int)response.StatusCode);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ARC-56 registry lookup of {url} failed", url);
                return null;
            }
        }
    }
}

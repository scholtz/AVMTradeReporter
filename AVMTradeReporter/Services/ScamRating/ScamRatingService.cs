using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Models.Data;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Services.ScamRating
{
    /// <summary>
    /// Scores how likely a pool is a scam deployment.
    ///
    /// Signals (in order):
    /// 1. <see cref="ScamRatingConfiguration.KnownScamPools"/> - a manually curated rating wins outright
    ///    (100 = known scammer pool, nobody should trust it).
    /// 2. The pool's approval program hash is not in the ARC-56 registry
    ///    (+<see cref="ScamRatingConfiguration.UnregisteredContractPoints"/>, default 5). A registry
    ///    outage or an unknown answer adds nothing - the absence of evidence must not penalise a pool.
    ///
    /// Anything else scores 0 (the default).
    /// </summary>
    public class ScamRatingService : IScamRatingService
    {
        private readonly IArc56RegistryClient _registryClient;
        private readonly ScamRatingConfiguration _config;
        private readonly ILogger<ScamRatingService> _logger;

        public ScamRatingService(
            IArc56RegistryClient registryClient,
            IOptions<AppConfiguration> appConfig,
            ILogger<ScamRatingService> logger)
        {
            _registryClient = registryClient;
            _config = appConfig.Value.ScamRating;
            _logger = logger;
        }

        public async Task<int> ComputeScamRatingAsync(Pool pool, CancellationToken cancellationToken = default)
        {
            if (_config.KnownScamPools.TryGetValue(pool.PoolAppId, out var knownRating))
            {
                return ScamRatingPolicy.Clamp(knownRating);
            }

            var rating = 0;

            if (!string.IsNullOrWhiteSpace(pool.ApprovalProgramHash))
            {
                var registered = await _registryClient.IsApprovalProgramRegisteredAsync(pool.ApprovalProgramHash, cancellationToken);
                if (registered == false)
                {
                    rating += _config.UnregisteredContractPoints;
                }
            }

            return ScamRatingPolicy.Clamp(rating);
        }

        public async Task<bool> ApplyAsync(Pool pool, CancellationToken cancellationToken = default)
        {
            var rating = await ComputeScamRatingAsync(pool, cancellationToken);
            var changed = false;
            if (pool.ScamRating != rating)
            {
                _logger.LogInformation("Pool {appId} {address} scam rating {oldRating} -> {newRating}", pool.PoolAppId, pool.PoolAddress, pool.ScamRating, rating);
                pool.ScamRating = rating;
                changed = true;
            }
            if (ScamRatingPolicy.Enforce(pool))
            {
                changed = true;
            }
            return changed;
        }
    }
}

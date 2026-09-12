using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Services.ScamRating
{
    /// <summary>
    /// Computes and applies <see cref="Pool.ScamRating"/>.
    /// </summary>
    public interface IScamRatingService
    {
        /// <summary>
        /// Computes the scam rating (0..100) of the pool from every available signal without mutating it.
        /// </summary>
        Task<int> ComputeScamRatingAsync(Pool pool, CancellationToken cancellationToken = default);

        /// <summary>
        /// Computes the rating, stores it in <see cref="Pool.ScamRating"/> and enforces
        /// <see cref="ScamRatingPolicy.ApplyBalanceRule"/>. Returns true when the pool was changed.
        /// </summary>
        Task<bool> ApplyAsync(Pool pool, CancellationToken cancellationToken = default);
    }
}

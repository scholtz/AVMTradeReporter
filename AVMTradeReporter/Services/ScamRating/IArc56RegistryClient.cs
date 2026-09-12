namespace AVMTradeReporter.Services.ScamRating
{
    /// <summary>
    /// Lookup client for the public ARC-56 program hash registry
    /// (https://github.com/scholtz/ARC56Registry, served at https://scholtz.github.io/ARC56Registry/).
    /// </summary>
    public interface IArc56RegistryClient
    {
        /// <summary>
        /// Checks whether an approval program with the given lowercase hex SHA-256 hash has a public
        /// ARC-56 spec in the registry.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the registry knows the hash, <c>false</c> when it does not, and <c>null</c>
        /// when the answer could not be determined (network error, malformed hash, lookup disabled) -
        /// callers must not treat <c>null</c> as a scam signal.
        /// </returns>
        Task<bool?> IsApprovalProgramRegisteredAsync(string approvalProgramHash, CancellationToken cancellationToken = default);
    }
}

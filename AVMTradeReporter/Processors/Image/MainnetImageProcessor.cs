using Algorand.Gossip;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Processors.Image
{
    public class MainnetImageProcessor
    {
        private static readonly byte[] Placeholder = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

        /// <summary>
        /// A cached icon older than this is re-resolved from source on the next request, so a
        /// once-bad icon (e.g. the Meld Gold / ASA.Gold ticker-collision mixup, see
        /// docs/ICON_SHARING.md) or a since-corrected upstream icon eventually self-heals without
        /// manual cache-file surgery. The re-resolution result must pass <see cref="IsUsableImage"/>
        /// before it overwrites anything - see the comment at the refresh call site.
        /// </summary>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);

        /// <summary>
        /// Minimum plausible size for a real icon PNG, comfortably above the 68-byte 1x1
        /// placeholder. Guards both the initial resolution and the staleness refresh against
        /// writing a corrupt/truncated/near-empty download over a working cached file.
        /// </summary>
        private const int MinUsableImageBytes = 256;

        private static bool IsUsableImage(byte[] data) =>
            data.Length >= MinUsableImageBytes && !data.AsSpan().SequenceEqual(Placeholder);

        private readonly IAssetRepository _assetRepository;
        private readonly AppConfiguration _config;

        public MainnetImageProcessor(IAssetRepository assetRepository, IOptions<AppConfiguration> config)
        {
            _assetRepository = assetRepository;
            _config = config.Value;
        }

        private bool IsMainnet => _config.GossipDiscovery.Network == GossipNetwork.AlgorandMainNet;

        /// <summary>
        /// Per-network subfolder for the id-keyed cache. Kept separate per network because
        /// Algorand mainnet and testnet (and other AVM networks) use independent asset id
        /// numbering spaces, so the same numeric id can mean two unrelated assets.
        /// </summary>
        private string NetworkFolder => _config.GossipDiscovery.Network switch
        {
            GossipNetwork.AlgorandMainNet => "mainnet-v1.0",
            GossipNetwork.AlgorandTestNet => "testnet-v1.0",
            var n => n.ToString().ToLowerInvariant(),
        };

        public async Task<byte[]> LoadImageAsync(ulong assetId, CancellationToken cancellationToken)
        {
            var imagesRoot = Path.Combine(AppContext.BaseDirectory, "images");
            var byIdLoader = new FilesystemImageLoader(Path.Combine(imagesRoot, NetworkFolder));
            var fsPath = $"{assetId}.png";

            var imageFromSystem = await byIdLoader.LoadImageAsync(fsPath, cancellationToken);
            if (imageFromSystem.Length > 0)
            {
                var lastWriteUtc = byIdLoader.GetLastWriteTimeUtc(fsPath);
                var isStale = lastWriteUtc.HasValue && DateTime.UtcNow - lastWriteUtc.Value > RefreshInterval;
                if (!isStale)
                {
                    return imageFromSystem;
                }
                // Cached icon is old enough to be worth re-resolving (falls through to the same
                // resolution logic used for a cache miss below). imageFromSystem is kept as a
                // fallback: if the fresh attempt fails or comes back too small/garbage, we keep
                // serving the old-but-working icon rather than replacing it with junk.
            }

            // Shared cache keyed by ASA unit name (ticker), stored on the images volume shared
            // between every network's deployment (see docs/ICON_SHARING.md). Populated by mainnet
            // resolutions below and consumed ONLY by non-mainnet deployments as a best-effort
            // fallback for a testnet asset with the same ticker as a real mainnet asset.
            //
            // Mainnet must NEVER read from this cache for its own lookups: distinct mainnet assets
            // can share a UnitName (e.g. "Meld Gold" and "ASA.Gold" both using a GOLD-style
            // ticker), and reading it back here would silently paint one real project's icon onto
            // an unrelated real project's asset. Mainnet identity must stay strictly asset-id-keyed
            // - see docs/ICON_SHARING.md.
            var byUnitNameLoader = new FilesystemImageLoader(Path.Combine(imagesRoot, "by-unitname"));
            var asset = await _assetRepository.GetAssetAsync(assetId, cancellationToken);
            var unitNameKey = NormalizeUnitName(asset?.Params?.UnitName);

            if (!IsMainnet && unitNameKey != null)
            {
                var sharedImage = await byUnitNameLoader.LoadImageAsync($"{unitNameKey}.png", cancellationToken);
                if (IsUsableImage(sharedImage))
                {
                    await byIdLoader.SaveImageAsync(fsPath, sharedImage);
                    return sharedImage;
                }
            }

            // Tinyman's ASA list and Pera's asset API only index Algorand mainnet - skip them on
            // non-mainnet deployments so we don't burn requests on ids that can never resolve
            // there, and don't poison the by-id cache with a placeholder before the by-unitname
            // fallback above ever gets a chance to be populated by the mainnet deployment.
            if (IsMainnet)
            {
                var tinymanAsaListImageLoader = new TinymanAsaListImageLoader();
                var imageFromTiny = await tinymanAsaListImageLoader.LoadImageAsync(assetId, cancellationToken);
                if (IsUsableImage(imageFromTiny))
                {
                    await byIdLoader.SaveImageAsync(fsPath, imageFromTiny);
                    await SaveSharedUnitNameIfAbsentAsync(byUnitNameLoader, unitNameKey, imageFromTiny, cancellationToken);
                    return imageFromTiny;
                }

                var peraLoader = new PeraImageLoader();
                var imageFromPera = await peraLoader.LoadImageAsync(assetId, cancellationToken);
                if (IsUsableImage(imageFromPera))
                {
                    await byIdLoader.SaveImageAsync(fsPath, imageFromPera);
                    await SaveSharedUnitNameIfAbsentAsync(byUnitNameLoader, unitNameKey, imageFromPera, cancellationToken);
                    return imageFromPera;
                }
            }

            if (imageFromSystem.Length > 0)
            {
                // Staleness refresh found nothing usable (network failure, upstream still serving
                // a placeholder, etc). Never let a failed refresh attempt destroy a working cached
                // icon - keep serving the stale-but-valid one and try again on the next request
                // past the TTL.
                return imageFromSystem;
            }

            await byIdLoader.SaveImageAsync(fsPath, Placeholder);
            return Placeholder;
        }

        /// <summary>
        /// First-writer-wins: if two distinct mainnet assets share a UnitName (e.g. two different
        /// "GOLD"-ticker tokens), whichever resolves its icon first claims the shared slot and
        /// later ones don't overwrite it. Keeps the testnet-facing fallback stable instead of
        /// flip-flopping between two unrelated projects' icons on every re-resolution.
        /// </summary>
        private static async Task SaveSharedUnitNameIfAbsentAsync(FilesystemImageLoader byUnitNameLoader, string? unitNameKey, byte[] imageData, CancellationToken cancellationToken)
        {
            if (unitNameKey == null)
            {
                return;
            }

            var fileName = $"{unitNameKey}.png";
            var existing = await byUnitNameLoader.LoadImageAsync(fileName, cancellationToken);
            if (existing.Length == 0)
            {
                await byUnitNameLoader.SaveImageAsync(fileName, imageData);
            }
        }

        private static string? NormalizeUnitName(string? unitName)
        {
            if (string.IsNullOrWhiteSpace(unitName))
            {
                return null;
            }

            var cleaned = new string(unitName.Trim().ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
            return cleaned.Length == 0 ? null : cleaned;
        }
    }
}

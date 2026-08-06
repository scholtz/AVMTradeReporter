using Algorand.Gossip;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Repository;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter.Processors.Image
{
    public class MainnetImageProcessor
    {
        private static readonly byte[] Placeholder = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

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
                return imageFromSystem;
            }

            // Shared cache keyed by ASA unit name (ticker), stored on the images volume shared
            // between every network's deployment (see docs/ICON_SHARING.md). Lets a testnet asset
            // reuse the icon already downloaded for its mainnet counterpart with the same ticker,
            // without needing to know the mainnet asset's id.
            var byUnitNameLoader = new FilesystemImageLoader(Path.Combine(imagesRoot, "by-unitname"));
            var asset = await _assetRepository.GetAssetAsync(assetId, cancellationToken);
            var unitNameKey = NormalizeUnitName(asset?.Params?.UnitName);

            if (unitNameKey != null)
            {
                var sharedImage = await byUnitNameLoader.LoadImageAsync($"{unitNameKey}.png", cancellationToken);
                if (sharedImage.Length > 0 && !sharedImage.AsSpan().SequenceEqual(Placeholder))
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
                if (imageFromTiny.Length > 0)
                {
                    await byIdLoader.SaveImageAsync(fsPath, imageFromTiny);
                    if (unitNameKey != null) await byUnitNameLoader.SaveImageAsync($"{unitNameKey}.png", imageFromTiny);
                    return imageFromTiny;
                }

                var peraLoader = new PeraImageLoader();
                var imageFromPera = await peraLoader.LoadImageAsync(assetId, cancellationToken);
                if (imageFromPera.Length > 0)
                {
                    await byIdLoader.SaveImageAsync(fsPath, imageFromPera);
                    if (unitNameKey != null) await byUnitNameLoader.SaveImageAsync($"{unitNameKey}.png", imageFromPera);
                    return imageFromPera;
                }
            }

            await byIdLoader.SaveImageAsync(fsPath, Placeholder);
            return Placeholder;
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

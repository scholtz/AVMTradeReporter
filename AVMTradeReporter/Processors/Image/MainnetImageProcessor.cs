namespace AVMTradeReporter.Processors.Image
{
    public class MainnetImageProcessor
    {
        public async Task<byte[]> LoadImageAsync(ulong assetId, CancellationToken cancellationToken)
        {

            var imagesDir = Path.Combine(AppContext.BaseDirectory, "images", "mainnet-v1.0");
            var filesystemImageLoader = new FilesystemImageLoader(imagesDir);
            var fsPath = $"{assetId}.png";
            var imageFromSystem = await filesystemImageLoader.LoadImageAsync(fsPath, cancellationToken);

            if (imageFromSystem.Length > 0)
            {
                return imageFromSystem;
            }

            var tinymanAsaListImageLoader = new TinymanAsaListImageLoader();
            var imageFromTiny = await tinymanAsaListImageLoader.LoadImageAsync(assetId, cancellationToken);

            if (imageFromTiny.Length > 0)
            {
                await filesystemImageLoader.SaveImageAsync(fsPath, imageFromTiny);
                return imageFromTiny;
            }


            var peraLoader = new PeraImageLoader();
            var imageFromPera = await peraLoader.LoadImageAsync(assetId, cancellationToken);

            if (imageFromPera.Length > 0)
            {
                await filesystemImageLoader.SaveImageAsync(fsPath, imageFromPera);
                return imageFromPera;
            }


            var placeholder = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
            await filesystemImageLoader.SaveImageAsync(fsPath, placeholder);

            return placeholder;
        }
    }
}

namespace AVMTradeReporter.Processors.Image
{
    public class FilesystemImageLoader
    {
        private readonly string _imageDirectory;
        public FilesystemImageLoader(string imageDirectory)
        {
            _imageDirectory = imageDirectory;
        }
        public async Task<byte[]> LoadImageAsync(string imageName, CancellationToken cancellationToken)
        {
            try
            {
                var filePath = Path.Combine(_imageDirectory, imageName);
                if (!File.Exists(filePath))
                {
                    return Array.Empty<byte>();
                }
                return await File.ReadAllBytesAsync(filePath, cancellationToken);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
        public async Task SaveImageAsync(string imageName, byte[] imageData)
        {
            var filePath = Path.Combine(_imageDirectory, imageName);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);
            await File.WriteAllBytesAsync(filePath, imageData);
        }
    }
}

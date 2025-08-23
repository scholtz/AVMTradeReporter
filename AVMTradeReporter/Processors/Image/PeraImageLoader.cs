using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace AVMTradeReporter.Processors.Image
{
    public class PeraImageLoader
    {
        class AssetData
        {
            public string Logo { get; set; } = string.Empty;
        }
        public async Task<byte[]> LoadImageAsync(ulong assetId, CancellationToken cancellationToken)
        {
            try
            {
                // load asset information from https://mainnet.api.perawallet.app/v1/public/assets/{assetId}/
                // from loaded json load logo {logo:"url"}
                // load image from url
                var url = $"https://mainnet.api.perawallet.app/v1/public/assets/{assetId}/";
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var response = await httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return Array.Empty<byte>();
                }
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var data = Newtonsoft.Json.JsonConvert.DeserializeObject<AssetData>(json);
                if (data == null || string.IsNullOrEmpty(data.Logo))
                {
                    return Array.Empty<byte>();
                }
                var logoUrl = data.Logo;
                // Validate URL format
                if (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var logoUri) || !logoUri.IsAbsoluteUri)
                {
                    return Array.Empty<byte>();
                }
                // Load image from URL
                var imageResponse = await httpClient.GetAsync(logoUri, cancellationToken);
                if (!imageResponse.IsSuccessStatusCode)
                {
                    return Array.Empty<byte>();
                }
                var imageData = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                // Check if the image is a valid PNG
                if (imageData.Length < 8 || imageData[0] != 0x89 || imageData[1] != 0x50 || imageData[2] != 0x4E || imageData[3] != 0x47 ||
                    imageData[4] != 0x0D || imageData[5] != 0x0A || imageData[6] != 0x1A || imageData[7] != 0x0A)
                {
                    return Array.Empty<byte>();
                }
                // Return the image data
                return imageData;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        public Task SaveImageAsync(string imageName, byte[] imageData)
        {
            throw new NotImplementedException("Saving images to pera is not implemented.");
        }
    }
}

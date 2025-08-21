using System.Net;

namespace AVMTradeReporter.Processors.Image
{
    public class TinymanAsaListImageLoader
    {
        public async Task<byte[]> LoadImageAsync(ulong assetId, CancellationToken cancellationToken)
        {
            try
            {
                var remoteUrl = $"https://asa-list.tinyman.org/assets/{assetId}/icon.png";
                using var httpClient = new HttpClient();
                //httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("image/png"));
                using var response = await httpClient.GetAsync(remoteUrl, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return Array.Empty<byte>();
                }
                // check if the content type is image/png
                if (response.Content.Headers.ContentType?.MediaType != "image/png")
                {
                    return Array.Empty<byte>();
                }

                return await response.Content.ReadAsByteArrayAsync();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
        public Task SaveImageAsync(string imageName, byte[] imageData)
        {
            throw new NotImplementedException("Saving images to tinyman ASA list is not implemented.");
        }
    }
}

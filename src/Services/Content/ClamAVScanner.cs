using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ecommerce.Contracts;
using ecommerce.Models;
using Microsoft.Extensions.Configuration;

namespace ecommerce.Services
{
    public class ClamAVScanner: IVirusScanner
    {
        private readonly HttpClient _httpClient;
        private readonly string _restApiUrl; // Use null-coalescing operator or 'required'

        public ClamAVScanner(IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient();
            _restApiUrl = config["ClamAV:RestApiUrl"]?? string.Empty; // Provide default value
        }

        public async Task<VirusScanResult> ScanAsync(Stream fileStream)
        {
            fileStream.Position = 0;
            using var content = new StreamContent(fileStream);
            var response = await _httpClient.PostAsync(_restApiUrl, content);
            
            if (!response.IsSuccessStatusCode)
            {
                // More informative error message
                throw new HttpRequestException($"ClamAV scan failed with status code: {response.StatusCode}"); 
            }

            var result = await JsonSerializer.DeserializeAsync<ClamAVResponse>(
                await response.Content.ReadAsStreamAsync()
            );

            return new VirusScanResult(
                result!.IsMalicious, 
                result.VirusName
            );
        }

        private class ClamAVResponse
        {
            public bool IsMalicious { get; set; }
            public string? VirusName { get; set; }
        }
    }
}
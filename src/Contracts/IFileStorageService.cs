using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ecommerce.Contracts
{
    public interface IFileStorageService
    {
        Task<string> UploadFileAsync(Stream fileStream, string filePath, bool append = false);
        Task<Stream> DownloadFile(string filePath);
        Task<bool> DeleteFileAsync(string filePath);
        string GenerateSignedUrl(string filePath, TimeSpan validFor);
        bool TryValidateSignedUrl(string url, out string filePath);
        Task CommitAsync(string filePath, List<string> blockIds);
    }
}

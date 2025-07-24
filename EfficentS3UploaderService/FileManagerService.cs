using Amazon.S3;
using Amazon.S3.Model;
using EfficentS3UploadSerivice;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using System.IO;
using System.Runtime.Intrinsics.Arm;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace EfficentS3UploadService
{
    internal class FileManagerService
    {
        private readonly ILogger<Worker> _logger;
        private readonly string _basePath;
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;

        public FileManagerService(ILogger<Worker> logger, string basePath, IAmazonS3 s3Client, string bucketName)
        {
            _logger = logger;
            _basePath = basePath;
            _s3Client = s3Client;
            _bucketName = bucketName;
        }

        public async Task ProcessMessageAndDownloadAsync(string jsonMessage)
        {
            _logger.LogInformation("(ProcessMessageAndDownloadAsync) File da processare : {json}", jsonMessage);
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(jsonMessage);
                if (payload == null || !payload.TryGetValue("key", out var encodedKey))
                {
                    _logger.LogWarning("Messaggio JSON non contiene 'key': {json}", jsonMessage);
                    return;
                }

                string s3Key = HttpUtility.UrlDecode(encodedKey);
                string localPath = Path.Combine(_basePath, s3Key);
                // 1. Leggi il metadata SHA256 da S3
                string? s3Sha256 = await GetS3ObjectSha256MetadataAsync(s3Key);
                // 2. Se il file locale esiste, calcola l'hash locale e confronta
                if (File.Exists(localPath))
                {
                    byte[] localFileBytes = File.ReadAllBytes(localPath);
                    string localSha256 = ComputeSha256(localFileBytes);

                    if (!string.IsNullOrEmpty(s3Sha256) && localSha256 == s3Sha256)
                    {
                        _logger.LogInformation("Il file locale è aggiornato (SHA256 coincide), skipping download: {localPath}", localPath);
                        return; // Salta il download e salvataggio
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                byte[] s3Content = await DownloadFromS3Async(s3Key);
                if (s3Content == null)
                {
                    _logger.LogWarning("File non trovato su S3 per la chiave: {s3Key}", s3Key);
                    return;
                }
                FileModificationTracker.MarkAsModifiedByMqtt(localPath);
                SaveOrUpdateFile(localPath, s3Content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante l'elaborazione del messaggio JSON: {json}", jsonMessage);
            }
        }

        private async Task<byte[]?> DownloadFromS3Async(string key)
        {
            try
            {
                GetObjectRequest request = new()
                {
                    BucketName = _bucketName,
                    Key = key
                };

                using GetObjectResponse response = await _s3Client.GetObjectAsync(request);
                using var ms = new MemoryStream();
                await response.ResponseStream.CopyToAsync(ms);
                return ms.ToArray();
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("File non trovato in S3: {key}", key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante il download da S3: {key}", key);
                return null;
            }
        }

        private void SaveOrUpdateFile(string filePath, byte[] newContent)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    if (IsFileLocked(filePath))
                    {
                        _logger.LogWarning("Il file è in uso e non può essere sovrascritto: {filePath}", filePath);
                        return;
                    }

                    byte[] existingContent = File.ReadAllBytes(filePath);
                    if (ComputeSha256(existingContent) == ComputeSha256(newContent))
                    {
                        _logger.LogInformation("Il contenuto è identico, il file non è stato modificato: {filePath}", filePath);
                        return;
                    }

                    _logger.LogInformation("Sovrascrivo il file: {filePath}", filePath);
                }
                else
                {
                    _logger.LogInformation("Creo nuovo file: {filePath}", filePath);
                }

                File.WriteAllBytes(filePath, newContent);
                _logger.LogInformation("File scritto con successo: {filePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore nella scrittura del file: {filePath}", filePath);
            }
        }

        private static bool IsFileLocked(string path)
        {
            try
            {
                using FileStream stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private static string ComputeSha256(byte[] data)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(data);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private async Task<string?> GetS3ObjectSha256MetadataAsync(string key)
        {
            try
            {
                var metadataRequest = new GetObjectMetadataRequest
                {
                    BucketName = _bucketName,
                    Key = key
                };

                var metadataResponse = await _s3Client.GetObjectMetadataAsync(metadataRequest);

                string? sha256Value = metadataResponse.Metadata["x-amz-meta-sha256"];
                if (!string.IsNullOrEmpty(sha256Value))
                {
                    return sha256Value;
                }

                return null;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Metadata non trovato in S3 per la chiave: {key}", key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante la lettura metadata da S3 per la chiave: {key}", key);
                return null;
            }
        }

  

        public bool MoveFileToRecycleBin(string filePath)
        {
        try
        {
            if (File.Exists(filePath))
            {
                FileSystem.DeleteFile(
                    filePath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin
                );
                _logger.LogInformation("File spostato nel cestino: {filePath}", filePath);
                    return true;
            }
            else
            {
                _logger.LogWarning("Il file da cancellare non esiste: {filePath}", filePath);
                    return true;
                }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore durante lo spostamento del file nel cestino: {filePath}", filePath);
                return false;
            }
    }


}
}

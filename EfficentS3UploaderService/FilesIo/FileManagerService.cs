using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace EfficentS3UploadService.FilesIo
{
    // This service handles file synchronization between AWS S3 and the local file system.
    internal class FileManagerService
    {
        private readonly ILogger<Worker.Worker> _logger;
        private readonly string _basePath;
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;

        // Constructor initializes required dependencies
        public FileManagerService(ILogger<Worker.Worker> logger, string basePath, IAmazonS3 s3Client, string bucketName)
        {
            _logger = logger;
            _basePath = basePath;
            _s3Client = s3Client;
            _bucketName = bucketName;
        }

        // Processes an MQTT message and downloads the corresponding file from S3 if needed
        public async Task ProcessMessageAndDownloadAsync(string jsonMessage)
        {
            _logger.LogInformation("(ProcessMessageAndDownloadAsync) File to process: {json}", jsonMessage);

            try
            {
                // Deserialize the incoming message
                var payload = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(jsonMessage);
                if (payload == null || !payload.TryGetValue("key", out var encodedKey))
                {
                    _logger.LogWarning("JSON message does not contain 'key': {json}", jsonMessage);
                    return;
                }

                // Decode the S3 key and determine the local file path
                string s3Key = HttpUtility.UrlDecode(encodedKey);
                string localPath = Path.Combine(_basePath, s3Key);

                // Retrieve the SHA256 metadata stored in S3
                string? s3Sha256 = await GetS3ObjectSha256MetadataAsync(s3Key);

                // Check if the local file exists and is already up-to-date
                if (File.Exists(localPath))
                {
                    byte[] localFileBytes = File.ReadAllBytes(localPath);
                    string localSha256 = ComputeSha256(localFileBytes);

                    if (!string.IsNullOrEmpty(s3Sha256) && localSha256 == s3Sha256)
                    {
                        _logger.LogInformation("Local file is already up to date (SHA256 match), skipping download: {localPath}", localPath);
                        return;
                    }
                }

                // Ensure the local directory exists
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

                // Download file from S3
                byte[] s3Content = await DownloadFromS3Async(s3Key);
                if (s3Content == null)
                {
                    _logger.LogWarning("File not found on S3 for key: {s3Key}", s3Key);
                    return;
                }

                // Mark the file as modified by MQTT (for downstream logic)
                FileModificationTracker.MarkAsModifiedByMqtt(localPath);

                // Write the file to the local file system
                SaveOrUpdateFile(localPath, s3Content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing JSON message: {json}", jsonMessage);
            }
        }

        // Downloads a file from S3 and returns it as byte array
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
                _logger.LogWarning("File not found in S3: {key}", key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while downloading from S3: {key}", key);
                return null;
            }
        }

        // Writes or updates the file only if its content has changed
        private void SaveOrUpdateFile(string filePath, byte[] newContent)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    if (IsFileLocked(filePath))
                    {
                        _logger.LogWarning("File is in use and cannot be overwritten: {filePath}", filePath);
                        return;
                    }

                    byte[] existingContent = File.ReadAllBytes(filePath);
                    if (ComputeSha256(existingContent) == ComputeSha256(newContent))
                    {
                        _logger.LogInformation("Content is identical, file was not modified: {filePath}", filePath);
                        return;
                    }

                    _logger.LogInformation("Overwriting file: {filePath}", filePath);
                }
                else
                {
                    _logger.LogInformation("Creating new file: {filePath}", filePath);
                }

                File.WriteAllBytes(filePath, newContent);
                _logger.LogInformation("File written successfully: {filePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing file: {filePath}", filePath);
            }
        }

        // Determines if a file is currently locked by another process
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

        // Computes the SHA256 checksum of the provided byte array
        private static string ComputeSha256(byte[] data)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hashBytes = sha256.ComputeHash(data);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        // Retrieves SHA256 metadata from the S3 object if available
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
                return string.IsNullOrEmpty(sha256Value) ? null : sha256Value;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Metadata not found in S3 for key: {key}", key);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading metadata from S3 for key: {key}", key);
                return null;
            }
        }

        // Moves a file to the system Recycle Bin (Windows only)
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
                    _logger.LogInformation("File moved to Recycle Bin: {filePath}", filePath);
                    return true;
                }
                else
                {
                    _logger.LogWarning("File to delete does not exist: {filePath}", filePath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving file to Recycle Bin: {filePath}", filePath);
                return false;
            }
        }
    }
}

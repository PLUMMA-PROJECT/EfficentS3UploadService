using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using System.Security.Cryptography;

namespace EfficentS3UploadService.S3;

public class S3Uploader
{
    // AWS configuration values (region, credentials, bucket)
    private readonly string _region;
    private readonly string _bucketName;
    private readonly string _accessKey;
    private readonly string _secretKey;

    // Logger instance
    private readonly ILogger<Worker.Worker> _logger;

    // Constructor: loads AWS config and sets logger
    public S3Uploader(IConfiguration config, ILogger<Worker.Worker> logger)
    {
        _logger = logger;
        _logger.LogInformation("S3Uploader initialized...");
        _region = config["AWS:Region"];
        _bucketName = config["AWS:BucketName"];
        _accessKey = config["AWS:AccessKey"];
        _secretKey = config["AWS:SecretKey"];
    }

    // Uploads a local file to S3 with custom SHA256 metadata and optional throttling
    public async Task UploadFileToS3(string filePath, string keyName)
    {
        try
        {
            var regionEndpoint = RegionEndpoint.GetBySystemName(_region);
            var s3Client = new AmazonS3Client(_accessKey, _secretKey, regionEndpoint);
            var fileTransferUtility = new TransferUtility(s3Client);

            // 1. Compute SHA-256 hash of file contents
            string sha256Hash;
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hashBytes = sha256.ComputeHash(stream);
                sha256Hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }

            var uploadRequest = new TransferUtilityUploadRequest
            {
                BucketName = _bucketName,
                FilePath = filePath,
                Key = keyName,
                PartSize = 5 * 1024 * 1024, // 5 MB part size for multipart uploads
                AutoCloseStream = true
            };

            // 2. Attach SHA256 hash as custom metadata
            uploadRequest.Metadata.Add("x-amz-meta-sha256", sha256Hash);

            // Upload progress callback (optional throttling)
            uploadRequest.UploadProgressEvent += (sender, e) =>
            {
                // _logger.LogInformation($"Progress: {e.PercentDone}% - {e.TransferredBytes}/{e.TotalBytes} bytes");

                // Optional: throttle every 10 MB transferred
                if (e.TransferredBytes % (10 * 1024 * 1024) == 0)
                {
                    Thread.Sleep(500); // 500 ms delay
                }
            };

            await fileTransferUtility.UploadAsync(uploadRequest);
            _logger.LogInformation("Upload completed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during S3 upload.");
            throw new Exception("Error during S3 upload", ex);
        }
    }
}

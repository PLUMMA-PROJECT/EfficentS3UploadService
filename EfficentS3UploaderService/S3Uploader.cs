using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;

namespace SampleService;

public class S3Uploader
{
    private readonly string _region;
    private readonly string _bucketName;
    private readonly string _accessKey;
    private readonly string _secretKey;

    private readonly ILogger<S3Uploader> _logger;

    public S3Uploader(IConfiguration config, ILogger<S3Uploader> logger)
    {
        _logger = logger;
        _logger.LogInformation("S3Uploader initialized...");
        _region = config["AWS:Region"];
        _bucketName = config["AWS:BucketName"];
        _accessKey = config["AWS:AccessKey"];
        _secretKey = config["AWS:SecretKey"];
    }

    public async Task UploadFileToS3(string filePath, string keyName)
    {
        try
        {
            var regionEndpoint = RegionEndpoint.GetBySystemName(_region);
            var s3Client = new AmazonS3Client(_accessKey, _secretKey, regionEndpoint);
            var fileTransferUtility = new TransferUtility(s3Client);

            var uploadRequest = new TransferUtilityUploadRequest
            {
                BucketName = _bucketName,
                FilePath = filePath,
                Key = keyName,
                PartSize = 5 * 1024 * 1024, // 5 MB
                AutoCloseStream = true
            };

            uploadRequest.UploadProgressEvent += (sender, e) =>
            {
                //_logger.LogInformation($"Progress: {e.PercentDone}% - {e.TransferredBytes}/{e.TotalBytes} bytes");

                // Optional: introduci ritardi artificiali dopo ogni chunk per ridurre banda
                if (e.TransferredBytes % (10 * 1024 * 1024) == 0) // ogni 10MB
                {
                    Thread.Sleep(500); // throttle di 500ms
                }
            };

            await fileTransferUtility.UploadAsync(uploadRequest);
            _logger.LogInformation("Upload completato.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore durante l'upload su S3.");
        }
    }

}
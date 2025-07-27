using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using System.Security.Cryptography;

namespace EfficentS3UploadService.S3;

public class S3FileManager
{
    // AWS configuration values (region, credentials, bucket)
    private readonly string _region;
    private readonly string _bucketName;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _clientId;
    private static AmazonS3Client _s3Client;
    // Logger instance
    private readonly ILogger<Worker.Worker> _logger;

    // Constructor: loads AWS config and sets logger
    public S3FileManager(IConfiguration config, ILogger<Worker.Worker> logger, string clientId)
    {
        _logger = logger;
        _logger.LogInformation("S3Uploader initialized...");
        _region = config["AWS:Region"];
        _bucketName = config["AWS:BucketName"];
        _accessKey = config["AWS:AccessKey"];
        _secretKey = config["AWS:SecretKey"];
        var regionEndpoint = RegionEndpoint.GetBySystemName(_region);
        _s3Client = new AmazonS3Client(_accessKey, _secretKey, regionEndpoint);
        _clientId = clientId;
    }

    // Uploads a local file to S3 with custom SHA256 metadata and optional throttling
    public async Task UploadFileToS3(string filePath, string keyName)
    {
        try
        {
            var fileTransferUtility = new TransferUtility(_s3Client);

            // Calcola SHA256 locale
            string localSha256;
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hashBytes = sha256.ComputeHash(stream);
                localSha256 = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }

            GetObjectMetadataResponse metadataResponse = null;
            bool objectExists = true;

            try
            {
                metadataResponse = await _s3Client.GetObjectMetadataAsync(_bucketName, keyName);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                objectExists = false;
            }

            if (!objectExists)
            {
                // ✅ Caso 1: file non esiste su S3 → fai upload
                _logger.LogDebug("(UploadFileToS3) Oggetto '{keyName}' non esiste su S3. Upload...", keyName);
                await Upload(filePath, keyName, localSha256, fileTransferUtility);
                return;
            }

            // Caso 2: file esiste su S3 → verifica SHA256 nel metadata
            if (!metadataResponse.Metadata.Keys.Contains("x-amz-meta-sha256"))
            {
                // ❌ Errore se il file esiste ma non ha metadato SHA256
                _logger.LogError("(UploadFileToS3) Oggetto '{keyName}' esiste ma manca 'x-amz-meta-sha256'. Upload vietato.", keyName);
                throw new InvalidOperationException($"File '{keyName}' su S3 non contiene metadato SHA256.");
            }

            string remoteSha256 = metadataResponse.Metadata["x-amz-meta-sha256"];

            if (remoteSha256 == localSha256)
            {
                // ✅ Caso 3: hash uguale → skip upload
                _logger.LogDebug("SHA256 identico per '{keyName}'. Nessun upload necessario.", keyName);
                return;
            }

            // Caso 4: hash diverso → confronta data
            var s3LastModified = metadataResponse.LastModified;
            var localLastModified = File.GetLastWriteTime(filePath).ToUniversalTime();

            if (localLastModified > s3LastModified)
            {
                _logger.LogDebug("SHA256 diverso e file locale più recente. Upload...");
                await Upload(filePath, keyName, localSha256, fileTransferUtility);
            }
            else
            {
                _logger.LogDebug("SHA256 diverso ma file locale NON più recente. Skip upload.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore durante l'upload su S3.");
            throw;
        }
    }

    private async Task Upload(string filePath, string keyName, string sha256Hash, TransferUtility fileTransferUtility)
    {
        var uploadRequest = new TransferUtilityUploadRequest
        {
            BucketName = _bucketName,
            FilePath = filePath,
            Key = keyName,
            PartSize = 5 * 1024 * 1024,
            AutoCloseStream = true
        };

        uploadRequest.Metadata.Add("sha256", sha256Hash);
        uploadRequest.Metadata.Add("mqttclientid", this._clientId);

        uploadRequest.UploadProgressEvent += (sender, e) =>
        {
            if (e.TransferredBytes % (10 * 1024 * 1024) == 0)
            {
                Thread.Sleep(500);
            }
        };

        await fileTransferUtility.UploadAsync(uploadRequest);
        _logger.LogInformation("(Upload) Upload completato per '{keyName}' - client id  {clientId}.", keyName,this._clientId);
    }




}

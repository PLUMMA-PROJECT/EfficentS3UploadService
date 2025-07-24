using Amazon.S3;
using EfficentS3UploadSerivice;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace EfficentS3UploadService
{
    internal class IotCoreViaWebsocket
    {
        // Logger instance for logging information and errors
        private readonly ILogger<Worker> _logger;

        // MQTT configuration variables
        private string _mqtt_region;
        private string _mqtt_endpoint;
        private string _mqtt_accessKey;
        private string _mqtt_secretKey;
        private string _pathToWatch;
        private string _bucketName;

        // MQTT client and options
        private IMqttClient _mqttClient;
        private MqttClientOptions _mqttClientOptions;

        // Unique client identifier for MQTT connection
        private string _clientId;

        public IotCoreViaWebsocket(IConfiguration config, ILogger<Worker> logger)
        {
            _logger = logger;
            // Load MQTT and AWS configuration from app settings
            _mqtt_region = config["AWS:MQTT_region"];
            _mqtt_endpoint = config["AWS:MQTT_endpoint"];
            _mqtt_accessKey = config["AWS:AccessKey"];
            _mqtt_secretKey = config["AWS:SecretKey"];
            // Generate unique client ID for this connection instance
            _clientId = Guid.NewGuid().ToString();
            // Path to watch for file changes
            _pathToWatch = config["FOLDER:Path"];
            // S3 bucket name for uploads
            _bucketName = config["AWS:BucketName"];

            _logger.LogInformation("WSS MQTT AWS IoT Core listener initialized : region  {region} endpoint {endpoint}", _mqtt_region, _mqtt_endpoint);
        }

        public async Task ConnectAndSubscribeAsync()
        {
            // Generate a signed WebSocket URL with AWS SigV4 authentication for MQTT connection
            string wsUrl = AwsSigV4Signer.CreatePresignedUrl(_mqtt_accessKey, _mqtt_secretKey, _mqtt_region, _mqtt_endpoint);

            _logger.LogInformation("Try to connect to : {url}", wsUrl);

            var mqttFactory = new MqttFactory();
            _mqttClient = mqttFactory.CreateMqttClient();

            // Build MQTT client options for WebSocket connection using MQTT 3.1.1 protocol
            _mqttClientOptions = new MqttClientOptionsBuilder()
                .WithWebSocketServer(wsUrl)  // <-- Full WSS URL with path and query
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithClientId(_clientId)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                .WithCleanSession(false) // Typical for WebSocket connections (non-persistent session)
                .WithTimeout(TimeSpan.FromSeconds(15))
                .WithoutPacketFragmentation()
                .Build();

            _logger.LogInformation("Starting connection to broker with ClientId {clientId}", _clientId);

            // Event handler when MQTT client successfully connects
            _mqttClient.ConnectedAsync += async e =>
            {
                _logger.LogInformation("Connected to AWS IoT Core! Broker: {endpoint}", _mqtt_endpoint);
                // Subscribe to the topic where file update notifications arrive
                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("EfficentS3UploadService/update")
                    .Build());

                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("EfficentS3UploadService/delete")
                    .Build());
                // Publish an online status message after connection
                await this.PublishOnlineMessage();
                await this.PublishQueuedDeletesAsync();
                await Worker.PublishQueuedNewfilesAsync();
                _logger.LogInformation("Subscribed to topic 'EfficentS3UploadService/update' and published online message.");






            };

            // Event handler for receiving MQTT application messages
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                // Decode the message payload from MQTT message
                string message = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                string topic = e.ApplicationMessage.Topic;
                _logger.LogInformation("Received new or updated file message: {message}", message);
                _logger.LogInformation("Verifying if file is savable under path: {path}", _pathToWatch + message);
                
                switch (topic)
                {
                    case "EfficentS3UploadService/update":
                        // Create Amazon S3 client and file manager to handle download and processing
                        var s3Client = new AmazonS3Client(_mqtt_accessKey, _mqtt_secretKey, Amazon.RegionEndpoint.GetBySystemName(_mqtt_region));
                        var fileManager = new FileManagerService(_logger, _pathToWatch, s3Client, _bucketName);
                
                        // Process the incoming message and download file if needed
                        await fileManager.ProcessMessageAndDownloadAsync(message);
                        break;
                    case "EfficentS3UploadService/delete":
                        _logger.LogInformation("Received new request to delete file message: {message}", message);
                        await HandleDeleteMessage(message);
                        break;

                    default:
                        _logger.LogWarning("Received message on unhandled topic: {topic}", topic);
                        break;
                }
            };

            // Event handler for MQTT client disconnection
            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogInformation("Disconnected from broker! Reason: {reason}, Exception: {exception}", e.Reason, e.Exception);
                // Wait 5 seconds before attempting reconnect
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    // Recreate the signed WebSocket URL and MQTT client options for reconnection
                    string wsUrl = AwsSigV4Signer.CreatePresignedUrl(_mqtt_accessKey, _mqtt_secretKey, _mqtt_region, _mqtt_endpoint);
                    _mqttClientOptions = new MqttClientOptionsBuilder()
                       .WithWebSocketServer(wsUrl)  // <-- Full WSS URL with path and query
                       .WithProtocolVersion(MqttProtocolVersion.V311)
                       .WithClientId(_clientId)
                       .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                       .WithCleanSession(false) // Typical for WebSocket connections (non-persistent session)
                       .WithTimeout(TimeSpan.FromSeconds(15))
                       .WithoutPacketFragmentation()
                       .Build();

                    // Attempt to reconnect
                    await _mqttClient.ConnectAsync(_mqttClientOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation($"Reconnection error: {ex.Message}");
                }
            };

            try
            {
                // Use a cancellation token to limit connection timeout to 30 seconds
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _mqttClient.ConnectAsync(_mqttClientOptions, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timeout or error during MQTT connection");
            }
        }

        // Publish a message to indicate the client is online
        public async Task PublishOnlineMessage()
        {
            try
            {
                _logger.LogInformation("(PublishOnlineMessage) Sending online signal message to endpoint {endpoint}", _mqtt_endpoint);
                if (_mqttClient.IsConnected)
                {
                    // Prepare message payload with client ID and current timestamp
                    var messagePayload = JsonSerializer.Serialize(new
                    {
                        clientId = _clientId,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });

                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic($"EfficentS3UploadService/online/{_clientId}")
                        .WithPayload(messagePayload)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(false)
                        .Build();

                    _logger.LogInformation("(PublishOnlineMessage) Check if client IsConnected before publish online signal: {connected}", _mqttClient.IsConnected);
                    try
                    {
                        // Publish the online status message to MQTT broker
                        await _mqttClient.PublishAsync(message);
                        _logger.LogInformation("(PublishOnlineMessage) Signal message published");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "(PublishOnlineMessage) Error during PublishAsync");
                    }
                    _logger.LogInformation("(PublishOnlineMessage) PublishAsync called");
                }
                else
                {
                    _logger.LogWarning("(PublishOnlineMessage) MQTT client is not connected at publish time.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex.Message);
            }
        }
 

        public async Task PublishDeleteMessage(string key)
        {
            _logger.LogInformation("Sending delete message to endpoint {endpoint}", _mqtt_endpoint);

            var messagePayload = JsonSerializer.Serialize(new
            {
                clientId = _clientId,
                key = key
            });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic("EfficentS3UploadService/delete")
                .WithPayload(messagePayload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            try
            {
                if (_mqttClient.IsConnected)
                {
                    await _mqttClient.PublishAsync(message);
                    _logger.LogInformation("Deleted message published: {key}", key);
                }
                else
                {
                    throw new InvalidOperationException("MQTT client is disconnected.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT publish failed. Queuing delete message: {key}", messagePayload);            
                throw new InvalidOperationException("MQTT publish failed. Queuing delete message: "+ messagePayload);
            }
        }




        public async Task PublishQueuedDeletesAsync()
        {
            if (!File.Exists(Worker.DeleteQueueFile)) return;

            var lines = File.ReadAllLines(Worker.DeleteQueueFile).ToList();
            var remaining = new List<string>();

            foreach (var key in lines)
            {
                try
                {
                    string relativeKey = Path.GetRelativePath(_pathToWatch, key)
                            .Replace("\\", "/");
                    await this.PublishDeleteMessage(relativeKey);
                    _logger.LogInformation("(PublishQueuedDeletesAsync) Republished delete message: {key}", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "(PublishQueuedDeletesAsync) Retry failed for: {key}", key);
                    remaining.Add(key);
                }
            }

            File.WriteAllLines(Worker.DeleteQueueFile, remaining);
        }

        private async Task HandleDeleteMessage(string messagePayload)
        {
            try
            {
                _logger.LogInformation("Handling delete message: {payload}", messagePayload);

                // Parse JSON per ottenere la key da cancellare
                var jsonDoc = JsonDocument.Parse(messagePayload);
                if (!jsonDoc.RootElement.TryGetProperty("key", out var keyElement))
                {
                    _logger.LogWarning("Delete message JSON does not contain 'key' property.");
                    return;
                }

                string relativeKey = keyElement.GetString();
                if (string.IsNullOrEmpty(relativeKey))
                {
                    _logger.LogWarning("Delete message 'key' is null or empty.");
                    return;
                }

                // Costruisci il percorso assoluto del file da cancellare
                string fileToDelete = Path.Combine(_pathToWatch, relativeKey.Replace('/', Path.DirectorySeparatorChar));

                // Crea il client S3 e file manager
                var s3Client = new AmazonS3Client(_mqtt_accessKey, _mqtt_secretKey, Amazon.RegionEndpoint.GetBySystemName(_mqtt_region));
                var fileManager = new FileManagerService(_logger, _pathToWatch, s3Client, _bucketName);

                // Usa il metodo MoveFileToRecycleBin invece di cancellare direttamente
                bool movedToRecycleBin = fileManager.MoveFileToRecycleBin(fileToDelete);

                if (movedToRecycleBin)
                {
                    _logger.LogInformation("File moved to recycle bin: {file}", fileToDelete);
                }
                else
                {
                    _logger.LogWarning("File could not be moved to recycle bin or does not exist: {file}", fileToDelete);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling delete message.");

                // In caso di errore, puoi accodare il messaggio per retry futuro
                try
                {
                    string fileKey = null;
                    var jsonDoc = JsonDocument.Parse(messagePayload);
                    if (jsonDoc.RootElement.TryGetProperty("key", out var keyElement))
                    {
                        fileKey = keyElement.GetString();
                    }

                    if (!string.IsNullOrEmpty(fileKey))
                    {
                        string fullPath = Path.Combine(_pathToWatch, fileKey.Replace('/', Path.DirectorySeparatorChar));
                        await File.AppendAllLinesAsync(Worker.DeleteQueueFile, new[] { fullPath });
                        _logger.LogInformation("Queued delete file for retry: {file}", fullPath);
                    }
                }
                catch (Exception queueEx)
                {
                    _logger.LogError(queueEx, "Failed to queue delete message for retry.");
                }
            }
        }

    }
}

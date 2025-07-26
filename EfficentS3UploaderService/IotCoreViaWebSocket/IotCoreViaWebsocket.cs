using Amazon.S3;
using EfficentS3UploadService.Helpers;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace EfficentS3UploadService
{
    // This class handles AWS IoT Core MQTT communication over WebSocket,
    // listens to update/delete events, and manages file operations with S3.
    internal class IotCoreViaWebsocket
    {
        // Logger instance for application diagnostics
        private readonly ILogger<Worker.Worker> _logger;

        // MQTT and AWS configuration values
        private string _mqtt_region;
        private string _mqtt_endpoint;
        private string _mqtt_accessKey;
        private string _mqtt_secretKey;
        private string _pathToWatch;
        private string _bucketName;
  

        // MQTT client and its connection options
        private IMqttClient _mqttClient;
        private MqttClientOptions _mqttClientOptions;

        // Unique MQTT client identifier
        private string _clientId;

        // Constructor that receives configuration and logger
        public IotCoreViaWebsocket(IConfiguration config, ILogger<Worker.Worker> logger)
        {
            _logger = logger;

            // Load settings from configuration
            _mqtt_region = config["AWS:MQTT_region"];
            _mqtt_endpoint = config["AWS:MQTT_endpoint"];
            _mqtt_accessKey = config["AWS:AccessKey"];
            _mqtt_secretKey = config["AWS:SecretKey"];
            _clientId = Guid.NewGuid().ToString();
            _pathToWatch = config["FOLDER:Path"];
            _bucketName = config["AWS:BucketName"];


            _logger.LogInformation("WSS MQTT AWS IoT Core listener initialized : region  {region} endpoint {endpoint}", _mqtt_region, _mqtt_endpoint);
        }

        // Establishes connection to AWS IoT Core and subscribes to topics
        public async Task ConnectAndSubscribeAsync()
        {
            // Create signed WebSocket URL using AWS SigV4 signing
            string wsUrl = AwsSigV4Signer.CreatePresignedUrl(_mqtt_accessKey, _mqtt_secretKey, _mqtt_region, _mqtt_endpoint);
            _logger.LogInformation("Try to connect to : {url}", wsUrl);

            var mqttFactory = new MqttFactory();
            _mqttClient = mqttFactory.CreateMqttClient();

            // Set MQTT client options for WebSocket connection
            _mqttClientOptions = new MqttClientOptionsBuilder()
                .WithWebSocketServer(wsUrl)
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithClientId(_clientId)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                .WithCleanSession(false)
                .WithTimeout(TimeSpan.FromSeconds(15))
                .WithoutPacketFragmentation()
                .Build();

            _logger.LogInformation("Starting connection to broker with ClientId {clientId}", _clientId);

            // Event triggered after successful connection
            _mqttClient.ConnectedAsync += async e =>
            {
                _logger.LogInformation("Connected to AWS IoT Core! Broker: {endpoint}", _mqtt_endpoint);

                // Subscribe to update and delete topics
                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("EfficentS3UploadService/update").Build());
                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("EfficentS3UploadService/delete").Build());

                // Publish "online" message and any pending operations
                await this.PublishOnlineMessage();
                await this.PublishQueuedDeletesAsync();
                await Worker.Worker.PublishQueuedNewfilesAsync();

                _logger.LogInformation("Subscribed to topic 'EfficentS3UploadService/update' and published online message.");
            };

            // Event triggered when receiving MQTT messages
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string message = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                string topic = e.ApplicationMessage.Topic;

                _logger.LogInformation("Received new or updated file message: {message}", message);
                _logger.LogInformation("Verifying if file is savable under path: {path}", _pathToWatch + message);

                switch (topic)
                {
                    case "EfficentS3UploadService/update":
                        // Handle update message: download and store the file from S3
                        var s3Client = new AmazonS3Client(_mqtt_accessKey, _mqtt_secretKey, Amazon.RegionEndpoint.GetBySystemName(_mqtt_region));
                        var fileManager = new FilesIo.FileManagerService(_logger, _pathToWatch, s3Client, _bucketName);
                        await fileManager.ProcessMessageAndDownloadAsync(message);
                        break;

                    case "EfficentS3UploadService/delete":
                        // Handle delete message
                        _logger.LogInformation("Received new request to delete file message: {message}", message);
                        await HandleDeleteMessage(message);
                        break;

                    default:
                        _logger.LogWarning("Received message on unhandled topic: {topic}", topic);
                        break;
                }
            };

            // Event triggered on disconnection
            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogInformation("Disconnected from broker! Reason: {reason}, Exception: {exception}", e.Reason, e.Exception);

                // Wait before reconnecting
                await Task.Delay(TimeSpan.FromSeconds(5));

                try
                {
                    // Rebuild connection URL and options
                    string wsUrl = AwsSigV4Signer.CreatePresignedUrl(_mqtt_accessKey, _mqtt_secretKey, _mqtt_region, _mqtt_endpoint);
                    _mqttClientOptions = new MqttClientOptionsBuilder()
                        .WithWebSocketServer(wsUrl)
                        .WithProtocolVersion(MqttProtocolVersion.V311)
                        .WithClientId(_clientId)
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                        .WithCleanSession(false)
                        .WithTimeout(TimeSpan.FromSeconds(15))
                        .WithoutPacketFragmentation()
                        .Build();

                    await _mqttClient.ConnectAsync(_mqttClientOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation($"Reconnection error: {ex.Message}");
                }
            };

            // Initial connection with timeout
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _mqttClient.ConnectAsync(_mqttClientOptions, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timeout or error during MQTT connection");
            }
        }

        // Publishes a message signaling the client is online
        public async Task PublishOnlineMessage()
        {
            try
            {
                _logger.LogInformation("(PublishOnlineMessage) Sending online signal message to endpoint {endpoint}", _mqtt_endpoint);

                if (_mqttClient.IsConnected)
                {
                      long _lastOnlineTimestamp=0;
                   var status = PersistentStatusHelper.LoadStatus();
                    if (status != null)
                    {
                        _lastOnlineTimestamp = status.LastOnlineTimestamp;
                    }

                    // Se non esiste timestamp salvato, allora è la prima volta
                    if (_lastOnlineTimestamp == 0)
                    {
                        _lastOnlineTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        PersistentStatusHelper.SaveStatus(_clientId, _lastOnlineTimestamp);
                        _logger.LogInformation("First-time online signal, saving timestamp {ts}", _lastOnlineTimestamp);
                    }

                    // Crea il payload con il timestamp persistente
                    var messagePayload = JsonSerializer.Serialize(new
                    {
                        clientId = _clientId,
                        timestamp = _lastOnlineTimestamp
                    });

                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic($"EfficentS3UploadService/online/{_clientId}")
                        .WithPayload(messagePayload)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(false)
                        .Build();

                    await _mqttClient.PublishAsync(message);
                    _logger.LogInformation("(PublishOnlineMessage) Signal message published");
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

        // Publishes a delete command to the MQTT broker
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
                throw new InvalidOperationException("MQTT publish failed. Queuing delete message: " + messagePayload);
            }
        }

        // Re-publishes delete messages stored in a queue file
        public async Task PublishQueuedDeletesAsync()
        {
            if (!File.Exists(Worker.Worker.DeleteQueueFile)) return;

            List<string> lines =  PersistentQueueHelper.ReadItemList(Worker.Worker.DeleteQueueFile);            
            var remaining = new List<string>();

            foreach (string key in lines)
            {
                try
                {
                    string relativeKey = Path.GetRelativePath(_pathToWatch, key).Replace("\\", "/");
                    await this.PublishDeleteMessage(relativeKey);
                    _logger.LogInformation("(PublishQueuedDeletesAsync) Republished delete message: {key}", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "(PublishQueuedDeletesAsync) Retry failed for: {key}", key);
                    remaining.Add(key);
                }
            }

            PersistentQueueHelper.WriteItemList(Worker.Worker.DeleteQueueFile, remaining);
        }

        // Handles a delete message by removing or moving the file
        private async Task HandleDeleteMessage(string messagePayload)
        {
            try
            {
                _logger.LogInformation("Handling delete message: {payload}", messagePayload);

                var jsonDoc = JsonDocument.Parse(messagePayload);
                if (!jsonDoc.RootElement.TryGetProperty("key", out var keyElement)) return;

                string relativeKey = keyElement.GetString();
                if (string.IsNullOrEmpty(relativeKey)) return;

                string fileToDelete = Path.Combine(_pathToWatch, relativeKey.Replace('/', Path.DirectorySeparatorChar));

                var s3Client = new AmazonS3Client(_mqtt_accessKey, _mqtt_secretKey, Amazon.RegionEndpoint.GetBySystemName(_mqtt_region));
                var fileManager = new FilesIo.FileManagerService(_logger, _pathToWatch, s3Client, _bucketName);

                bool movedToRecycleBin = fileManager.MoveFileToRecycleBin(fileToDelete);

                if (movedToRecycleBin)
                    _logger.LogInformation("File moved to recycle bin: {file}", fileToDelete);
                else
                    _logger.LogWarning("File could not be moved to recycle bin or does not exist: {file}", fileToDelete);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling delete message.");

                try
                {
                    string fileKey = null;
                    var jsonDoc = JsonDocument.Parse(messagePayload);
                    if (jsonDoc.RootElement.TryGetProperty("key", out var keyElement))
                        fileKey = keyElement.GetString();

                    if (!string.IsNullOrEmpty(fileKey))
                    {
                        string fullPath = Path.Combine(_pathToWatch, fileKey.Replace('/', Path.DirectorySeparatorChar));
                        await File.AppendAllLinesAsync(Worker.Worker.DeleteQueueFile, new[] { fullPath });
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

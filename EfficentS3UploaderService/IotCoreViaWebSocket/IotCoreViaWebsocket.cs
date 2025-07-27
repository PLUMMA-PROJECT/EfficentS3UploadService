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

        public string ClientId => _clientId;


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
                
                

                // Publish "online" message and any pending operations
                await this.PublishOnlineMessage();
                await this.PublishQueuedDeletesAsync();
                await this.PublishQueuedRenamesAsync();
                await Worker.Worker.PublishQueuedNewfilesAsync();
                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("EfficentS3UploadService/update").Build());
                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("EfficentS3UploadService/delete").Build());
                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("EfficentS3UploadService/rename").Build());
                _logger.LogInformation("Subscribed to topics and published online message.");
            };

            // Event triggered when receiving MQTT messages
            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string message = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);                
                string topic = e.ApplicationMessage.Topic;
                var helper = new MessageHelper(message);

                string clientId = helper.GetValue("mqttclientid");
                if (clientId == _clientId)
                {
                    _logger.LogDebug("Received message from same client {clientId}, ignoring", clientId);
                    return; // Ignore messages from other clients
                }

                _logger.LogInformation("MQTT Received new message: {message} in topic {topic}", message,topic);                
                
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
                        _logger.LogDebug("Received new request to delete file message: {message}", message);
                        await HandleDeleteMessage(message);
                        break;
                    case "EfficentS3UploadService/rename":
                        // Handle delete message
                        _logger.LogDebug("Received new request to rename file message: {message}", message);
                        await HandleRenameMessage(message);
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
                _logger.LogDebug("(PublishOnlineMessage) Sending online signal message to endpoint {endpoint}", _mqtt_endpoint);

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
                        _logger.LogInformation("(PublishOnlineMessage) First-time online signal, saving timestamp {ts}", _lastOnlineTimestamp);
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

                    _logger.LogDebug("(PublishOnlineMessage) Signal message published");
                    _lastOnlineTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    PersistentStatusHelper.SaveStatus(_clientId, _lastOnlineTimestamp);
                    _logger.LogDebug("Saving last online timestamp {ts}", _lastOnlineTimestamp);
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

            
                _logger.LogInformation("(PublishDeleteMessage ) Sending delete message to endpoint {endpoint}", _mqtt_endpoint);

                var messagePayload = JsonSerializer.Serialize(new
                {
                    mqttclientid = _clientId,
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
                    PersistentStatusHelper.SaveStatus(_clientId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    _logger.LogDebug("(PublishDeleteMessage) Deleted message published: {key}", key);

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
        


        // Publishes a rename file command to the MQTT broker
        public async Task PublishRenameMessage(string oldKey,string newKey, long _timestamp)
        {
            _logger.LogInformation("(PublishRenameMessage)  Sending rename file message to endpoint {endpoint}", _mqtt_endpoint);

            var messagePayload = JsonSerializer.Serialize(new
            {
                mqttclientid = _clientId,
                old_key = oldKey,
                new_key = newKey,
                timestamp = _timestamp
            });

            var message = new MqttApplicationMessageBuilder()
                .WithTopic("EfficentS3UploadService/rename")
                .WithPayload(messagePayload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)
                .Build();

            try
            {
                if (_mqttClient.IsConnected)
                {
                    await _mqttClient.PublishAsync(message);
                    _logger.LogDebug("(PublishRenameMessage) Rename file message published: {oldkey} > {newkey}", oldKey,newKey);
                    PersistentStatusHelper.SaveStatus(_clientId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                else
                {
                    throw new InvalidOperationException("MQTT client is disconnected.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT publish failed. Queuing rename file message: {key}", messagePayload);
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
                    _logger.LogDebug("(PublishQueuedDeletesAsync) Republished delete message: {key}", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "(PublishQueuedDeletesAsync) Retry failed for: {key}", key);
                    remaining.Add(key);
                }
            }

            PersistentQueueHelper.WriteItemList(Worker.Worker.DeleteQueueFile, remaining);
        }

        // Re-publishes delete messages stored in a queue file
        public async Task PublishQueuedRenamesAsync()
        {
            if (!File.Exists(Worker.Worker.RenameFileQueue)) return;

            List<PersistentQueueHelper.QueueEntry> lines = PersistentQueueHelper.ReadQueue(Worker.Worker.RenameFileQueue);
            var remaining = new List<PersistentQueueHelper.QueueEntry>();

            foreach (PersistentQueueHelper.QueueEntry key in lines)
            {
                try
                {
                    string oldKey = Path.GetRelativePath(_pathToWatch, key.Item).Split(">")[0].Replace("\\", "/");
                    string newKey = Path.GetRelativePath(_pathToWatch, key.Item).Split(">")[1].Replace("\\", "/");
                    long timestamp = new DateTimeOffset(key.Timestamp.ToUniversalTime()).ToUnixTimeSeconds();
                    await this.PublishRenameMessage(oldKey,newKey,timestamp);
                    _logger.LogDebug("(PublishQueuedRenamesAsync) Republished renaming file message: {key}", key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "(PublishQueuedRenamesAsync) Retry failed for: {key}", key);
                    remaining.Add(key);
                }
            }

            PersistentQueueHelper.WriteQueue(Worker.Worker.RenameFileQueue, remaining);
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
                    _logger.LogInformation("(HandleDeleteMessage) File moved to recycle bin: {file}", fileToDelete);
                else
                    _logger.LogWarning("(HandleDeleteMessage) File could not be moved to recycle bin or does not exist: {file}", fileToDelete);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "(HandleDeleteMessage) Error handling delete message.");

                try
                {

                 
                    string fileKey = null;
                    var jsonDoc = JsonDocument.Parse(messagePayload);
                    if (jsonDoc.RootElement.TryGetProperty("key", out var keyElement))
                        fileKey = keyElement.GetString();

                    if (!string.IsNullOrEmpty(fileKey))
                    {
                        string fullPath = Path.Combine(_pathToWatch, fileKey.Replace('/', Path.DirectorySeparatorChar));
                        PersistentQueueHelper.EnqueueToJsonFile(Worker.Worker.DeleteQueueFile, fullPath);
                        _logger.LogInformation("(HandleDeleteMessage) Queued delete file for retry: {file}", fullPath);
                    }
                }
                catch (Exception queueEx)
                {
                    _logger.LogError(queueEx, "(HandleDeleteMessage) Failed to queue delete message for retry.");
                }
            }
        }



        // Handles a delete message by removing or moving the file
        private async Task HandleRenameMessage(string messagePayload)
        {
            try
            {
                _logger.LogInformation("(HandleRenameMessage) Handling rename message: {payload}", messagePayload);

                var jsonDoc = JsonDocument.Parse(messagePayload);
                if (!jsonDoc.RootElement.TryGetProperty("old_key", out var oldKeyElement)) return;
                if (!jsonDoc.RootElement.TryGetProperty("new_key", out var newKeyElement)) return;
                if (!jsonDoc.RootElement.TryGetProperty("timestamp", out var timestampElement)) return;

                string oldKey = oldKeyElement.GetString();
                if (string.IsNullOrEmpty(oldKey)) return;

                string newKey = newKeyElement.GetString();
                if (string.IsNullOrEmpty(newKey)) return;

                if (!timestampElement.TryGetInt64(out long timestamp)) return;
                DateTime utcDateTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;

                string fileToRename = Path.Combine(_pathToWatch, oldKey.Replace('/', Path.DirectorySeparatorChar));
                string newFileToHave = Path.Combine(_pathToWatch, newKey.Replace('/', Path.DirectorySeparatorChar));

                var s3Client = new AmazonS3Client(_mqtt_accessKey, _mqtt_secretKey, Amazon.RegionEndpoint.GetBySystemName(_mqtt_region));
                var fileManager = new FilesIo.FileManagerService(_logger, _pathToWatch, s3Client, _bucketName);

                bool renamed = fileManager.RenameFileIfNewer(fileToRename, newFileToHave, utcDateTime);

                if (renamed)
                    _logger.LogInformation("File {file} renamed into {newkey}", fileToRename,newKey);
                else
                    _logger.LogWarning("File could not be renamed or does not exist: {file}", fileToRename);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling renaming message.");
                try
                {
                    var jsonDoc = JsonDocument.Parse(messagePayload);
                    if (!jsonDoc.RootElement.TryGetProperty("old_key", out var oldKeyElement)) return;
                    if (!jsonDoc.RootElement.TryGetProperty("new_key", out var newKeyElement)) return;
                    if (!jsonDoc.RootElement.TryGetProperty("timestamp", out var timestampElement)) return;

                    string oldKey = oldKeyElement.GetString();
                    if (string.IsNullOrEmpty(oldKey)) return;

                    string newKey = newKeyElement.GetString();
                    if (string.IsNullOrEmpty(newKey)) return;

                    if (!timestampElement.TryGetInt64(out long timestamp)) return;
                    string fileToRename = Path.Combine(_pathToWatch, oldKey.Replace('/', Path.DirectorySeparatorChar));
                    string newFileToHave = Path.Combine(_pathToWatch, newKey.Replace('/', Path.DirectorySeparatorChar));
                
                    PersistentQueueHelper.EnqueueToJsonFile(Worker.Worker.RenameFileQueue, fileToRename+">"+newFileToHave);
                    _logger.LogInformation("Queued file renaming for retry: {file}", fileToRename + ">" + newFileToHave);

                }
                catch (Exception queueEx)
                {
                    _logger.LogError(queueEx, "Failed to queue renaming message for retry.");
                }
            }
        }
    }
}

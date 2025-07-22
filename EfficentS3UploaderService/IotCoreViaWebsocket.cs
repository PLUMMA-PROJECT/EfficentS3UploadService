using Amazon.S3;
using EfficentS3UploadSerivice;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;
using System.Text;
using System.Text.Json;

namespace EfficentS3UploadService
{
    internal class IotCoreViaWebsocket
    {

        private readonly ILogger<Worker> _logger;

        private string _mqtt_region;
        private string _mqtt_endpoint;
        private string _mqtt_accessKey;
        private string _mqtt_secretKey;
        private string _pathToWatch;
        private string _bucketName;
        private IMqttClient _mqttClient;
        private MqttClientOptions _mqttClientOptions;
        private string _clientId;

        public IotCoreViaWebsocket(IConfiguration config, ILogger<Worker> logger)
        {
            _logger = logger;            
            _mqtt_region = config["AWS:MQTT_region"];
            _mqtt_endpoint = config["AWS:MQTT_endpoint"];
            _mqtt_accessKey = config["AWS:AccessKey"]; 
            _mqtt_secretKey = config["AWS:SecretKey"];
            _clientId = Guid.NewGuid().ToString();
            _pathToWatch = config["FOLDER:Path"];
            _bucketName = config["AWS:BucketName"];
            _logger.LogInformation("WSS MQTT AWS IoT Core listener initialized : region  {region} endpoint {endpoint}",_mqtt_region,_mqtt_endpoint);
        }


        public async Task ConnectAndSubscribeAsync()
        {
            string wsUrl = AwsSigV4Signer.CreatePresignedUrl(_mqtt_accessKey, _mqtt_secretKey, _mqtt_region, _mqtt_endpoint);
           
            _logger.LogInformation("Try to connect to : {url}", wsUrl);

            var mqttFactory = new MqttFactory();            
            _mqttClient = mqttFactory.CreateMqttClient();

  

            _mqttClientOptions = new MqttClientOptionsBuilder()
                .WithWebSocketServer(wsUrl)  // <-- URL WSS completo con path e query
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithClientId(_clientId)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                .WithCleanSession(false) // Tipico per connessioni via WebSocket (senza sessione persistente)
                .WithTimeout(TimeSpan.FromSeconds(15))             
                .WithoutPacketFragmentation()
                .Build();
           


            _logger.LogInformation("Starting connection to broker with ClientId {clientId}", _clientId);
            _mqttClient.ConnectedAsync += async e =>
            {
                _logger.LogInformation("Connesso ad AWS IoT Core! al broker {endpoint}", _mqtt_endpoint);
                await _mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("EfficentS3UploadService/update")
                    .Build());
                await this.PublishOnlineMessage();
            };

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string message = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                _logger.LogInformation("Messaggio di nuovo file o file cambiato ricevuto: {message}",message);
                _logger.LogInformation("Verifichiamo che sia salvabile in questa path: {message}", _pathToWatch+message);

                var s3Client = new AmazonS3Client(_mqtt_accessKey, _mqtt_secretKey, Amazon.RegionEndpoint.GetBySystemName(_mqtt_region));
                var fileManager = new FileManagerService(_logger,_pathToWatch, s3Client, _bucketName);
               
                
                await fileManager.ProcessMessageAndDownloadAsync(message);
                
            };

          

            _mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogInformation("Disconnesso dal broker! Reason: {reason}, Exception: {exception}", e.Reason, e.Exception);
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    string wsUrl = AwsSigV4Signer.CreatePresignedUrl(_mqtt_accessKey, _mqtt_secretKey, _mqtt_region, _mqtt_endpoint);
                    _mqttClientOptions = new MqttClientOptionsBuilder()
                       .WithWebSocketServer(wsUrl)  // <-- URL WSS completo con path e query
                       .WithProtocolVersion(MqttProtocolVersion.V311)
                       .WithClientId(_clientId)
                       .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                       .WithCleanSession(false) // Tipico per connessioni via WebSocket (senza sessione persistente)
                       .WithTimeout(TimeSpan.FromSeconds(15))
                       .WithoutPacketFragmentation()
                       .Build();

                    await _mqttClient.ConnectAsync(_mqttClientOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation($"Errore di riconnessione: {ex.Message}");
                }
            };


            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await _mqttClient.ConnectAsync(_mqttClientOptions, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timeout o errore durante connessione MQTT");
            }
        }

        public async Task PublishOnlineMessage()
        {
            try
            {
                _logger.LogInformation("Mando messaggio back on line {endpoint}", _mqtt_endpoint);
                if (_mqttClient.IsConnected)
                {
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

                    _logger.LogInformation("IsConnected before publish: {connected}", _mqttClient.IsConnected);
                    try
                    {
                        await _mqttClient.PublishAsync(message);
                        _logger.LogInformation("Messaggio pubblicato");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Errore durante PublishAsync");
                    }
                    _logger.LogInformation("PublishAsync chiamato");
                }
                else
                {
                    _logger.LogWarning("Client MQTT non è connesso al momento della pubblicazione.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex.Message);
            }
        }
    }
}

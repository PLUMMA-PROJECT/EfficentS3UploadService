using EfficentS3UploadSerivice;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using System.Text;

namespace EfficentS3UploadService
{
    internal class IotCoreViaWebsocket
    {

        private readonly ILogger<Worker> _logger;

        private string _mqtt_region;
        private string _mqtt_endpoint;
        private string _mqtt_accessKey;
        private string _mqtt_secretKey;

        public IotCoreViaWebsocket(IConfiguration config, ILogger<Worker> logger)
        {
            _logger = logger;            
            _mqtt_region = config["AWS:MQTT_region"];
            _mqtt_endpoint = config["AWS:MQTT_endpoint"];
            _mqtt_accessKey = config["AWS:AccessKey"]; ;
            _mqtt_secretKey = config["AWS:SecretKey"]; ;            
            _logger.LogInformation("WSS MQTT AWS IoT Core listener initialized : region  {region} endpoint {endpoint}",_mqtt_region,_mqtt_endpoint);
        }


        public async Task ConnectAndSubscribeAsync()
        {
            string wsUrl = AwsSigV4Signer.CreatePresignedUrl(_mqtt_accessKey, _mqtt_secretKey, _mqtt_region, _mqtt_endpoint);
           
            _logger.LogInformation("Try to connect to : {url}", wsUrl);
          
            var mqttFactory = new MqttFactory();
            var mqttClient = mqttFactory.CreateMqttClient();
            var clientId = Guid.NewGuid().ToString();
            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithWebSocketServer(wsUrl)  // <-- URL WSS completo con path e query
                .WithProtocolVersion(MqttProtocolVersion.V311)
                .WithClientId(clientId)
                .Build();



            _logger.LogInformation("Starting connection to broker with ClientId {clientId}", clientId);
            mqttClient.ConnectedAsync += async e =>
            {
                _logger.LogInformation("Connesso ad AWS IoT Core! al broker {endpoint}", _mqtt_endpoint);
                await mqttClient.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter("s3/update")
                    .Build());
            };

            mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                string message = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
                _logger.LogInformation("Messaggio ricevuto: {message}",message);
                await Task.CompletedTask;
            };

          

            mqttClient.DisconnectedAsync += async e =>
            {
                _logger.LogInformation("Disconnesso dal broker!");
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    await mqttClient.ConnectAsync(mqttClientOptions);
                }
                catch (Exception ex)
                {
                    _logger.LogInformation($"Errore di riconnessione: {ex.Message}");
                }
            };


            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await mqttClient.ConnectAsync(mqttClientOptions, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Timeout o errore durante connessione MQTT");
            }
        }
    }
}

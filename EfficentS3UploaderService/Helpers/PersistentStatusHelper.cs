using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfficentS3UploadService.Helpers
{
    internal static class PersistentStatusHelper
    {
        private static readonly object _fileLock = new();
        private static readonly string StatusFile = Path.Combine(AppContext.BaseDirectory, "online_status.json");

        public class OnlineStatus
        {
            [JsonPropertyName("clientId")]
            public string ClientId { get; set; }

            [JsonPropertyName("lastOnlineTimestamp")]
            public long LastOnlineTimestamp { get; set; }
        }

        public static void SaveStatus(string clientId, long timestamp)
        {
            lock (_fileLock)
            {
                var status = new OnlineStatus
                {
                    ClientId = clientId,
                    LastOnlineTimestamp = timestamp
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(status, options);
                File.WriteAllText(StatusFile, json);
            }
        }

        public static OnlineStatus LoadStatus()
        {
            lock (_fileLock)
            {
                if (!File.Exists(StatusFile)) return null;

                try
                {
                    var json = File.ReadAllText(StatusFile);
                    return JsonSerializer.Deserialize<OnlineStatus>(json);
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}

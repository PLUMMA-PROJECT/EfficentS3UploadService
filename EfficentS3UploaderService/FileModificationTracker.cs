using System.Collections.Concurrent;

namespace EfficentS3UploadService
{
    public static class FileModificationTracker
    {
        private static readonly ConcurrentDictionary<string, DateTime> mqttModifiedFiles = new();

        public static void MarkAsModifiedByMqtt(string path)
        {
            mqttModifiedFiles[path] = DateTime.UtcNow;
        }

        public static bool WasRecentlyModifiedByMqtt(string path, int secondsThreshold = 3)
        {
            if (mqttModifiedFiles.TryGetValue(path, out var timestamp))
            {
                if ((DateTime.UtcNow - timestamp).TotalSeconds < secondsThreshold)
                {
                    return true;
                }
                mqttModifiedFiles.TryRemove(path, out _); // cleanup
            }
            return false;
        }
    }
}

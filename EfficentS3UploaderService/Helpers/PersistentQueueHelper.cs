using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfficentS3UploadService.Helpers
{
    internal class PersistentQueueHelper
    {
        private static readonly object _fileLock = new();

        public class QueueEntry
        {
            [JsonPropertyName("item")]
            public string Item { get; set; }

            [JsonPropertyName("timestamp")]
            public DateTime Timestamp { get; set; }
        }

        public static void EnqueueToJsonFile(string filePath, string newItem)
        {
            lock (_fileLock)
            {
                var now = DateTime.UtcNow;

                if (filePath.Equals(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"), StringComparison.OrdinalIgnoreCase))
                {
                    var toDelete = ReadQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"));
                    var newList = ReadQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"));

                    // Se l'item è in TO_DELETE → lo rimuoviamo
                    toDelete.RemoveAll(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase));
                    WriteQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"), toDelete);

                    // Aggiungiamo in NEW solo se non già presente
                    if (!newList.Any(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase)))
                    {
                        newList.Add(new QueueEntry { Item = newItem, Timestamp = now });
                        WriteQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"), newList);
                    }
                }
                else if (filePath.Equals(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"), StringComparison.OrdinalIgnoreCase))
                {
                    var toDelete = ReadQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"));
                    var newList = ReadQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"));

                    // Se l'item è in NEW → lo rimuoviamo
                    newList.RemoveAll(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase));
                    WriteQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"), newList);

                    // Aggiungiamo in TO_DELETE solo se non già presente
                    if (!toDelete.Any(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase)))
                    {
                        toDelete.Add(new QueueEntry { Item = newItem, Timestamp = now });
                        WriteQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"), toDelete);
                    }
                }
                else
                {
                    throw new ArgumentException("filePath must be either (DELETION) "+ Path.Combine(AppContext.BaseDirectory, "delete_queue.json")+" or (NEW FILES) "+ Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"));
                }
            }
        }

        public static List<QueueEntry> ReadQueue(string filePath)
        {
            lock (_fileLock)
            {
                if (!File.Exists(filePath))
                    return new List<QueueEntry>();

                try
                {
                    string json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<List<QueueEntry>>(json) ?? new List<QueueEntry>();
                }
                catch
                {
                    return new List<QueueEntry>();
                }
            }
        }

        public static void WriteQueue(string filePath, List<QueueEntry> entries)
        {
            lock (_fileLock)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(entries, options);
                File.WriteAllText(filePath, json);
            }
        }

        // ✅ Getter: Only items (as List<string>)
        public static List<string> ReadItemList(string filePath)
        {
            lock (_fileLock)
            {
                var entries = ReadQueue(filePath);
                var items = new List<string>();
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Item))
                        items.Add(entry.Item);
                }
                return items;
            }
        }

        // ✅ Setter: Only items (as List<string>) → recreate with current timestamp
        public static void WriteItemList(string filePath, List<string> items)
        {
            lock (_fileLock)
            {
                var entries = new List<QueueEntry>();
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        entries.Add(new QueueEntry
                        {
                            Item = item,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                }

                WriteQueue(filePath, entries);
            }
        }
    }
}

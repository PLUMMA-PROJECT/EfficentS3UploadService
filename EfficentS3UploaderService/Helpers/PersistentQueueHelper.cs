using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EfficentS3UploadService.Helpers
{
    /// <summary>
    /// A helper class for managing two persistent queues stored as JSON files:
    /// - newfile_queue.json: files pending processing
    /// - delete_queue.json: files pending deletion
    /// Items are mutually exclusive across the queues.
    /// </summary>
    internal class PersistentQueueHelper
    {
        private static readonly object _fileLock = new();

        /// <summary>
        /// Structure representing an entry in the queue.
        /// </summary>
        public class QueueEntry
        {
            [JsonPropertyName("item")]
            public string Item { get; set; }

            [JsonPropertyName("timestamp")]
            public DateTime Timestamp { get; set; }
        }

        /// <summary>
        /// Adds an item to the specified queue (either newfile or delete),
        /// ensuring that it is not present in the opposite queue.
        /// If found in the opposite queue, it will be removed from it first.
        /// </summary>
        public static void EnqueueToJsonFile(string filePath, string newItem)
        {
            lock (_fileLock)
            {
                var now = DateTime.UtcNow;

                if (filePath.Equals(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"), StringComparison.OrdinalIgnoreCase))
                {
                    var toDelete = ReadQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"));
                    var newList = ReadQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"));

                    // Remove from TO_DELETE if already there
                    toDelete.RemoveAll(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase));
                    WriteQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"), toDelete);

                    // Add to NEW only if not already there
                    if (!newList.Exists(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase)))
                    {
                        newList.Add(new QueueEntry { Item = newItem, Timestamp = now });
                        WriteQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"), newList);
                    }
                }
                else if (filePath.Equals(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"), StringComparison.OrdinalIgnoreCase))
                {
                    var toDelete = ReadQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"));
                    var newList = ReadQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"));

                    // Remove from NEW if already there
                    newList.RemoveAll(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase));
                    WriteQueue(Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"), newList);

                    // Add to TO_DELETE only if not already there
                    if (!toDelete.Exists(e => e.Item.Equals(newItem, StringComparison.OrdinalIgnoreCase)))
                    {
                        toDelete.Add(new QueueEntry { Item = newItem, Timestamp = now });
                        WriteQueue(Path.Combine(AppContext.BaseDirectory, "delete_queue.json"), toDelete);
                    }
                }
                else
                {
                    throw new ArgumentException("filePath must be either (DELETION) " +
                        Path.Combine(AppContext.BaseDirectory, "delete_queue.json") +
                        " or (NEW FILES) " +
                        Path.Combine(AppContext.BaseDirectory, "newfile_queue.json"));
                }
            }
        }

        /// <summary>
        /// Reads the queue from a JSON file and returns the list of QueueEntry items.
        /// </summary>
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

        /// <summary>
        /// Writes a list of QueueEntry items to a JSON file.
        /// </summary>
        public static void WriteQueue(string filePath, List<QueueEntry> entries)
        {
            lock (_fileLock)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(entries, options);
                File.WriteAllText(filePath, json);
            }
        }

        /// <summary>
        /// Reads only the item strings from the queue file (no timestamps).
        /// </summary>
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

        /// <summary>
        /// Writes a list of item strings to the queue file, creating QueueEntry objects with current timestamps.
        /// </summary>
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

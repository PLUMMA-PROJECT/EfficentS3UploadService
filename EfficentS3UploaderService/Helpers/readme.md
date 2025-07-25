# Efficient S3 Upload Service – Persistent Queue Helper

This project includes a helper class that manages a persistent queue saved as a JSON file, specifically designed for managing file paths related to new uploads and deletions in an S3 context.

## Features

- Thread-safe queue file access using file-level locking.
- Tracks both the item (e.g., file path) and the UTC timestamp of when it was added.
- Enforces mutual exclusion between two lists:
  - `newfile_queue.json`
  - `delete_queue.json`
- Prevents duplication and ensures that the same item is not present in both queues simultaneously.

## Queue Logic

### When Adding a New Item

- If writing to `newfile_queue.json`:
  - Removes the item from `delete_queue.json` (if present).
  - Adds the item to `newfile_queue.json` only if not already present.

- If writing to `delete_queue.json`:
  - Removes the item from `newfile_queue.json` (if present).
  - Adds the item to `delete_queue.json` only if not already present.

## Usage

### Enqueue an Item

```csharp
PersistentQueueHelper.EnqueueToJsonFile(queuePath, itemPath);
```

Where `queuePath` must be one of:

```csharp
public static readonly string DeleteQueueFile = Path.Combine(AppContext.BaseDirectory, "delete_queue.json");
public static readonly string NewFileQueue = Path.Combine(AppContext.BaseDirectory, "newfile_queue.json");
```

### Read Full Queue

```csharp
List<QueueEntry> entries = PersistentQueueHelper.ReadQueue(queuePath);
```

### Read Only Items

```csharp
List<string> items = PersistentQueueHelper.ReadItemList(queuePath);
```

### Write Item List (Overwrites Existing Queue)

```csharp
PersistentQueueHelper.WriteItemList(queuePath, new List<string> { "file1.txt", "file2.txt" });
```

## `QueueEntry` Structure

```json
{
  "item": "full/path/to/file.txt",
  "timestamp": "2025-07-24T15:30:00Z"
}
```

## License

This component is intended for internal use within the EfficientS3UploadService.
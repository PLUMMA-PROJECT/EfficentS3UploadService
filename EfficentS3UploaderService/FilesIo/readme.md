
# FileManagerService

## Overview

`FileManagerService` is a core utility in the `EfficentS3UploadService` project responsible for **synchronizing files between AWS S3 and the local filesystem**. It is designed to work with **MQTT messages** from AWS IoT Core, and ensures that files are downloaded, updated, or removed **only when necessary**, based on SHA256 hash comparison.

The service also integrates with a helper class, `FileModificationTracker`, to prevent redundant operations triggered by closely repeated MQTT events.

---

## Responsibilities

- 📥 Download files from S3 based on MQTT messages.
- 🧠 Detect file content changes using SHA256 hashes.
- 🧷 Avoid overwriting files that are locked or already up-to-date.
- 🗂 Automatically create local directories if they don’t exist.
- 🧾 Read SHA256 hash metadata from S3 (`x-amz-meta-sha256`).
- 🗑 Move deleted files to the Windows Recycle Bin (when applicable).
- 🕒 Track and suppress redundant updates using `FileModificationTracker`.

---

## MQTT Message Handling Flow

1. Receives a JSON message via MQTT:
   ```json
   { "key": "folder%2Ffile.txt" }
   ```
2. Decodes the S3 key and downloads the file.
3. Retrieves the SHA256 hash from S3 metadata.
4. Compares it with the local file's hash.
5. If content has changed, saves or replaces the file.
6. Marks the file as modified using `FileModificationTracker`.

---

## Public Methods

### `Task ProcessMessageAndDownloadAsync(string jsonMessage)`

- Parses the incoming JSON MQTT message.
- Downloads the corresponding file from S3.
- Compares content using SHA256 hashes.
- Saves or updates the local file only if needed.
- Uses `FileModificationTracker` to prevent redundant reprocessing.

### `bool MoveFileToRecycleBin(string filePath)`

- Moves the specified file to the Recycle Bin on Windows.
- Returns `true` if the operation succeeds or the file doesn’t exist.

---

## Private Methods

| Method                                | Description |
|---------------------------------------|-------------|
| `DownloadFromS3Async(string key)`     | Downloads file contents from S3 for a given key. |
| `SaveOrUpdateFile(string path, byte[])` | Saves the file if new or different from existing content. |
| `IsFileLocked(string path)`           | Checks if the file is currently in use by another process. |
| `ComputeSha256(byte[] data)`          | Calculates the SHA256 hash of a byte array. |
| `GetS3ObjectSha256MetadataAsync(string key)` | Retrieves SHA256 metadata from S3 object headers. |

---

## FileModificationTracker

### Purpose

`FileModificationTracker` is a lightweight static helper that tracks **files recently modified** by the system as a result of MQTT messages. This avoids **reprocessing** the same file within a short time window.

### How it works

- Whenever a file is modified due to an MQTT-triggered download, its path is recorded with a UTC timestamp.
- If another MQTT message arrives for the same file shortly after (e.g., within 3 seconds), the file is **skipped**.
- This prevents redundant writes and unnecessary S3 requests.

### API

| Method | Description |
|--------|-------------|
| `void MarkAsModifiedByMqtt(string path)` | Records the file path and timestamp. |
| `bool WasRecentlyModifiedByMqtt(string path, int secondsThreshold = 3)` | Returns `true` if the file was modified within the last N seconds, and removes expired entries. |

---

## Dependencies

| Dependency             | Description |
|------------------------|-------------|
| `ILogger<Worker>`      | Logs information and errors during processing. |
| `IAmazonS3`            | AWS S3 SDK client for downloading files and reading metadata. |
| `string basePath`      | Local root directory where files are stored. |
| `string bucketName`    | Name of the S3 bucket to pull files from. |

---

## Platform Notes

- ✅ Fully compatible with Windows
- 🗑 Uses Windows-specific Recycle Bin API (via `Microsoft.VisualBasic.FileIO`)
- 📦 Assumes S3 objects have `x-amz-meta-sha256` metadata set for hash checking
- ⚠️ Will skip files locked by external applications

---

## Example JSON Message

```json
{
  "key": "configs%2Fdevice-123.json"
}
```

> The `key` is expected to be URL-encoded and will be decoded internally before accessing S3.

---

## License

📁 Internal use only – part of the `EfficentS3UploadService` system.  
Not intended for public reuse or distribution.

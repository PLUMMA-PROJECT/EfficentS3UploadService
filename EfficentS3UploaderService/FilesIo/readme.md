# FileManagerService

## Overview

`FileManagerService` is a utility class responsible for synchronizing files between AWS S3 and the local filesystem. It handles downloading files from S3 based on MQTT messages and managing local file states including safe replacement, hash comparison, and deletion via Recycle Bin.

---

## Features

- Download and store S3 files locally
- Detect content changes via SHA256 hash comparison
- Avoids overwriting locked or unchanged files
- Automatically creates directories as needed
- Reads custom SHA256 metadata from S3 objects
- Moves files to Recycle Bin instead of hard deletion (Windows)

---

## Public Methods

### `ProcessMessageAndDownloadAsync(string jsonMessage)`
Parses a JSON message (from MQTT), decodes the S3 key, downloads the corresponding file (if needed), and stores it locally after comparing hashes.

### `MoveFileToRecycleBin(string filePath)`
Moves the specified file to the system's Recycle Bin. Returns `true` if successful or the file doesn't exist.

---

## Private Methods

- `DownloadFromS3Async(string key)`  
  Downloads and returns the contents of an S3 object.

- `SaveOrUpdateFile(string filePath, byte[] newContent)`  
  Saves a file if it doesn’t exist or overwrites it if the content differs.

- `IsFileLocked(string path)`  
  Checks whether the file is currently locked by another process.

- `ComputeSha256(byte[] data)`  
  Calculates the SHA256 hash of a byte array.

- `GetS3ObjectSha256MetadataAsync(string key)`  
  Retrieves the `x-amz-meta-sha256` value from S3 object metadata.

---

## Configuration

This class depends on the following injected values:

- `ILogger<Worker>` — For structured logging
- `string basePath` — Root local folder where files will be written
- `IAmazonS3 s3Client` — AWS S3 client for interacting with buckets
- `string bucketName` — Target S3 bucket name

---

## Notes

- Designed for use with **AWS IoT Core + MQTT** and **S3**.
- Compatible with Windows-based environments for Recycle Bin operations.
- Assumes SHA256 hashes are stored in the metadata field `x-amz-meta-sha256` of each S3 object.

---

## License

Internal utility used in the `EfficentS3UploadService` project. Not for public distribution.

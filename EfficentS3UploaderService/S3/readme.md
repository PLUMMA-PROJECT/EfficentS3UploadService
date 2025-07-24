# S3Uploader

## Overview

`S3Uploader` is a utility class responsible for uploading files to Amazon S3 with support for:

- Multipart uploads (5MB+)
- Custom metadata (`x-amz-meta-sha256`)
- Optional bandwidth throttling
- SHA-256 content verification

It is used as part of the `EfficentS3UploadService` project to ensure reliable and traceable file synchronization between a local system and the cloud.

---

## Features

- Authenticates with AWS using access keys from configuration
- Uploads any local file to a specified key on S3
- Automatically computes and attaches SHA-256 checksum as metadata
- Supports large files through AWS S3 multipart uploads
- Logs success and failure states
- Throttles network usage by pausing every 10MB transferred (optional)

---

## Constructor

```csharp
public S3Uploader(IConfiguration config, ILogger<Worker> logger)
```

**Dependencies:**
- `IConfiguration` (expects AWS config values under `AWS:*`)
- `ILogger<Worker>` for diagnostic output

**Expected config keys:**
```json
"AWS": {
  "Region": "eu-west-1",
  "BucketName": "your-bucket",
  "AccessKey": "AKIA...",
  "SecretKey": "..."
}
```

---

## Public Method

### `Task UploadFileToS3(string filePath, string keyName)`

Uploads a local file to the specified S3 key path. Attaches a SHA-256 hash as metadata (`x-amz-meta-sha256`) for later verification.

**Parameters:**
- `filePath`: Path to the local file to upload
- `keyName`: The destination S3 object key

**Exceptions:**
- Throws on invalid credentials, network failures, or AWS errors

---

## SHA-256 Metadata

Before uploading, the file's SHA-256 hash is computed and sent as a custom metadata field:

```http
x-amz-meta-sha256: <computed_hash>
```

This can be used later to verify content integrity or detect updates.

---

## Throttling (optional)

By default, a small delay is introduced every 10MB transferred:

```csharp
Thread.Sleep(500); // 500ms delay every 10MB
```

This helps to:
- Reduce load on the network
- Prevent S3 throttling
- Smooth out bursts in low-bandwidth environments

You can adjust or disable this behavior as needed.

---

## Logging

The uploader logs:
- Initialization
- Start and end of upload
- Any exceptions encountered

---

## Usage Example

```csharp
var uploader = new S3Uploader(config, logger);
await uploader.UploadFileToS3("C:\temp\log.txt", "logs/2025-07-24/log.txt");
```

---

## License

Internal utility used in the `EfficentS3UploadService` project. Not intended for external distribution.

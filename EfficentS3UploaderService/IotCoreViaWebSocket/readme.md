# IotCoreViaWebsocket

## Overview

`IotCoreViaWebsocket` is a class that connects to **AWS IoT Core** via **MQTT over WebSocket** using AWS SigV4 authentication. It listens for file update and delete messages, interacts with **Amazon S3**, and manages local file changes accordingly.

---

## Features

- Connects securely to AWS IoT Core over WebSocket using SigV4
- Subscribes to:
  - `EfficentS3UploadService/update`: to download files
  - `EfficentS3UploadService/delete`: to delete files
- Sends "online" presence messages
- Supports offline delete message queuing and retry
- Logs all operations using ASP.NET Core `ILogger`

---

## Key Methods

### `ConnectAndSubscribeAsync()`
Establishes the MQTT connection, subscribes to topics, and handles reconnection logic.

### `PublishOnlineMessage()`
Publishes an "online" status message with client ID and timestamp.

### `PublishDeleteMessage(string key)`
Sends a delete instruction for a given file key.

### `PublishQueuedDeletesAsync()`
Replays delete instructions from a local queue file if MQTT was previously unavailable.

### `HandleDeleteMessage(string messagePayload)`
Parses a delete message and moves the corresponding file to a recycle bin.

---

## Configuration Keys (used in `IConfiguration`)

```json
{
  "AWS": {
    "MQTT_region": "...",
    "MQTT_endpoint": "...",
    "AccessKey": "...",
    "SecretKey": "...",
    "BucketName": "..."
  },
  "FOLDER": {
    "Path": "..."
  }
}

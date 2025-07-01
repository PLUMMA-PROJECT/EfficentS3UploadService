using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Transfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;
using System.Collections.Generic;

namespace SampleService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILogger<S3Uploader> _s3Logger;

    private FileSystemWatcher? _watcher;
    private readonly string _pathToWatch = @"C:\Temp";
    private readonly S3Uploader _uploader;
    private readonly Dictionary<string, DateTime> _fileExecutionTimestamps = new();
    private readonly object _lock = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromSeconds(3);

    public Worker(ILogger<Worker> logger, ILogger<S3Uploader> s3Logger)
    {
      IConfiguration config=  new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();
        _logger = logger;
        _s3Logger = s3Logger;
        _logger.LogInformation("Worker initialized...");
        _uploader = new S3Uploader(config, _s3Logger);
        _pathToWatch = config["FOLDER:Path"];
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExecuteAsync started.");

        _watcher = new FileSystemWatcher(_pathToWatch)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;

        _logger.LogInformation("Started watching {path}", _pathToWatch);

        return Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("File created: {file}", e.FullPath);
        _ = HandleFileChangeAsync(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("File deleted: {file}", e.FullPath);
        // Non serve upload su cancellazione
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("File changed: {file}", e.FullPath);
        _ = HandleFileChangeAsync(e.FullPath);
    }

    private async Task HandleFileChangeAsync(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                _logger.LogInformation("Skipping directory change: {dir}", fullPath);
                return;
            }

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("File does not exist: {file}", fullPath);
                return;
            }

            lock (_lock)
            {
                if (_fileExecutionTimestamps.TryGetValue(fullPath, out var lastExecution))
                {
                    if (DateTime.UtcNow - lastExecution < _debounceWindow)
                    {
                        _logger.LogInformation("Skipping duplicate trigger for {file}", fullPath);
                        return;
                    }
                }
                _fileExecutionTimestamps[fullPath] = DateTime.UtcNow;
            }

            // Aspetta che il file non sia più lockato (max 5 tentativi)
            int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                if (await IsFileReadyAsync(fullPath))
                    break;

                _logger.LogWarning("File {file} is still in use. Retrying in 1500ms...", fullPath);
                await Task.Delay(1500);
            }

            var relativePath = Path.GetRelativePath(_pathToWatch, fullPath);
            var s3Key = relativePath.Replace('\\', '/');

            _logger.LogInformation("Uploading {fullPath} to S3 with key {key}", fullPath, s3Key);
            await _uploader.UploadFileToS3(fullPath, s3Key);
            _logger.LogInformation("Upload completato per {file}", fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle file change for {file}", fullPath);
        }
    }

    private static async Task<bool> IsFileReadyAsync(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, true);
            await Task.CompletedTask; // placeholder per async
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _watcher?.Dispose();
        _logger.LogInformation("File watcher stopped.");
        return base.StopAsync(cancellationToken);
    }
}

using EfficentS3UploadService;
using System.IO;
using System.Threading.Tasks;

namespace EfficentS3UploadSerivice;

public class Worker : BackgroundService
{
    private static ILogger<Worker> _logger;
    private readonly ILogger<S3Uploader> _s3Logger;
    

    private FileSystemWatcher? _watcher;
    private static string _pathToWatch = @"C:\Temp";
    private static S3Uploader _uploader;
    private readonly IotCoreViaWebsocket _mqttClient;
    private readonly Dictionary<string, DateTime> _fileExecutionTimestamps = new();
    private readonly object _lock = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromSeconds(3);


    public Worker(ILogger<Worker> logger, ILogger<S3Uploader> s3Logger)
    {
      IConfiguration config=  new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();
        _logger = logger;
        _uploader = new S3Uploader(config, _logger);
        // Initialize Event listener on IoT Core
        _mqttClient = new IotCoreViaWebsocket(config,_logger);
        _pathToWatch = config["FOLDER:Path"];             
        _logger.LogInformation("Worker initialized...");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExecuteAsync started.");
        // Initialize Watcher
        _watcher = new FileSystemWatcher(_pathToWatch)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime   | NotifyFilters.Size
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;

        _logger.LogInformation("Started watching {path}", _pathToWatch);
        await _mqttClient.ConnectAndSubscribeAsync();
        await Task.Delay(TimeSpan.FromSeconds(30));
        await _mqttClient.PublishOnlineMessage();
        await Task.Delay(Timeout.Infinite, stoppingToken);
        await PublishQueuedNewfilesAsync();
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _logger.LogInformation("File created: {file}", e.FullPath);
        _ = HandleFileChangeAsync(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("File deleted: {file}", e.FullPath);
                string relativeKey = Path.GetRelativePath(_pathToWatch, e.FullPath)
                         .Replace("\\", "/");
                await _mqttClient.PublishDeleteMessage(relativeKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling deleted file: {file}", e.FullPath);
                _logger.LogInformation("Delete queue path is: {path}", DeleteQueueFile);

                EnqueueDeletePath(e.FullPath);
            }
        });
    }

    public static readonly string DeleteQueueFile = Path.Combine(AppContext.BaseDirectory, "delete_queue.json");
    public static readonly string NewFileQueue = Path.Combine(AppContext.BaseDirectory, "newfile_queue.json");


    // Chiamato quando la connessione fallisce
    private void EnqueueDeletePath(string fullPath)
    {
        try
        {
            _logger.LogInformation("(EnqueueDeletePath) File saved in queue: {file}", fullPath);
            _logger.LogInformation("Delete queue path is: {path}", DeleteQueueFile);
            File.AppendAllLines(DeleteQueueFile, new[] { fullPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue delete path: {file}", fullPath);
        }
    }


    // Chiamato quando la connessione fallisce
    private void EnqueueNewPath(string fullPath)
    {
        try
        {
            _logger.LogInformation("(EnqueueNewPath) File saved in queue: {file}", fullPath);
            _logger.LogInformation("New file queue path is: {path}", NewFileQueue);
            File.AppendAllLines(NewFileQueue, new[] { fullPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue new file path: {file}", fullPath);
        }
    }



    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _logger.LogInformation("File changed: {file}", e.FullPath);
        _ = HandleFileChangeAsync(e.FullPath);
    }

    private async Task HandleFileChangeAsync(string fullPath)
    {
        try
        {

            if (FileModificationTracker.WasRecentlyModifiedByMqtt(fullPath))
            {
                return; // Skip upload
            }
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
            EnqueueNewPath(fullPath);
            
        }
    }

    public static async Task PublishQueuedNewfilesAsync()
    {
        if (!File.Exists(Worker.NewFileQueue)) return;

        var lines = File.ReadAllLines(Worker.NewFileQueue).ToList();
        var remaining = new List<string>();

        foreach (var key in lines)
        {
            try
            {
                string relativeKey = Path.GetRelativePath(_pathToWatch, key)
                        .Replace("\\", "/");
                await _uploader.UploadFileToS3(key, relativeKey);
                _logger.LogInformation("(PublishQueuedNewfilesAsync) Republished new file message: {key}", key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "(PublishQueuedNewfilesAsync) Retry failed new file for: {key}", key);
                remaining.Add(key);
            }
        }

        File.WriteAllLines(Worker.NewFileQueue, remaining);
    }

    private static async Task<bool> IsFileReadyAsync(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, true);
            await Task.CompletedTask; 
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

    private bool ShouldIgnore(string path)
    {
        return path.Contains("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Thumbs.db", StringComparison.OrdinalIgnoreCase);
    }
}

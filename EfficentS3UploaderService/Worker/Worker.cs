using EfficentS3UploadService;
using EfficentS3UploadService.FilesIo;
using EfficentS3UploadService.Helpers;
using EfficentS3UploadService.S3;
using System.IO;
using System.Threading.Tasks;

namespace EfficentS3UploadService.Worker;

public class Worker : BackgroundService
{
    private static ILogger<Worker> _logger;
    private readonly ILogger<S3FileManager> _s3Logger;
    

    private FileSystemWatcher? _watcher;
    private static string _pathToWatch = @"C:\Temp";
    private static S3FileManager _uploader;
    private readonly IotCoreViaWebsocket _mqttClient;
    private readonly Dictionary<string, DateTime> _fileExecutionTimestamps = new();
    private readonly object _lock = new();
    private readonly TimeSpan _debounceWindow = TimeSpan.FromSeconds(3);
    private HashSet<string> _previousSnapshot = new();


    public Worker(ILogger<Worker> logger, ILogger<S3FileManager> s3Logger)
    {
      IConfiguration config=  new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();
        _logger = logger;
        _uploader = new S3FileManager(config, _logger);
        // Initialize Event listener on IoT Core
        _mqttClient = new IotCoreViaWebsocket(config,_logger);
        _pathToWatch = config["FOLDER:Path"];             
        _logger.LogInformation("Worker initialized...");
    }


    private HashSet<string> TakeSnapshot(string path)
    {
        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(path)) return snapshot;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                snapshot.Add(Path.GetFullPath(file));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot scan failed");
        }

        return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ExecuteAsync started.");
        // Initialize Watcher
        _previousSnapshot = TakeSnapshot(_pathToWatch);

        _watcher = new FileSystemWatcher(_pathToWatch)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime   | NotifyFilters.Size
        };

        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;

        _logger.LogInformation("Started watching {path}", _pathToWatch);
        await _mqttClient.ConnectAndSubscribeAsync();
        await Task.Delay(TimeSpan.FromSeconds(30));
        await _mqttClient.PublishOnlineMessage();
        await Task.Delay(Timeout.Infinite, stoppingToken);
        await PublishQueuedNewfilesAsync();
    }


    private async void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;

        _logger.LogInformation("File renamed from {OldName} to {NewName}", e.OldFullPath, e.FullPath);

        try
        {
            // Chiave S3 vecchia
            var oldKey = Path.GetRelativePath(_pathToWatch, e.OldFullPath).Replace("\\", "/");
            // Chiave S3 nuova
            var newKey = Path.GetRelativePath(_pathToWatch, e.FullPath).Replace("\\", "/");

            await _mqttClient.PublishRenameMessage(oldKey, newKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds() );

            _logger.LogInformation("S3 file renamed from {OldKey} to {NewKey}", oldKey, newKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle renamed file from {OldFile} to {NewFile}", e.OldFullPath, e.FullPath);
           

            EnqueueRenamePath(e.OldFullPath,e.FullPath);

        }
    }


    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _logger.LogInformation("(OnCreated) File created: {file}", e.FullPath);
        _ = HandleFileChangeAsync(e.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {

        if (FilesIo.FileManagerService._recentlyRenamedFiles.TryGetValue(e.FullPath, out DateTime renameTime))
        {
            if ((DateTime.UtcNow - renameTime).TotalSeconds < 1)
            {
                // È un falso "delete" dopo una rename
                _logger.LogInformation("(MoveFileToRecycleBin) Ignorato delete dopo rename: {Path}",e.FullPath);
                FilesIo.FileManagerService._recentlyRenamedFiles.Remove(e.FullPath);       
                return;
            }
        }
       
        if (ShouldIgnore(e.FullPath)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("(OnDeleted) File deleted: {file}", e.FullPath);
                bool wasDirectory = _previousSnapshot.Any(path => path.StartsWith(e.FullPath + Path.DirectorySeparatorChar));
                // Se è una directory, confronta snapshot per scoprire quali file sono stati eliminati
                if (wasDirectory)
                {
                    var currentSnapshot = TakeSnapshot(_pathToWatch);
                    var deletedFiles = _previousSnapshot.Except(currentSnapshot);

                    foreach (var deletedFile in deletedFiles)
                    {
                        string relativeKey = Path.GetRelativePath(_pathToWatch, deletedFile)
                                .Replace("\\", "/");

                        _logger.LogInformation("(OnDeleted) Detected deleted file via snapshot: {file}", deletedFile);
                        await _mqttClient.PublishDeleteMessage(relativeKey);
                    }

                    _previousSnapshot = currentSnapshot;
                    return;
                }
                string relativeKeyPath = Path.GetRelativePath(_pathToWatch, e.FullPath)
                         .Replace("\\", "/");
                await _mqttClient.PublishDeleteMessage(relativeKeyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "(OnDeleted) Error handling deleted file: {file}", e.FullPath);
                _logger.LogInformation("(OnDeleted) Delete queue path is: {path}", DeleteQueueFile);

                EnqueueDeletePath(e.FullPath);
            }
        });
    }

    public static readonly string DeleteQueueFile = Path.Combine(AppContext.BaseDirectory, "delete_queue.json");
    public static readonly string NewFileQueue = Path.Combine(AppContext.BaseDirectory, "newfile_queue.json");
    public static readonly string RenameFileQueue = Path.Combine(AppContext.BaseDirectory, "rename_queue.json");


    // Chiamato quando la connessione fallisce
    private void EnqueueRenamePath(string oldFullPath, string fullPath)
    {
        try
        {
            _logger.LogInformation("(EnqueueRenamePath) Enqueuing file to renamed file queue: {file}", fullPath);
            PersistentQueueHelper.EnqueueToJsonFile(RenameFileQueue, oldFullPath+">"+fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "(EnqueueRenamePath) Failed to enqueue renamed file lete path: {file}", fullPath);
        }
    }


    // Chiamato quando la connessione fallisce
    private void EnqueueDeletePath(string fullPath)
    {
        try
        {
            _logger.LogInformation("(EnqueueDeletePath) Enqueuing file to delete queue: {file}", fullPath);
            PersistentQueueHelper.EnqueueToJsonFile(DeleteQueueFile, fullPath);
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
            PersistentQueueHelper.EnqueueToJsonFile(NewFileQueue, fullPath);
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
        if (!File.Exists(NewFileQueue)) return;

        var lines = PersistentQueueHelper.ReadItemList(NewFileQueue);
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
        PersistentQueueHelper.WriteItemList(NewFileQueue,remaining);
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

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EfficentS3UploadService.FilesIo
{
    public class MyDirectoryWatcher : IDisposable
    {
        private readonly ILogger<MyDirectoryWatcher> _logger;
        private readonly string _directoryToWatch;
        private readonly TimeSpan _pollingInterval = TimeSpan.FromSeconds(6);
        private Dictionary<string, FileSnapshot> _previousSnapshot = new();

        private CancellationTokenSource? _cts;
        private Task? _watchingTask;

        public event EventHandler<FileSystemEventArgs>? Created;
        public event EventHandler<FileSystemEventArgs>? Changed;
        public event EventHandler<FileSystemEventArgs>? Deleted;
        public event EventHandler<RenamedEventArgs>? Renamed; // Per eventuale gestione rinomini

        public MyDirectoryWatcher(string directoryToWatch, ILogger<MyDirectoryWatcher> logger)
        {
            _directoryToWatch = directoryToWatch ?? throw new ArgumentNullException(nameof(directoryToWatch));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogInformation("[WATCHING] Starting directory watcher {Directory}", _directoryToWatch);
        }

        // Metodo pubblico per avviare il watcher in modo asincrono
        public Task StartAsync()
        {
            if (_watchingTask != null && !_watchingTask.IsCompleted)
                throw new InvalidOperationException("Watcher already started.");

            _cts = new CancellationTokenSource();
            _watchingTask = WatchDirectoryAsync(_cts.Token);
            return Task.CompletedTask;
        }

        // Metodo per fermare il watcher
        public async Task StopAsync()
        {
            if (_cts == null)
                return;

            _cts.Cancel();

            try
            {
                if (_watchingTask != null)
                    await _watchingTask;
            }
            catch (OperationCanceledException)
            {
                // previsto alla cancellazione
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                _watchingTask = null;
            }
        }

        private async Task WatchDirectoryAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[WATCHING] {Directory}", _directoryToWatch);

            _previousSnapshot = await CaptureSnapshotAsync(_directoryToWatch, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("[WATCHING] Detecting changes in {Directory}", _directoryToWatch);
                    var currentSnapshot = await CaptureSnapshotAsync(_directoryToWatch, stoppingToken);
                    DetectChanges(_previousSnapshot, currentSnapshot);
                    _previousSnapshot = currentSnapshot;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while watching directory");
                }

                await Task.Delay(_pollingInterval, stoppingToken);
            }
        }


        private async Task<Dictionary<string, FileSnapshot>> CaptureSnapshotAsync(string directory, CancellationToken cancellationToken)
        {
            var snapshot = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
            var nowUtc = DateTime.UtcNow;
            var stabilityThreshold = TimeSpan.FromSeconds(10);
            int maxRetries = 3;
            int delayMs = 100; // 100 ms tra i retry

            foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnore(filePath))
                    continue;

                int attempt = 0;
                bool success = false;
                FileInfo? info = null;

                while (attempt < maxRetries && !success)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        info = new FileInfo(filePath);

                        // Salta file troppo recenti (ancora in scrittura)
                        if (nowUtc - info.LastWriteTimeUtc < stabilityThreshold)
                            break;

                        success = true; // lettura riuscita
                    }
                    catch (IOException)
                    {
                        _logger.LogWarning("File inaccessibile (in uso?): {Path}, tentativo {Attempt}", filePath, attempt + 1);
                        attempt++;
                        if (attempt < maxRetries)
                            await Task.Delay(delayMs, cancellationToken);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        _logger.LogWarning("File non autorizzato: {Path}", filePath);
                        break; // non ha senso ritentare
                    }
                }

                if (!success || info == null)
                    continue;

                snapshot[filePath] = new FileSnapshot
                {
                    FullPath = filePath,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                    Length = info.Length
                };
            }

            return snapshot;
        }


        private void DetectChanges(
      Dictionary<string, FileSnapshot> previous,
      Dictionary<string, FileSnapshot> current)
        {
            var prevKeys = new HashSet<string>(previous.Keys);
            var currKeys = new HashSet<string>(current.Keys);

            var added = currKeys.Except(prevKeys).ToList();
            var removed = prevKeys.Except(currKeys).ToList();
            var maybeModified = currKeys.Intersect(prevKeys);

            // Prova a rilevare i rinomini prima di trattare aggiunte e rimozioni
            var handledAdded = new HashSet<string>();
            var handledRemoved = new HashSet<string>();

            foreach (var removedPath in removed)
            {
                var oldFile = previous[removedPath];

                // Cerca un added file che abbia caratteristiche simili (dimensione, data)
                var possibleRenamedNewPath = added.FirstOrDefault(newPath =>
                {
                    var newFile = current[newPath];
                    return newFile.Length == oldFile.Length &&
                           newFile.LastWriteTimeUtc == oldFile.LastWriteTimeUtc;
                });

                if (possibleRenamedNewPath != null)
                {
                    // Rilevato rename
                    OnRenamed(removedPath, possibleRenamedNewPath);

                    handledRemoved.Add(removedPath);
                    handledAdded.Add(possibleRenamedNewPath);
                }
            }

            // Ora segnala i restanti added come created
            foreach (var path in added.Except(handledAdded))
                OnCreated(path);

            // E segnala i restanti removed come deleted
            foreach (var path in removed.Except(handledRemoved))
                OnDeleted(path);

            // Segnala modifiche
            foreach (var path in maybeModified)
            {
                var oldFile = previous[path];
                var newFile = current[path];

                if (oldFile.LastWriteTimeUtc != newFile.LastWriteTimeUtc ||
                    oldFile.Length != newFile.Length)
                {
                    OnChanged(path);
                }
            }
        }


        private void OnCreated(string path)
        {
            _logger.LogInformation("[CREATED] {Path}", path);
            Created?.Invoke(this, new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(path)!, Path.GetFileName(path)));
        }

        private void OnDeleted(string path)
        {
            _logger.LogInformation("[DELETED] {Path}", path);
            Deleted?.Invoke(this, new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path.GetDirectoryName(path)!, Path.GetFileName(path)));
        }

        private void OnChanged(string path)
        {
            _logger.LogInformation("[CHANGED] {Path}", path);
            Changed?.Invoke(this, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(path)!, Path.GetFileName(path)));
        }

        private void OnRenamed(string oldPath, string newPath)
        {
            _logger.LogInformation("[RENAMED] {OldPath} -> {NewPath}", oldPath, newPath);
            Renamed?.Invoke(this, new RenamedEventArgs(
                WatcherChangeTypes.Renamed,
                Path.GetDirectoryName(newPath)!,
                Path.GetFileName(newPath),
                Path.GetFileName(oldPath)
            ));
        }

        private bool ShouldIgnore(string path)
        {
            string fileName = Path.GetFileName(path);
            string lowerPath = path.ToLowerInvariant();

            // Skip system folders
            if (lowerPath.Contains(@"\$recycle.bin\") ||
                lowerPath.Contains(@"\system volume information\") ||
                lowerPath.Contains(@"\windows\"))
                return true;

            // Skip hidden or system files
            try
            {
                var attr = File.GetAttributes(path);
                if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                    return true;
            }
            catch
            {
                return true;
            }

            // Skip known temporary or irrelevant files
            string[] ignoredFiles =
            {
                "thumbs.db", "desktop.ini", "ehthumbs.db", "iconcache.db",
                "ntuser.dat", "ntuser.dat.log1", "ntuser.dat.log2",
                "pagefile.sys", "swapfile.sys", "hiberfil.sys"
            };

            if (ignoredFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                return true;

            // Skip temp/editing files (Office, Foto, etc.)
            if (fileName.StartsWith("~") || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".~tmp", StringComparison.OrdinalIgnoreCase))
                return true;

            // Files without extension are suspicious (often temp)
            if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
                return true;

            return false;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private class FileSnapshot
        {
            public string FullPath { get; set; } = null!;
            public DateTime LastWriteTimeUtc { get; set; }
            public long Length { get; set; }
        }
    }
}

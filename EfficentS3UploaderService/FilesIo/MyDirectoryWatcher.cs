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
        private readonly TimeSpan _stabilityThreshold = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _renameWindow = TimeSpan.FromSeconds(10);
        private Dictionary<string, FileSnapshot> _previousSnapshot = new();
        private readonly Dictionary<FileSnapshot, DateTime> _recentlyDeleted = new();

        private CancellationTokenSource? _cts;
        private Task? _watchingTask;

        public event EventHandler<FileSystemEventArgs>? Created;
        public event EventHandler<FileSystemEventArgs>? Changed;
        public event EventHandler<FileSystemEventArgs>? Deleted;
        public event EventHandler<RenamedEventArgs>? Renamed;

        public MyDirectoryWatcher(string directoryToWatch, ILogger<MyDirectoryWatcher> logger)
        {
            _directoryToWatch = directoryToWatch ?? throw new ArgumentNullException(nameof(directoryToWatch));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogInformation("[WATCHING] Starting directory watcher {Directory}", _directoryToWatch);
        }

        public Task StartAsync()
        {
            if (_watchingTask != null && !_watchingTask.IsCompleted)
                throw new InvalidOperationException("Watcher already started.");

            _cts = new CancellationTokenSource();
            _watchingTask = WatchDirectoryAsync(_cts.Token);
            return Task.CompletedTask;
        }

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
                // expected
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
            _previousSnapshot = await CaptureSnapshotAsync(_directoryToWatch, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
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
            int maxRetries = 3;
            int delayMs = 100;

            foreach (var filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (ShouldIgnore(filePath))
                    continue;

                int attempt = 0;
                FileInfo? info = null;

                while (attempt < maxRetries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        info = new FileInfo(filePath);

                        if (nowUtc - info.LastWriteTimeUtc < _stabilityThreshold)
                        {
                            attempt++;
                            await Task.Delay(delayMs, cancellationToken);
                            continue;
                        }

                        break;
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
                        break;
                    }
                }

                if (info != null)
                {
                    snapshot[filePath] = new FileSnapshot
                    {
                        FullPath = filePath,
                        LastWriteTimeUtc = info.LastWriteTimeUtc,
                        Length = info.Length
                    };
                }
            }

            return snapshot;
        }

        private void DetectChanges(Dictionary<string, FileSnapshot> previous, Dictionary<string, FileSnapshot> current)
        {
            var prevKeys = new HashSet<string>(previous.Keys);
            var currKeys = new HashSet<string>(current.Keys);

            var added = currKeys.Except(prevKeys).ToList();
            var removed = prevKeys.Except(currKeys).ToList();
            var maybeModified = currKeys.Intersect(prevKeys);

            var handledAdded = new HashSet<string>();
            var handledRemoved = new HashSet<string>();

            foreach (var removedPath in removed)
            {
                var oldFile = previous[removedPath];

                // Cerca tra gli added un possibile rename
                var possibleRenamedNewPath = added.FirstOrDefault(newPath =>
                {
                    var newFile = current[newPath];
                    return newFile.Length == oldFile.Length &&
                           Math.Abs((newFile.LastWriteTimeUtc - oldFile.LastWriteTimeUtc).TotalSeconds) < 2;
                });

                if (possibleRenamedNewPath != null)
                {
                    OnRenamed(removedPath, possibleRenamedNewPath);
                    handledRemoved.Add(removedPath);
                    handledAdded.Add(possibleRenamedNewPath);
                }
                else
                {
                    // Salva in memoria per un possibile match successivo
                    _recentlyDeleted[oldFile] = DateTime.UtcNow;
                }
            }

            // Cleanup dei deleted troppo vecchi
            var expiration = DateTime.UtcNow - _renameWindow;
            foreach (var old in _recentlyDeleted.ToList())
            {
                if (old.Value < expiration)
                    _recentlyDeleted.Remove(old.Key);
            }

            // Controlla se gli added matchano deleted recenti → possibile rename ritardato
            foreach (var newPath in added.Except(handledAdded))
            {
                var newFile = current[newPath];

                var matchedOld = _recentlyDeleted.Keys.FirstOrDefault(old =>
                    old.Length == newFile.Length &&
                    Math.Abs((old.LastWriteTimeUtc - newFile.LastWriteTimeUtc).TotalSeconds) < 2);

                if (matchedOld != null)
                {
                    OnRenamed(matchedOld.FullPath, newPath);
                    handledAdded.Add(newPath);
                    _recentlyDeleted.Remove(matchedOld);
                }
            }

            // Eventi Created
            foreach (var path in added.Except(handledAdded))
                OnCreated(path);

            // Eventi Deleted
            foreach (var path in removed.Except(handledRemoved))
                OnDeleted(path);

            // Eventi Changed
            foreach (var path in maybeModified)
            {
                var oldFile = previous[path];
                var newFile = current[path];

                if (oldFile.LastWriteTimeUtc != newFile.LastWriteTimeUtc || oldFile.Length != newFile.Length)
                    OnChanged(path);
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

            if (lowerPath.Contains(@"\$recycle.bin\") ||
                lowerPath.Contains(@"\system volume information\") ||
                lowerPath.Contains(@"\windows\"))
                return true;

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

            string[] ignoredFiles =
            {
                "thumbs.db", "desktop.ini", "ehthumbs.db", "iconcache.db",
                "ntuser.dat", "ntuser.dat.log1", "ntuser.dat.log2",
                "pagefile.sys", "swapfile.sys", "hiberfil.sys"
            };

            if (ignoredFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                return true;

            if (fileName.StartsWith("~") || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".~tmp", StringComparison.OrdinalIgnoreCase))
                return true;

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

            public override bool Equals(object? obj)
            {
                return obj is FileSnapshot other &&
                       Length == other.Length &&
                       LastWriteTimeUtc == other.LastWriteTimeUtc;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Length, LastWriteTimeUtc);
            }
        }
    }
}

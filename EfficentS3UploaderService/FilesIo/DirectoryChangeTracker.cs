using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EfficentS3UploadService.FilesIo
{
    internal class DirectoryChangeTracker
    {
        private readonly string _rootPath;
        private HashSet<string> _previousSnapshot;

        public DirectoryChangeTracker(string rootPath)
        {
            if (string.IsNullOrEmpty(rootPath))
                throw new ArgumentException("Root path cannot be null or empty.", nameof(rootPath));

            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Directory not found: {rootPath}");

            _rootPath = Path.GetFullPath(rootPath);
            _previousSnapshot = TakeSnapshot(_rootPath);
        }

        /// <summary>
        /// Crea uno snapshot dei file sotto _rootPath (file completi con path assoluto).
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private HashSet<string> TakeSnapshot(string path)
        {
            var allFiles = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            return new HashSet<string>(allFiles.Select(f => Path.GetFullPath(f)), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Aggiorna lo snapshot corrente con lo stato attuale della directory.
        /// </summary>
        public void RefreshSnapshot()
        {
            _previousSnapshot = TakeSnapshot(_rootPath);
        }

        /// <summary>
        /// Restituisce la lista dei file eliminati rispetto all'ultimo snapshot.
        /// </summary>
        /// <returns>Lista di percorsi completi dei file cancellati.</returns>
        public IEnumerable<string> GetDeletedFiles()
        {
            var currentSnapshot = TakeSnapshot(_rootPath);
            var deletedFiles = _previousSnapshot.Except(currentSnapshot, StringComparer.OrdinalIgnoreCase).ToList();
            _previousSnapshot = currentSnapshot;
            return deletedFiles;
        }

        /// <summary>
        /// Controlla se un path (cartella o file) era presente nello snapshot precedente.
        /// </summary>
        /// <param name="fullPath">Path assoluto da verificare</param>
        /// <returns>True se esisteva almeno un file sotto il path</returns>
        public bool WasPathDirectory(string fullPath)
        {
            fullPath = Path.GetFullPath(fullPath);
            // Aggiungiamo un separator per evitare falsi positivi (es: c:\folder1 vs c:\folder10)
            string prefix = fullPath.EndsWith(Path.DirectorySeparatorChar.ToString()) ? fullPath : fullPath + Path.DirectorySeparatorChar;

            return _previousSnapshot.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
     }
}

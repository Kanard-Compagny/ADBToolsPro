using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Encapsule les opérations sur le système de fichiers de l'appareil (listage, transfert, création,
    /// renommage, suppression) ainsi que le parsing de la sortie "ls -al". Aucune dépendance UI.
    /// </summary>
    public class FileExplorerService
    {
        private readonly AdbManager _adbManager;

        public FileExplorerService(AdbManager adbManager)
        {
            _adbManager = adbManager;
        }

        /// <summary>Liste un dossier distant. Lève une exception avec le message d'erreur ADB en cas d'échec.</summary>
        public async Task<List<AndroidFileItem>> ListDirectoryAsync(string serial, string path)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell ls -al {AdbManager.QuoteRemoteArg(path)}");

            // "ls" renvoie un code d'erreur dès qu'UNE entrée est illisible (ex. "/" ou "/data" sans root),
            // tout en listant les autres : on n'échoue que si rien n'a pu être listé.
            var files = ParseLsOutput(res.StandardOutput);
            if (!res.IsSuccess && files.Count == 0)
            {
                string err = res.StandardError?.Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(err) ? "Accès refusé ou dossier inexistant." : err);
            }

            return files;
        }

        /// <summary>Parse la sortie de "ls -al" en liste triée (dossiers d'abord, puis alphabétique). Fonction pure, testable.</summary>
        public static List<AndroidFileItem> ParseLsOutput(string lsOutput)
        {
            var files = new List<AndroidFileItem>();
            string[] lines = (lsOutput ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.StartsWith("total") || string.IsNullOrWhiteSpace(line)) continue;

                // Exemple de ligne Android : drwxrwx--- 14 system system 4096 2023-11-20 14:02 Download
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 6)
                {
                    string permissions = parts[0];
                    bool isDir = permissions.StartsWith("d") || permissions.StartsWith("l"); // l = lien symbolique (raccourci)

                    // Déduction du nom (tout ce qui vient après l'heure)
                    string name = parts.Length >= 8 ? ExtractNameAfterColumns(line, 7) : parts.Last();

                    // Lien symbolique : "sdcard -> /storage/self/primary". Seul le nom avant " -> " désigne
                    // l'entrée ; la cible collée au nom cassait la navigation, le renommage et la suppression.
                    if (permissions.StartsWith("l"))
                    {
                        int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                        if (arrow > 0) name = name.Substring(0, arrow);
                    }

                    if (name == "." || name == "..") continue; // On ignore les pointeurs de boucle Unix

                    // Déduction de la taille et date
                    string sizeStr = parts.Length >= 5 ? parts[3] : "0"; // Parfois c'est l'index 3 ou 4 selon l'OEM
                    if (!long.TryParse(sizeStr, out _)) sizeStr = parts.Length >= 5 ? parts[4] : "0"; // Fallback index 4

                    string date = parts.Length >= 7 ? $"{parts[5]} {parts[6]}" : "";

                    files.Add(new AndroidFileItem
                    {
                        Icon = isDir ? "📁" : "📄",
                        Name = name,
                        Size = isDir ? "" : FormatFileSize(sizeStr),
                        Date = date,
                        IsDirectory = isDir
                    });
                }
            }

            // Tri professionnel : Dossiers d'abord, puis fichiers, classés alphabétiquement
            return files.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name).ToList();
        }

        /// <summary>
        /// Renvoie le texte de la ligne après les <paramref name="columnsToSkip"/> premières colonnes, en
        /// conservant les espaces internes du nom (un "string.Join" des colonnes fusionnait les espaces
        /// multiples d'un nom de fichier).
        /// </summary>
        private static string ExtractNameAfterColumns(string line, int columnsToSkip)
        {
            int i = 0, column = 0;
            while (i < line.Length && column < columnsToSkip)
            {
                while (i < line.Length && line[i] == ' ') i++;   // espaces avant la colonne
                while (i < line.Length && line[i] != ' ') i++;   // la colonne elle-même
                column++;
            }
            while (i < line.Length && line[i] == ' ') i++;      // séparateur avant le nom
            return line.Substring(Math.Min(i, line.Length)).TrimEnd('\r');
        }

        /// <summary>Formateur de taille de fichier humainement lisible (Ko, Mo, Go). Fonction pure, testable.</summary>
        public static string FormatFileSize(string sizeStr)
        {
            if (long.TryParse(sizeStr, out long bytes))
            {
                if (bytes == 0) return "0 o";
                string[] suf = { "o", "Ko", "Mo", "Go", "To" };
                int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
                double num = Math.Round(bytes / Math.Pow(1024, place), 1);
                return $"{num} {suf[place]}";
            }
            return sizeStr;
        }

        public Task<AdbCommandResult> CreateFolderAsync(string serial, string fullPath) =>
            _adbManager.ExecuteAsync($"-s {serial} shell mkdir {AdbManager.QuoteRemoteArg(fullPath)}");

        public Task<AdbCommandResult> RenameAsync(string serial, string oldPath, string newPath) =>
            _adbManager.ExecuteAsync($"-s {serial} shell mv {AdbManager.QuoteRemoteArg(oldPath)} {AdbManager.QuoteRemoteArg(newPath)}");

        public Task<AdbCommandResult> DeleteAsync(string serial, string remotePath) =>
            _adbManager.ExecuteAsync($"-s {serial} shell rm -rf {AdbManager.QuoteRemoteArg(remotePath)}");
    }
}

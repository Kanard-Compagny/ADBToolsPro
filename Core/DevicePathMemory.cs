using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Mémoire des dossiers du terminal déjà choisis pour déposer des fichiers (Master) : reproposés la fois suivante,
    /// d'abord pour un fichier du même nom, puis de la même extension. Entrées « nom du fichier|dossier », la plus
    /// récente en premier, gardées dans les réglages du poste.
    /// </summary>
    public static class DevicePathMemory
    {
        public const int MaxEntries = 40;

        private static (string File, string Path) Split(string entry)
        {
            int bar = (entry ?? "").IndexOf('|');
            return bar < 0 ? ("", entry ?? "") : (entry.Substring(0, bar), entry.Substring(bar + 1));
        }

        public static string Normalize(string path)
        {
            string p = (path ?? "").Trim();
            return p.Length == 0 ? "" : p.EndsWith("/") ? p : p + "/";
        }

        /// <summary>Retient un dossier choisi pour un fichier (en tête de liste).</summary>
        public static void Remember(List<string> memory, string fileName, string path)
        {
            path = Normalize(path);
            if (memory == null || path.Length == 0) return;
            string name = System.IO.Path.GetFileName(fileName ?? "");
            memory.RemoveAll(e => { var x = Split(e); return x.File.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Path == path; });
            memory.Insert(0, name + "|" + path);
            if (memory.Count > MaxEntries) memory.RemoveRange(MaxEntries, memory.Count - MaxEntries);
        }

        /// <summary>Dossier déjà utilisé pour ce fichier (même nom), sinon pour la même extension ; vide sinon.</summary>
        public static string SuggestFor(List<string> memory, string fileName)
        {
            if (memory == null) return "";
            string name = System.IO.Path.GetFileName(fileName ?? "");
            string ext = System.IO.Path.GetExtension(name);
            foreach (var e in memory)
            {
                var x = Split(e);
                if (x.File.Equals(name, StringComparison.OrdinalIgnoreCase)) return x.Path;
            }
            if (ext.Length > 0)
                foreach (var e in memory)
                {
                    var x = Split(e);
                    if (System.IO.Path.GetExtension(x.File).Equals(ext, StringComparison.OrdinalIgnoreCase)) return x.Path;
                }
            return "";
        }

        /// <summary>Dossiers récents, sans doublon, le plus récent d'abord.</summary>
        public static List<string> Recent(List<string> memory, int count = 8) =>
            (memory ?? new List<string>()).Select(e => Split(e).Path).Where(p => p.Length > 0).Distinct().Take(count).ToList();

        /// <summary>Oublie un dossier (toutes ses entrées).</summary>
        public static void Forget(List<string> memory, string path)
        {
            path = Normalize(path);
            memory?.RemoveAll(e => Split(e).Path == path);
        }
    }
}

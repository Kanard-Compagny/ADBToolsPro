using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADBToolsNew.Core
{
    /// <summary>Résultat d'un test dans une fiche d'historique.</summary>
    public sealed class HistoryItem
    {
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Comment { get; set; } = "";
    }

    /// <summary>Fiche d'historique : un diagnostic SAV, un rapport RMA ou une analyse Wi-Fi d'un terminal.</summary>
    public sealed class HistoryEntry
    {
        public int Format { get; set; } = 1;
        /// <summary>« Diagnostic SAV », « Rapport RMA » ou « Analyse Wi-Fi ».</summary>
        public string Type { get; set; } = "";
        public DateTime Date { get; set; } = DateTime.Now;
        public string Serial { get; set; } = "";
        public string Model { get; set; } = "";
        public string Android { get; set; } = "";
        public string Technician { get; set; } = "";
        public string Client { get; set; } = "";
        public string Incident { get; set; } = "";
        public int Ok { get; set; }
        public int Warn { get; set; }
        public int Bad { get; set; }
        public string Summary { get; set; } = "";
        public List<HistoryItem> Items { get; set; } = new List<HistoryItem>();
        /// <summary>Copie du rapport rangée à côté de la fiche (nom de fichier seul), ou vide.</summary>
        public string ReportFile { get; set; } = "";

        /// <summary>Chemin de la fiche sur le disque (renseigné à la lecture).</summary>
        [JsonIgnore] public string EntryPath { get; set; }
        [JsonIgnore] public string ReportPath => string.IsNullOrEmpty(ReportFile) || EntryPath == null ? null : Path.Combine(Path.GetDirectoryName(EntryPath), ReportFile);

        /// <summary>Verdict court : « 8 conformes · 1 à surveiller · 1 non conforme ».</summary>
        [JsonIgnore]
        public string Verdict
        {
            get
            {
                if (Ok + Warn + Bad == 0) return Summary ?? "";
                var parts = new List<string>();
                if (Ok > 0) parts.Add(Ok > 1 ? Loc.F("{0} conformes", Ok) : Loc.F("{0} conforme", Ok));
                if (Warn > 0) parts.Add(Loc.F("{0} à surveiller", Warn));
                if (Bad > 0) parts.Add(Bad > 1 ? Loc.F("{0} non conformes", Bad) : Loc.F("{0} non conforme", Bad));
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>
    /// Historique des diagnostics, rangé par numéro de série (comme les dossiers SAV de Scanalyse) :
    /// &lt;dossier de l'historique&gt;\&lt;numéro de série&gt;\AAAAMMJJ_HHMMSS_&lt;type&gt;.json, avec une copie du rapport à côté.
    /// Par défaut le dossier « Historique » suit le programme (clé USB, dossier partagé) ; un dossier réseau
    /// permet à toute l'équipe de partager le même historique.
    /// </summary>
    public static class DiagHistory
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public static string DefaultFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Historique");

        public static string Folder
        {
            get
            {
                string custom = AppSettings.Current.HistoryFolder;
                return string.IsNullOrWhiteSpace(custom) ? DefaultFolder : custom;
            }
        }

        public static bool Enabled => AppSettings.Current.HistoryEnabled;

        /// <summary>Enregistre la fiche (et une copie du rapport s'il y en a un). Renvoie le chemin de la fiche.</summary>
        public static string Save(HistoryEntry entry, string reportToCopy)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            string serialDir = Path.Combine(Folder, FileNameHelper.Sanitize(string.IsNullOrWhiteSpace(entry.Serial) ? "Inconnu" : entry.Serial));
            Directory.CreateDirectory(serialDir);

            string typeTag = entry.Type.StartsWith("Analyse", StringComparison.OrdinalIgnoreCase) ? "WiFi"
                           : entry.Type.Contains("RMA") ? "RMA" : "SAV";
            string baseName = $"{entry.Date:yyyyMMdd_HHmmss}_{typeTag}";

            if (!string.IsNullOrEmpty(reportToCopy) && File.Exists(reportToCopy))
            {
                string reportName = baseName + Path.GetExtension(reportToCopy);
                string dest = Path.Combine(serialDir, reportName);
                // Le rapport est peut-être déjà enregistré dans ce dossier (dossier des rapports = historique).
                if (!string.Equals(Path.GetFullPath(reportToCopy), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(reportToCopy, dest, true);
                entry.ReportFile = reportName;
            }

            string path = Path.Combine(serialDir, baseName + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(entry, JsonOptions), new UTF8Encoding(false));
            entry.EntryPath = path;
            return path;
        }

        /// <summary>Toutes les fiches, de la plus récente à la plus ancienne (fiches illisibles ignorées).</summary>
        public static List<HistoryEntry> LoadAll(string serialFilter = null)
        {
            var list = new List<HistoryEntry>();
            string root = Folder;
            if (!Directory.Exists(root)) return list;

            IEnumerable<string> dirs = Directory.GetDirectories(root);
            if (!string.IsNullOrWhiteSpace(serialFilter))
                dirs = dirs.Where(d => string.Equals(Path.GetFileName(d), FileNameHelper.Sanitize(serialFilter), StringComparison.OrdinalIgnoreCase));

            foreach (var dir in dirs)
            {
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var e = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(file));
                        if (e == null) continue;
                        e.EntryPath = file;
                        list.Add(e);
                    }
                    catch { /* fiche abîmée ou autre fichier : ignorée */ }
                }
            }
            return list.OrderByDescending(e => e.Date).ToList();
        }

        /// <summary>
        /// Supprime les fiches (et leurs rapports) plus anciennes que la durée de conservation choisie
        /// (0 = jamais). Les dossiers de terminal vides sont retirés. Renvoie le nombre de fiches supprimées.
        /// </summary>
        public static int Purge(int retentionDays)
        {
            if (retentionDays <= 0) return 0;
            string root = Folder;
            if (!Directory.Exists(root)) return 0;
            DateTime limit = DateTime.Now.AddDays(-retentionDays);
            int removed = 0;
            foreach (var dir in Directory.GetDirectories(root))
            {
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var e = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(file));
                        if (e == null || e.Date >= limit) continue;
                        if (!string.IsNullOrEmpty(e.ReportFile))
                        {
                            string report = Path.Combine(dir, e.ReportFile);
                            if (File.Exists(report)) File.Delete(report);
                        }
                        File.Delete(file);
                        removed++;
                    }
                    catch { }
                }
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            return removed;
        }

        // ------------------------------------------------------------------ Construction des fiches

        /// <summary>Fiche d'un diagnostic SAV (résultats de chaque test, identité relue dans le test « auth »).</summary>
        public static HistoryEntry FromDiagnostic(string type, string serial, string client, string incident, IEnumerable<DiagComponent> components)
        {
            var list = components?.ToList() ?? new List<DiagComponent>();
            var auth = list.FirstOrDefault(c => c.SystemId == "auth");
            var e = new HistoryEntry
            {
                Type = type,
                Serial = serial ?? "",
                Client = client ?? "",
                Incident = incident ?? "",
                Technician = AppSettings.Current.ReportTechnician ?? "",
                Model = WordReportService.ExtractField(auth?.Details ?? "", "Modèle"),
                Android = WordReportService.ExtractField(auth?.Details ?? "", "Version Android"),
            };
            foreach (var c in list.Where(c => c.SystemId != "auth"))
            {
                e.Items.Add(new HistoryItem { Name = c.ComponentName ?? "", Status = WordReportService.ValidityLabel(c.StatusIcon), Comment = c.Comment ?? "" });
                switch (c.StatusIcon)
                {
                    case "✅": e.Ok++; break;
                    case "⚠️": e.Warn++; break;
                    case "❌": e.Bad++; break;
                }
            }
            return e;
        }
    }
}

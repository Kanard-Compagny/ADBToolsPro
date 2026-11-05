using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ADBToolsNew.Core
{
    /// <summary>Description d'un profil d'équipe (fichier profil.json à l'intérieur du .adbprofil).</summary>
    public sealed class TeamProfileInfo
    {
        public int Format { get; set; } = 1;
        public string Name { get; set; } = "";
        public string CreatedWith { get; set; } = "";
        public DateTime Created { get; set; } = DateTime.Now;

        // Réglages partagés (null = non inclus dans le profil)
        public string ReportCompany { get; set; }
        public string ReportContact { get; set; }
        public string UpdateShareFolder { get; set; }
        public string ReportsFolder { get; set; }
    }

    /// <summary>Ce que l'on choisit d'inclure à l'export.</summary>
    public sealed class TeamProfileExportOptions
    {
        public string Name { get; set; } = "";
        public bool Header { get; set; } = true;          // société, coordonnées, logo
        public bool Templates { get; set; } = true;       // modèles Word personnels
        public bool Library { get; set; } = true;         // bibliothèque de commandes ADB
        public bool UpdateShare { get; set; } = true;     // dossier de diffusion des mises à jour
        public bool ReportsFolder { get; set; }           // dossier des rapports (utile s'il est sur le réseau)
    }

    /// <summary>Profil lu depuis un fichier, prêt à être appliqué.</summary>
    public sealed class TeamProfilePackage
    {
        public TeamProfileInfo Info { get; set; }
        public byte[] SavTemplate { get; set; }
        public byte[] WifiTemplate { get; set; }
        public byte[] Logo { get; set; }
        public string LogoExtension { get; set; }
        public string LibraryJson { get; set; }
    }

    /// <summary>
    /// Profil d'équipe (comme Scanalyse) : un seul fichier .adbprofil (archive zip) qui regroupe l'en-tête des
    /// rapports, le logo, les modèles Word personnels, la bibliothèque de commandes ADB et le dossier de diffusion
    /// des mises à jour, pour configurer tous les postes d'une équipe de la même façon. Le nom du technicien n'est
    /// jamais inclus (il est propre à chaque poste).
    /// </summary>
    public static class TeamProfile
    {
        public const string Extension = ".adbprofil";
        public const string FileFilter = "Profil d'équipe ADB Tools (*.adbprofil)|*.adbprofil";

        private const string InfoEntry = "profil.json";
        private const string SavEntry = "modeles/Rapport_SAV.docx";
        private const string WifiEntry = "modeles/Rapport_Wifi.docx";
        private const string LogoEntryPrefix = "modeles/logo";
        private const string LibraryEntry = "bibliotheque.json";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public static void Export(string path, TeamProfileExportOptions options, string libraryJson, string appVersion)
        {
            var s = AppSettings.Current;
            var info = new TeamProfileInfo
            {
                Name = string.IsNullOrWhiteSpace(options.Name) ? "Profil d'équipe" : options.Name.Trim(),
                CreatedWith = "ADB Tools " + appVersion,
                Created = DateTime.Now,
                ReportCompany = options.Header ? s.ReportCompany ?? "" : null,
                ReportContact = options.Header ? s.ReportContact ?? "" : null,
                UpdateShareFolder = options.UpdateShare && !string.IsNullOrWhiteSpace(s.UpdateShareFolder) ? s.UpdateShareFolder : null,
                ReportsFolder = options.ReportsFolder && !string.IsNullOrWhiteSpace(s.ReportsFolder) ? s.ReportsFolder : null,
            };

            string tmp = path + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                WriteText(zip, InfoEntry, JsonSerializer.Serialize(info, JsonOptions));

                if (options.Templates)
                {
                    if (ReportTemplates.HasUserTemplate(ReportKind.Sav)) zip.CreateEntryFromFile(ReportTemplates.UserTemplatePath(ReportKind.Sav), SavEntry);
                    if (ReportTemplates.HasUserTemplate(ReportKind.Wifi)) zip.CreateEntryFromFile(ReportTemplates.UserTemplatePath(ReportKind.Wifi), WifiEntry);
                }
                if (options.Header)
                {
                    string logo = ReportTemplates.GetLogoPath();
                    if (logo != null) zip.CreateEntryFromFile(logo, LogoEntryPrefix + Path.GetExtension(logo).ToLowerInvariant());
                }
                if (options.Library && !string.IsNullOrWhiteSpace(libraryJson)) WriteText(zip, LibraryEntry, libraryJson);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static TeamProfilePackage Read(string path)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var infoEntry = zip.GetEntry(InfoEntry) ?? throw new InvalidDataException("Ce fichier n'est pas un profil d'équipe ADB Tools (profil.json manquant).");
                var pkg = new TeamProfilePackage
                {
                    Info = JsonSerializer.Deserialize<TeamProfileInfo>(ReadText(infoEntry)) ?? new TeamProfileInfo()
                };
                if (pkg.Info.Format > 1)
                    throw new InvalidDataException("Ce profil a été créé par une version plus récente d'ADB Tools. Mettez ADB Tools à jour pour l'importer.");

                pkg.SavTemplate = ReadBytes(zip.GetEntry(SavEntry));
                pkg.WifiTemplate = ReadBytes(zip.GetEntry(WifiEntry));
                foreach (var ext in new[] { ".png", ".jpg", ".jpeg" })
                {
                    var e = zip.GetEntry(LogoEntryPrefix + ext);
                    if (e != null) { pkg.Logo = ReadBytes(e); pkg.LogoExtension = ext; break; }
                }
                var lib = zip.GetEntry(LibraryEntry);
                if (lib != null) pkg.LibraryJson = ReadText(lib);
                return pkg;
            }
        }

        /// <summary>
        /// Applique les réglages, le logo et les modèles du profil (la bibliothèque est fusionnée par l'appelant).
        /// Les modèles personnels remplacés sont mis de côté dans Templates\_anciens, jamais supprimés.
        /// </summary>
        public static void ApplySettingsAndFiles(TeamProfilePackage pkg)
        {
            var s = AppSettings.Current;
            var info = pkg.Info;
            if (info.ReportCompany != null) s.ReportCompany = info.ReportCompany;
            if (info.ReportContact != null) s.ReportContact = info.ReportContact;
            if (!string.IsNullOrWhiteSpace(info.UpdateShareFolder)) s.UpdateShareFolder = info.UpdateShareFolder;
            if (!string.IsNullOrWhiteSpace(info.ReportsFolder)) s.ReportsFolder = info.ReportsFolder;
            s.TeamProfileName = info.Name ?? "";
            s.TeamProfileImported = DateTime.Now;
            AppSettings.Save();

            string tmpDir = Path.Combine(Path.GetTempPath(), "ADBToolsPro-profil-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                if (pkg.SavTemplate != null) ReportTemplates.Import(ReportKind.Sav, WriteTemp(tmpDir, "Rapport_SAV.docx", pkg.SavTemplate));
                if (pkg.WifiTemplate != null) ReportTemplates.Import(ReportKind.Wifi, WriteTemp(tmpDir, "Rapport_Wifi.docx", pkg.WifiTemplate));
                if (pkg.Logo != null) ReportTemplates.SetLogo(WriteTemp(tmpDir, "logo" + pkg.LogoExtension, pkg.Logo));
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ------------------------------------------------------------------ Outils

        private static string WriteTemp(string dir, string name, byte[] data)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, data);
            return p;
        }

        private static void WriteText(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text);
        }

        private static string ReadText(ZipArchiveEntry e)
        {
            using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return r.ReadToEnd();
        }

        private static byte[] ReadBytes(ZipArchiveEntry e)
        {
            if (e == null) return null;
            if (e.Length > 50L * 1024 * 1024) throw new InvalidDataException("Fichier trop volumineux dans le profil : " + e.FullName);
            using (var src = e.Open())
            using (var ms = new MemoryStream())
            {
                src.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}

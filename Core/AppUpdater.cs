using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    /// <summary>Une version disponible : publiée sur GitHub ou déposée dans le dossier de diffusion de l'équipe.</summary>
    public sealed class AppRelease
    {
        public Version Version { get; set; }
        public string Tag { get; set; }
        public string Notes { get; set; }
        public string PageUrl { get; set; }
        public string ZipUrl { get; set; }
        public string ZipName { get; set; }
        public long ZipSize { get; set; }
        public string Sha256Url { get; set; }
        public DateTime? PublishedAt { get; set; }

        /// <summary>Vrai si la version vient du dossier de diffusion de l'équipe (ZipUrl/Sha256Url sont alors des chemins).</summary>
        public bool FromShare { get; set; }

        public string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
        public string SourceLabel => FromShare ? Loc.T("dossier de l'équipe") : "GitHub";
    }

    /// <summary>Résultat d'une recherche de mise à jour, avec l'état de chaque source pour l'affichage.</summary>
    public sealed class UpdateCheck
    {
        /// <summary>Version la plus récente trouvée (toutes sources confondues), ou null.</summary>
        public AppRelease Latest { get; set; }
        /// <summary>Message sur GitHub (dépôt privé, pas de connexion...), null si tout va bien.</summary>
        public string GitHubProblem { get; set; }
        /// <summary>Message sur le dossier de l'équipe (introuvable...), null si tout va bien ou non configuré.</summary>
        public string ShareProblem { get; set; }
    }

    /// <summary>
    /// Mise à jour du programme lui-même. Deux sources, comme Scanalyse :
    /// - le dossier de diffusion de l'équipe (partage réseau où l'on dépose ADBToolsPro-X.Y.Z.zip et son .sha256) ;
    /// - les versions publiées sur GitHub (Kanard-Compagny/ADBToolsPro), lisibles seulement si le dépôt est public.
    /// La plus récente des deux est proposée. L'installation vérifie l'empreinte SHA-256, décompresse l'archive dans
    /// un dossier temporaire, puis un petit script attend la fermeture d'ADB Tools, recopie les nouveaux fichiers et
    /// relance le programme. Les données de l'utilisateur ne sont jamais touchées : l'archive ne contient que les
    /// fichiers du programme (Tools\, Templates\, Captures\, theme.txt... ne sont ni remplacés ni supprimés).
    /// </summary>
    public static class AppUpdater
    {
        public const string Repository = "Kanard-Compagny/ADBToolsPro";
        public static string ReleasesPage => $"https://github.com/{Repository}/releases";
        private static string LatestApi => $"https://api.github.com/repos/{Repository}/releases/latest";

        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ADBTools-Updater/1.0"); // obligatoire pour l'API GitHub
            return client;
        }

        /// <summary>Version du programme en cours d'exécution (X.Y.Z).</summary>
        public static Version CurrentVersion
        {
            get
            {
                var v = typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>Vérification automatique au démarrage (activée par défaut).</summary>
        public static bool AutoCheckEnabled
        {
            get => AppSettings.Current.CheckUpdatesAtStartup;
            set { AppSettings.Current.CheckUpdatesAtStartup = value; AppSettings.Save(); }
        }

        /// <summary>Dossier de diffusion de l'équipe (vide : GitHub seulement).</summary>
        public static string ShareFolder
        {
            get => AppSettings.Current.UpdateShareFolder ?? "";
            set { AppSettings.Current.UpdateShareFolder = value ?? ""; AppSettings.Save(); }
        }

        // ------------------------------------------------------------------ Recherche

        /// <summary>Interroge le dossier de l'équipe (s'il est configuré) et GitHub, et garde la version la plus récente.</summary>
        public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
        {
            var result = new UpdateCheck();
            var found = new List<AppRelease>();

            string share = ShareFolder;
            if (!string.IsNullOrWhiteSpace(share))
            {
                try
                {
                    var r = await Task.Run(() => FindInShare(share), ct).ConfigureAwait(false);
                    if (r != null) found.Add(r);
                    else result.ShareProblem = Loc.T("aucune archive ADBToolsPro-X.Y.Z.zip dans le dossier de l'équipe");
                }
                catch (Exception ex)
                {
                    result.ShareProblem = Loc.F("dossier de l'équipe inaccessible ({0})", ex.Message);
                }
            }

            try
            {
                var r = await GetLatestFromGitHubAsync(ct).ConfigureAwait(false);
                if (r != null) found.Add(r);
                else result.GitHubProblem = Loc.T("versions GitHub non accessibles (dépôt privé ou aucune version publiée)");
            }
            catch (Exception ex)
            {
                result.GitHubProblem = Loc.F("GitHub injoignable ({0})", ex.Message);
            }

            // À version égale, on préfère le dossier de l'équipe (réseau local, plus rapide).
            result.Latest = found.OrderByDescending(r => r.Version).ThenByDescending(r => r.FromShare).FirstOrDefault();
            return result;
        }

        /// <summary>Dernière version publiée sur GitHub (null si aucune, ou si le dépôt est privé).</summary>
        public static async Task<AppRelease> GetLatestFromGitHubAsync(CancellationToken ct = default)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, LatestApi))
            {
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    // 404 : aucune version publiée, ou dépôt privé (invisible sans compte).
                    if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                    resp.EnsureSuccessStatusCode();
                    string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ParseRelease(json);
                }
            }
        }

        /// <summary>Lit la réponse de l'API GitHub (séparé pour pouvoir être testé sans réseau).</summary>
        public static AppRelease ParseRelease(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
                if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;

                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (!TryParseVersion(tag, out var version)) return null;

                var release = new AppRelease
                {
                    Version = version,
                    Tag = tag,
                    Notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "",
                    PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : ReleasesPage,
                };
                if (root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(p.GetString(), out var date))
                    release.PublishedAt = date;

                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name.StartsWith("ADBToolsPro-", StringComparison.OrdinalIgnoreCase) &&
                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            release.ZipName = name;
                            release.ZipUrl = url;
                            release.ZipSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0;
                        }
                        else if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase))
                        {
                            release.Sha256Url = url;
                        }
                    }
                }
                return release;
            }
        }

        private static readonly Regex ShareZipName = new Regex(@"^ADBToolsPro-(\d+\.\d+\.\d+)\.zip$", RegexOptions.IgnoreCase);

        /// <summary>Version la plus récente déposée dans le dossier de l'équipe (null si aucune).</summary>
        public static AppRelease FindInShare(string folder)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(Loc.T("dossier introuvable"));
            AppRelease best = null;
            foreach (var path in Directory.GetFiles(folder, "ADBToolsPro-*.zip"))
            {
                var m = ShareZipName.Match(Path.GetFileName(path));
                if (!m.Success || !TryParseVersion(m.Groups[1].Value, out var v)) continue;
                if (best != null && best.Version >= v) continue;
                string sha = path + ".sha256";
                string notes = Path.Combine(folder, $"ADBToolsPro-{m.Groups[1].Value}.md");
                var fi = new FileInfo(path);
                best = new AppRelease
                {
                    Version = v,
                    Tag = "v" + m.Groups[1].Value,
                    ZipName = fi.Name,
                    ZipUrl = path,
                    ZipSize = fi.Length,
                    Sha256Url = File.Exists(sha) ? sha : null,
                    PublishedAt = fi.LastWriteTime,
                    PageUrl = folder,
                    Notes = File.Exists(notes) ? SafeRead(notes) : "",
                    FromShare = true
                };
            }
            return best;
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return ""; }
        }

        public static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim().TrimStart('v', 'V');
            int dash = s.IndexOf('-');
            if (dash >= 0) s = s.Substring(0, dash);
            if (!Version.TryParse(s, out var v)) return false;
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }

        /// <summary>Vrai si la version trouvée est plus récente que celle en cours.</summary>
        public static bool IsUpdate(AppRelease release) =>
            release != null && release.Version > CurrentVersion;

        /// <summary>
        /// Installable automatiquement : archive présente, et empreinte SHA-256 publiée à côté (obligatoire pour
        /// GitHub ; facultative dans le dossier de l'équipe, qui est un emplacement de confiance choisi par l'équipe).
        /// </summary>
        public static bool CanInstall(AppRelease release) =>
            release != null && !string.IsNullOrEmpty(release.ZipUrl) &&
            (release.FromShare || !string.IsNullOrEmpty(release.Sha256Url));

        // ------------------------------------------------------------------ Téléchargement et installation

        private static string WorkFolder => Path.Combine(Path.GetTempPath(), "ADBToolsPro-maj");

        /// <summary>
        /// Récupère l'archive (téléchargement ou copie depuis le dossier de l'équipe), vérifie son empreinte SHA-256
        /// et la décompresse. Renvoie le dossier contenant les nouveaux fichiers du programme, prêt à être recopié
        /// par <see cref="StartInstallAndExit"/>.
        /// </summary>
        public static async Task<string> DownloadAsync(AppRelease release, IProgress<double> progress, CancellationToken ct = default)
        {
            if (!CanInstall(release)) throw new InvalidOperationException(Loc.T("Cette version ne contient pas d'archive installable."));

            try { if (Directory.Exists(WorkFolder)) Directory.Delete(WorkFolder, true); } catch { }
            Directory.CreateDirectory(WorkFolder);
            string zipPath = Path.Combine(WorkFolder, release.ZipName);

            // Empreinte attendue (format sha256sum : "<hash>  <nom>")
            string expected = null;
            if (!string.IsNullOrEmpty(release.Sha256Url))
            {
                string shaText = release.FromShare
                    ? File.ReadAllText(release.Sha256Url)
                    : await Http.GetStringAsync(release.Sha256Url).ConfigureAwait(false);
                expected = shaText.Trim().Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
                if (string.IsNullOrEmpty(expected) || expected.Length != 64)
                    throw new InvalidDataException(Loc.T("Empreinte SHA-256 de la mise à jour illisible."));
            }

            if (release.FromShare)
            {
                using (var src = File.OpenRead(release.ZipUrl))
                using (var dst = File.Create(zipPath))
                    await CopyWithProgressAsync(src, dst, src.Length, progress, ct).ConfigureAwait(false);
            }
            else
            {
                using (var resp = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? release.ZipSize;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = File.Create(zipPath))
                        await CopyWithProgressAsync(src, dst, total, progress, ct).ConfigureAwait(false);
                }
            }

            if (expected != null)
            {
                string actual;
                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(zipPath))
                    actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (actual != expected)
                    throw new InvalidDataException(Loc.T("L'archive est incomplète ou modifiée (empreinte SHA-256 différente). Rien n'a été installé."));
            }

            string extractFolder = Path.Combine(WorkFolder, "fichiers");
            ZipFile.ExtractToDirectory(zipPath, extractFolder);

            // L'archive contient un dossier racine « ADBToolsPro\ » : on renvoie le dossier qui contient l'exe.
            string exeName = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName);
            string exe = Directory.GetFiles(extractFolder, exeName, SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null) throw new InvalidDataException(Loc.F("{0} introuvable dans l'archive de mise à jour.", exeName));
            return Path.GetDirectoryName(exe);
        }

        private static async Task CopyWithProgressAsync(Stream src, Stream dst, long total, IProgress<double> progress, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(done * 100.0 / total);
            }
        }

        /// <summary>
        /// Lance le script de remplacement (il attend la fermeture d'ADB Tools, recopie les fichiers puis relance
        /// le programme). L'appelant doit ensuite fermer l'application.
        /// </summary>
        public static void StartInstallAndExit(string newFilesFolder)
        {
            string appFolder = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            int pid = Process.GetCurrentProcess().Id;
            string log = Path.Combine(WorkFolder, "installation.log");
            string script = Path.Combine(WorkFolder, "installer.cmd");

            // robocopy /E : copie récursive, remplace les fichiers du programme sans jamais supprimer ceux de
            // l'utilisateur. Codes de retour 0 à 7 = succès.
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine(":attente");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto attente");
            sb.AppendLine(")");
            sb.AppendLine($"robocopy \"{newFilesFolder}\" \"{appFolder}\" /E /R:5 /W:1 /NP /NFL /NDL /LOG:\"{log}\"");
            // Échec de la copie (fichier verrouillé, droits...) : on ouvre le journal pour comprendre, et on relance
            // quand même le programme (les fichiers non copiés sont restés dans leur version précédente).
            sb.AppendLine($"if %errorlevel% GEQ 8 start \"\" notepad.exe \"{log}\"");
            sb.AppendLine($"start \"\" \"{exePath}\"");
            sb.AppendLine("exit");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = WorkFolder
            });
        }

        /// <summary>Vrai si le dossier du programme est modifiable (sinon la mise à jour automatique est impossible).</summary>
        public static bool AppFolderIsWritable()
        {
            try
            {
                string test = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $".test_ecriture_{Guid.NewGuid():N}");
                File.WriteAllText(test, "");
                File.Delete(test);
                return true;
            }
            catch { return false; }
        }
    }
}

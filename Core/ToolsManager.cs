using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    public enum ToolComponent { PlatformTools, Scrcpy, FFmpeg }

    /// <summary>Avancement d'une installation : message + pourcentage (0 à 100, ou -1 si inconnu).</summary>
    public sealed class ToolProgress
    {
        public ToolProgress(string message, double percent) { Message = message; Percent = percent; }
        public string Message { get; }
        public double Percent { get; }
    }

    /// <summary>État d'un outil tiers, pour l'affichage.</summary>
    public sealed class ToolStatus
    {
        public ToolComponent Component { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string SourceLabel { get; set; }
        public string License { get; set; }
        public bool Installed { get; set; }
        public string InstalledVersion { get; set; }
        public string LatestVersion { get; set; }
        public bool UpdateAvailable => Installed && ToolsManager.IsNewer(LatestVersion, InstalledVersion);
    }

    /// <summary>
    /// Outils tiers utilisés par ADB Tools (adb/fastboot, scrcpy, ffmpeg) : ils ne sont plus livrés avec le
    /// programme mais téléchargés depuis leurs sources officielles, dans le dossier Tools\ à côté de l'exe
    /// (programme portable). Les versions installées sont notées dans Tools\versions.txt.
    /// - ADB / Fastboot : Google (platform-tools-latest-windows.zip)
    /// - scrcpy : dernière version publiée sur GitHub (Genymobile/scrcpy)
    /// - FFmpeg : build « release essentials » de gyan.dev (téléchargé seulement si on utilise le filigrane)
    /// </summary>
    public static class ToolsManager
    {
        public static string ToolsFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");
        private static string ManifestPath => Path.Combine(ToolsFolder, "versions.txt");
        private static string LastCheckPath => Path.Combine(ToolsFolder, "derniere_verification.txt");

        private const string PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
        private const string PlatformToolsRepoXml = "https://dl.google.com/android/repository/repository2-3.xml";
        private const string ScrcpyLatestApi = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
        private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private static readonly string[] FfmpegVersionUrls =
        {
            "https://www.gyan.dev/ffmpeg/builds/release-version",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.ver"
        };

        // adb fourni par Google uniquement : scrcpy embarque aussi un adb, on garde celui de platform-tools.
        private static readonly string[] AdbFiles = { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll" };

        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            // .NET Framework : on s'assure que TLS 1.2 est autorisé (exigé par GitHub et Google).
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ADBTools-Updater/1.0"); // obligatoire pour l'API GitHub
            return client;
        }

        // ------------------------------------------------------------------ Description des outils

        public static string DisplayName(ToolComponent c)
        {
            switch (c)
            {
                case ToolComponent.PlatformTools: return "ADB & Fastboot";
                case ToolComponent.Scrcpy: return "scrcpy";
                default: return "FFmpeg";
            }
        }

        private static string Description(ToolComponent c)
        {
            switch (c)
            {
                case ToolComponent.PlatformTools: return Loc.T("Indispensable : communication avec les terminaux Android.");
                case ToolComponent.Scrcpy: return Loc.T("Affichage et contrôle de l'écran du terminal (Mirroring), enregistrement vidéo.");
                default: return Loc.T("Facultatif : incrustation du filigrane sur les vidéos enregistrées (≈ 100 Mo).");
            }
        }

        private static string SourceLabel(ToolComponent c)
        {
            switch (c)
            {
                case ToolComponent.PlatformTools: return "Google — Android SDK Platform-Tools";
                case ToolComponent.Scrcpy: return "GitHub — Genymobile/scrcpy";
                default: return "gyan.dev — FFmpeg release essentials";
            }
        }

        private static string License(ToolComponent c)
        {
            switch (c)
            {
                case ToolComponent.PlatformTools: return Loc.T("Licence : conditions du SDK Android (Google)");
                case ToolComponent.Scrcpy: return Loc.T("Licence : Apache 2.0");
                default: return Loc.T("Licence : GPL v3");
            }
        }

        private static string[] RequiredFiles(ToolComponent c)
        {
            switch (c)
            {
                case ToolComponent.PlatformTools: return new[] { "adb.exe", "fastboot.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll" };
                case ToolComponent.Scrcpy: return new[] { "scrcpy.exe", "scrcpy-server" };
                default: return new[] { "ffmpeg.exe" };
            }
        }

        public static bool IsInstalled(ToolComponent c)
        {
            return RequiredFiles(c).All(f => File.Exists(Path.Combine(ToolsFolder, f)));
        }

        /// <summary>Outils nécessaires au démarrage (FFmpeg est proposé seulement quand on en a besoin).</summary>
        public static List<ToolComponent> MissingEssentials()
        {
            return new[] { ToolComponent.PlatformTools, ToolComponent.Scrcpy }.Where(c => !IsInstalled(c)).ToList();
        }

        // ------------------------------------------------------------------ Versions

        private static Dictionary<string, string> ReadManifest()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ManifestPath)) return result;
                foreach (var line in File.ReadAllLines(ManifestPath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) result[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return result;
        }

        private static void WriteManifestVersion(ToolComponent c, string version)
        {
            var m = ReadManifest();
            m[c.ToString()] = version ?? "";
            Directory.CreateDirectory(ToolsFolder);
            File.WriteAllLines(ManifestPath, m.Select(kv => kv.Key + "=" + kv.Value));
        }

        /// <summary>Version installée : notée lors de l'installation, sinon demandée à l'outil lui-même.</summary>
        public static string GetInstalledVersion(ToolComponent c)
        {
            if (!IsInstalled(c)) return null;
            if (ReadManifest().TryGetValue(c.ToString(), out var v) && !string.IsNullOrWhiteSpace(v)) return v;

            switch (c)
            {
                case ToolComponent.PlatformTools:
                    return FirstMatch(RunAndCapture(Path.Combine(ToolsFolder, "adb.exe"), "version"), @"Version\s+(\d+(?:\.\d+)+)");
                case ToolComponent.Scrcpy:
                    return FirstMatch(RunAndCapture(Path.Combine(ToolsFolder, "scrcpy.exe"), "--version"), @"scrcpy\s+v?(\d+(?:\.\d+)+)");
                default:
                    return FirstMatch(RunAndCapture(Path.Combine(ToolsFolder, "ffmpeg.exe"), "-version"), @"ffmpeg version\s+n?(\d+(?:\.\d+)+)");
            }
        }

        /// <summary>Dernière version publiée et adresse de téléchargement (version null si inconnue).</summary>
        public static async Task<(string Version, string Url, string Sha1)> GetLatestAsync(ToolComponent c, CancellationToken ct = default)
        {
            switch (c)
            {
                case ToolComponent.PlatformTools:
                {
                    // Le catalogue officiel du SDK donne la version stable ET le nom exact de son archive Windows
                    // (avec son empreinte SHA-1). Le lien « latest » de Google peut avoir une version de retard
                    // sur ce catalogue : on télécharge donc l'archive exacte annoncée, pour rester cohérent.
                    try
                    {
                        string xml = await GetStringAsync(PlatformToolsRepoXml, ct).ConfigureAwait(false);
                        var best = ParsePlatformToolsCatalog(xml);
                        if (best.Url != null) return best;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* catalogue illisible : repli sur le lien « latest », version inconnue */ }
                    return (null, PlatformToolsUrl, null);
                }
                case ToolComponent.Scrcpy:
                {
                    string json = await GetStringAsync(ScrcpyLatestApi, ct).ConfigureAwait(false);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                        string url = null;
                        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                        {
                            string name = asset.GetProperty("name").GetString() ?? "";
                            if (Regex.IsMatch(name, @"^scrcpy-win64-v[\d.]+\.zip$", RegexOptions.IgnoreCase))
                            {
                                url = asset.GetProperty("browser_download_url").GetString();
                                break;
                            }
                        }
                        return (tag.TrimStart('v', 'V'), url, null);
                    }
                }
                default:
                {
                    string version = null;
                    foreach (var u in FfmpegVersionUrls)
                    {
                        try
                        {
                            version = FirstMatch(await GetStringAsync(u, ct).ConfigureAwait(false), @"(\d+(?:\.\d+)+)");
                            if (version != null) break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { }
                    }
                    return (version, FfmpegUrl, null);
                }
            }
        }

        /// <summary>Version stable la plus récente de platform-tools dans le catalogue du SDK Android, avec
        /// l'adresse de son archive Windows et son empreinte SHA-1. Fonction pure, testable.</summary>
        internal static (string Version, string Url, string Sha1) ParsePlatformToolsCatalog(string xml)
        {
            (string Version, string Url, string Sha1) best = (null, null, null);
            foreach (Match pkg in Regex.Matches(xml ?? "", @"<remotePackage path=""platform-tools"">(.*?)</remotePackage>", RegexOptions.Singleline))
            {
                string body = pkg.Groups[1].Value;
                var channel = Regex.Match(body, @"<channelRef ref=""([^""]+)""");
                if (channel.Success && channel.Groups[1].Value != "channel-0") continue; // versions stables uniquement

                var rev = Regex.Match(body, @"<revision>\s*<major>(\d+)</major>(?:\s*<minor>(\d+)</minor>)?(?:\s*<micro>(\d+)</micro>)?", RegexOptions.Singleline);
                if (!rev.Success) continue;
                string version = rev.Groups[1].Value + "." + (rev.Groups[2].Success ? rev.Groups[2].Value : "0") + "." + (rev.Groups[3].Success ? rev.Groups[3].Value : "0");

                foreach (Match archive in Regex.Matches(body, @"<archive>(.*?)</archive>", RegexOptions.Singleline))
                {
                    string a = archive.Groups[1].Value;
                    if (!Regex.IsMatch(a, @"<host-os>\s*windows\s*</host-os>")) continue;
                    var url = Regex.Match(a, @"<url>\s*([^<\s]+)\s*</url>");
                    if (!url.Success) continue;
                    var sha = Regex.Match(a, @"<checksum[^>]*>\s*([0-9a-fA-F]{40})\s*</checksum>");
                    if (best.Version == null || IsNewer(version, best.Version))
                    {
                        string u = url.Groups[1].Value;
                        if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) u = "https://dl.google.com/android/repository/" + u;
                        best = (version, u, sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null);
                    }
                }
            }
            return best;
        }

        /// <summary>Vrai si <paramref name="latest"/> est strictement plus récente que <paramref name="installed"/>.</summary>
        public static bool IsNewer(string latest, string installed)
        {
            var a = ParseVersion(latest);
            var b = ParseVersion(installed);
            if (a == null || b == null) return false;
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        private static int[] ParseVersion(string v)
        {
            var m = Regex.Match(v ?? "", @"\d+(?:\.\d+)*");
            if (!m.Success) return null;
            return m.Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        }

        // ------------------------------------------------------------------ États (pour l'écran Outils tiers)

        public static async Task<List<ToolStatus>> GetStatusesAsync(bool checkOnline, CancellationToken ct = default)
        {
            var list = new List<ToolStatus>();
            foreach (ToolComponent c in Enum.GetValues(typeof(ToolComponent)))
            {
                var s = new ToolStatus
                {
                    Component = c,
                    DisplayName = DisplayName(c),
                    Description = Description(c),
                    SourceLabel = SourceLabel(c),
                    License = License(c),
                    Installed = IsInstalled(c)
                };
                s.InstalledVersion = await Task.Run(() => GetInstalledVersion(c)).ConfigureAwait(false);
                if (checkOnline)
                {
                    try { s.LatestVersion = (await GetLatestAsync(c, ct).ConfigureAwait(false)).Version; }
                    catch (OperationCanceledException) { throw; }
                    catch { s.LatestVersion = null; }
                }
                list.Add(s);
            }
            return list;
        }

        /// <summary>Vérification des mises à jour, au plus une fois par 24 h (sauf si <paramref name="force"/>).
        /// Retourne les outils installés pour lesquels une version plus récente existe.</summary>
        public static async Task<List<ToolStatus>> CheckForUpdatesAsync(bool force = false, CancellationToken ct = default)
        {
            if (!force)
            {
                try
                {
                    if (File.Exists(LastCheckPath) &&
                        DateTime.TryParse(File.ReadAllText(LastCheckPath).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                        DateTime.UtcNow - last < TimeSpan.FromHours(24))
                        return new List<ToolStatus>();
                }
                catch { }
            }

            var statuses = await GetStatusesAsync(true, ct).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(ToolsFolder);
                File.WriteAllText(LastCheckPath, DateTime.UtcNow.ToString("o"));
            }
            catch { }
            return statuses.Where(s => s.UpdateAvailable).ToList();
        }

        // ------------------------------------------------------------------ Installation / mise à jour

        public static async Task<string> InstallAsync(ToolComponent c, IProgress<ToolProgress> progress, CancellationToken ct = default)
        {
            string name = DisplayName(c);
            progress?.Report(new ToolProgress(Loc.F("{0} : recherche de la dernière version…", name), -1));
            var latest = await GetLatestAsync(c, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(latest.Url))
                throw new InvalidOperationException(Loc.F("Impossible de trouver le téléchargement de {0}.", name));

            string work = Path.Combine(Path.GetTempPath(), "ADBTools_outils");
            string zipPath = Path.Combine(work, c + ".zip");
            string staging = Path.Combine(work, c + "_extrait");
            Directory.CreateDirectory(work);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);

            try
            {
                // 1. Téléchargement (80 % de la barre)
                using (var response = await Http.GetAsync(latest.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long? total = response.Content.Headers.ContentLength;
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int n;
                        int lastPercent = -1;
                        while ((n = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                            read += n;
                            if (total.HasValue && total.Value > 0)
                            {
                                int percent = (int)(read * 100 / total.Value);
                                if (percent != lastPercent)
                                {
                                    lastPercent = percent;
                                    progress?.Report(new ToolProgress(Loc.F("{0} : téléchargement… {1} / {2} Mo", name, read / 1048576, total.Value / 1048576), percent * 0.8));
                                }
                            }
                            else
                            {
                                progress?.Report(new ToolProgress(Loc.F("{0} : téléchargement… {1} Mo", name, read / 1048576), -1));
                            }
                        }
                    }
                }

                // Contrôle d'intégrité quand la source fournit une empreinte (catalogue Google)
                if (!string.IsNullOrEmpty(latest.Sha1))
                {
                    progress?.Report(new ToolProgress(Loc.F("{0} : vérification de l'archive…", name), 82));
                    string actual;
                    using (var sha = System.Security.Cryptography.SHA1.Create())
                    using (var f = File.OpenRead(zipPath))
                        actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                    if (actual != latest.Sha1)
                        throw new InvalidOperationException(Loc.F("L'archive téléchargée de {0} est corrompue (empreinte différente). Réessayez.", name));
                }

                // 2. Extraction des seuls fichiers utiles dans un dossier temporaire
                progress?.Report(new ToolProgress(Loc.F("{0} : extraction…", name), 85));
                string version = latest.Version;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // dossier
                        string[] parts = entry.FullName.Replace('\\', '/').Split('/');
                        bool take = false;
                        switch (c)
                        {
                            case ToolComponent.PlatformTools:
                                take = parts.Length == 2 && parts[0] == "platform-tools";
                                if (take && entry.Name == "source.properties")
                                {
                                    using (var r = new StreamReader(entry.Open()))
                                        version = FirstMatch(r.ReadToEnd(), @"Pkg\.Revision\s*=\s*(\d+(?:\.\d+)+)") ?? version;
                                    take = false;
                                }
                                break;
                            case ToolComponent.Scrcpy:
                                take = parts.Length == 2 && !AdbFiles.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);
                                break;
                            default:
                                take = entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) && parts.Contains("bin");
                                if (take) version = FirstMatch(entry.FullName, @"ffmpeg-n?(\d+(?:\.\d+)+)") ?? version;
                                break;
                        }
                        if (!take) continue;
                        entry.ExtractToFile(Path.Combine(staging, entry.Name), true);
                    }
                }

                foreach (var f in RequiredFiles(c))
                {
                    if (!File.Exists(Path.Combine(staging, f)))
                        throw new InvalidOperationException(Loc.F("Le fichier {0} est absent de l'archive téléchargée de {1}. Réessayez plus tard.", f, name));
                }

                // 3. Remplacement dans Tools\ (adb doit être arrêté, sinon adb.exe est verrouillé)
                progress?.Report(new ToolProgress(Loc.F("{0} : installation…", name), 92));
                Directory.CreateDirectory(ToolsFolder);
                if (c == ToolComponent.PlatformTools) StopAdbServer();

                foreach (var file in Directory.GetFiles(staging))
                {
                    string dest = Path.Combine(ToolsFolder, Path.GetFileName(file));
                    CopyWithRetry(file, dest, name);
                }

                WriteManifestVersion(c, version);
                progress?.Report(new ToolProgress(Loc.F("{0} {1} installé.", name, version), 100));
                return version;
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        private static void CopyWithRetry(string source, string dest, string toolName)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(source, dest, true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(700);
                }
                catch (IOException ex)
                {
                    throw new IOException(Loc.F("Impossible de remplacer {0} : le fichier est utilisé. ", Path.GetFileName(dest)) +
                        Loc.F("Arrêtez le mirroring / l'enregistrement en cours, puis relancez la mise à jour de {0}.", toolName), ex);
                }
            }
        }

        /// <summary>Arrête le serveur adb lancé depuis notre dossier Tools (libère adb.exe pour la mise à jour).</summary>
        internal static void StopAdbServer()
        {
            string adb = Path.Combine(ToolsFolder, "adb.exe");
            if (File.Exists(adb)) RunAndCapture(adb, "kill-server", 5000);

            foreach (var p in Process.GetProcessesByName("adb"))
            {
                try
                {
                    string path = p.MainModule?.FileName;
                    if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(adb), StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { /* processus d'un autre utilisateur ou déjà terminé */ }
                finally { p.Dispose(); }
            }
        }

        // ------------------------------------------------------------------ Utilitaires

        private static async Task<string> GetStringAsync(string url, CancellationToken ct)
        {
            using (var response = await Http.GetAsync(url, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private static string FirstMatch(string text, string pattern)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string RunAndCapture(string exe, string args, int timeoutMs = 5000)
        {
            try
            {
                if (!File.Exists(exe)) return null;
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    return stdout.Result + "\n" + stderr.Result;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

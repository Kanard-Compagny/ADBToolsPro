using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Encapsule le lancement de scrcpy (mirroring), l'enregistrement vidéo (screenrecord + ffmpeg),
    /// et la gestion du dossier local de captures. Les process lancés ici sont directement rattachés au
    /// Job Object de l'app (ChildProcessTracker), comme pour tout process externe de longue durée.
    /// </summary>
    public class MirroringService
    {
        public static string ResolveScrcpyPath(string baseFolder)
        {
            string p = Path.Combine(baseFolder, "Tools", "scrcpy.exe");
            if (File.Exists(p)) return p;
            p = Path.Combine(baseFolder, "Tools", "scrcpy", "scrcpy.exe");
            if (File.Exists(p)) return p;
            p = Path.Combine(baseFolder, "scrcpy.exe");
            if (File.Exists(p)) return p;
            return null;
        }

        public static string ResolveFfmpegPath(string baseFolder)
        {
            string p = Path.Combine(baseFolder, "Tools", "ffmpeg.exe");
            if (File.Exists(p)) return p;
            p = Path.Combine(baseFolder, "Tools", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(p)) return p;
            return null;
        }

        public static string BuildScrcpyArgs(string serial, bool turnScreenOff, bool stayAwake, bool noAudio, bool showTouches, string windowTitle)
        {
            string args = $"-s {serial} ";
            if (turnScreenOff) args += "--turn-screen-off ";
            if (stayAwake) args += "--stay-awake ";
            if (noAudio) args += "--no-audio ";
            if (showTouches) args += "--show-touches ";
            args += $"--window-title \"{windowTitle}\" ";
            return args;
        }

        /// <summary>Lance scrcpy et le rattache au Job Object. Retourne le Process démarré, ou null en cas d'échec.</summary>
        public Process StartScrcpy(string scrcpyPath, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = scrcpyPath,
                WorkingDirectory = Path.GetDirectoryName(scrcpyPath),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process != null)
            {
                ChildProcessTracker.AddProcess(process); // 🔒 Enferme scrcpy dans le Job Object de l'app
            }
            return process;
        }

        /// <summary>Lance "adb shell screenrecord" et le rattache au Job Object. Retourne le Process démarré, ou null en cas d'échec.</summary>
        public Process StartScreenRecord(string adbExePath, string serial, string remotePath, string bitRate, string sizeArg)
        {
            string args = $"-s {serial} shell screenrecord {sizeArg}--bit-rate {bitRate} {remotePath}";
            var psi = new ProcessStartInfo
            {
                FileName = adbExePath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process != null)
            {
                ChildProcessTracker.AddProcess(process); // 🔒 Évite un adb.exe zombie si l'app crashe/est tuée
            }
            return process;
        }

        public static string BuildWatermarkText(string company, string model, bool includeModel, bool includeDate)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(company)) parts.Add(company);
            if (includeModel) parts.Add(model);
            if (includeDate) parts.Add(DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
            // ':' doit être échappé pour le parseur de filtres ffmpeg ; une apostrophe casserait la valeur
            // 'text=...' entre guillemets simples si elle n'était pas échappée avec le même procédé que
            // l'échappement d'apostrophe en shell POSIX (fermer/échapper/rouvrir les guillemets).
            return string.Join(" • ", parts).Replace(":", "\\:").Replace("'", "'\\''");
        }

        /// <summary>Coordonnées FFmpeg (x,y) du filigrane selon l'index sélectionné (0=Bas-Gauche, 1=Bas-Droite, 2=Haut-Gauche, 3=Haut-Droite).</summary>
        public static string BuildWatermarkPosition(int positionIndex)
        {
            switch (positionIndex)
            {
                case 1: return "x=w-tw-20:y=h-th-20"; // Bas-Droite (w = width vidéo, tw = text width)
                case 2: return "x=20:y=20";             // Haut-Gauche
                case 3: return "x=w-tw-20:y=20";        // Haut-Droite
                default: return "x=20:y=h-th-20";       // Bas-Gauche (défaut)
            }
        }

        /// <summary>
        /// Incruste le filigrane via ffmpeg puis supprime le fichier brut. Si ffmpeg est introuvable
        /// (ffmpegPath null), se rabat sur une simple copie du fichier brut vers la destination finale.
        /// Retourne true si le filigrane a bien été incrusté, false si ffmpeg a échoué — dans ce cas,
        /// l'enregistrement brut est conservé (copié tel quel vers la destination, sans filigrane) plutôt
        /// que perdu silencieusement, et l'appelant peut prévenir le technicien de l'échec du filigrane.
        /// </summary>
        public async Task<bool> ApplyWatermarkAsync(string ffmpegPath, string rawVideoPath, string outputVideoPath, string watermarkText, string positionConfig)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                File.Copy(rawVideoPath, outputVideoPath, true);
                if (File.Exists(rawVideoPath)) File.Delete(rawVideoPath);
                return false; // Vidéo conservée, mais aucun filigrane n'a été incrusté.
            }

            // Police explicite : sans "fontfile", ffmpeg sous Windows ne trouve souvent aucune police par défaut
            // (fontconfig absent) et l'incrustation échoue.
            string fontPart = BuildFontFileOption();

            // Commande FFmpeg magique
            string ffmpegArgs = $"-y -i {AdbManager.QuoteLocalArg(rawVideoPath)} -vf \"drawtext={fontPart}text='{watermarkText}':{positionConfig}:fontcolor=white:fontsize=22:box=1:boxcolor=black@0.6:boxborderw=8\" -c:a copy {AdbManager.QuoteLocalArg(outputVideoPath)}";

            bool success = false;
            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = ffmpegArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        ADBToolsNew.ChildProcessTracker.AddProcess(p); // 🔒 Évite un ffmpeg.exe zombie si l'app crashe/est tuée en plein encodage
                        p.WaitForExit();
                        success = p.ExitCode == 0 && File.Exists(outputVideoPath);
                    }
                }
                catch { success = false; }
            });

            if (success)
            {
                if (File.Exists(rawVideoPath)) File.Delete(rawVideoPath);
            }
            else if (File.Exists(rawVideoPath))
            {
                // Le filigrane a échoué : on garde l'enregistrement brut plutôt que de le perdre.
                try { File.Copy(rawVideoPath, outputVideoPath, true); File.Delete(rawVideoPath); } catch { }
            }

            return success;
        }

        /// <summary>
        /// Option "fontfile=...:" pour drawtext, pointant sur une police Windows présente (Arial, sinon Segoe UI).
        /// Le ':' du lecteur doit être échappé pour le parseur de filtres ffmpeg ("C\\:/Windows/..."). Chaîne vide
        /// si aucune police connue n'est trouvée (ffmpeg tentera alors sa police par défaut).
        /// </summary>
        public static string BuildFontFileOption()
        {
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(fontsDir)) return "";

            foreach (var name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf" })
            {
                string path = Path.Combine(fontsDir, name);
                if (!File.Exists(path)) continue;

                string ffmpegPath = path.Replace('\\', '/').Replace(":", "\\:");
                return $"fontfile='{ffmpegPath}':";
            }
            return "";
        }

        /// <summary>Crée (si besoin) et retourne le chemin du dossier "Captures" portable, à côté de l'exécutable.</summary>
        public static string GetCaptureFolderPath(string baseFolder)
        {
            string path = Path.Combine(baseFolder, "Captures");
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return path;
        }

        public static (int Count, double Megabytes) GetCaptureFolderStats(string capturePath)
        {
            var files = new DirectoryInfo(capturePath).GetFiles();
            long totalBytes = files.Sum(f => f.Length);
            return (files.Length, totalBytes / (1024.0 * 1024.0));
        }

        public static void ClearCaptureFolder(string capturePath)
        {
            var files = new DirectoryInfo(capturePath).GetFiles();
            foreach (var f in files) f.Delete();
        }
    }
}

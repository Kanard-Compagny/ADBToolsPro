using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Exécute "adb push"/"adb pull" en lisant le flux de sortie caractère par caractère plutôt que ligne
    /// par ligne : adb met à jour sa progression avec des retours chariot ('\r'), pas des sauts de ligne, donc
    /// la lecture .NET standard (BeginOutputReadLine, qui ne se déclenche que sur '\n', voir AdbStreamProcess)
    /// ne verrait jamais les mises à jour intermédiaires, seulement le résultat final. Permet aussi
    /// l'annulation en cours de route en tuant le process.
    ///
    /// Lit stdout ET stderr en parallèle : la barre de progression restait figée lors du premier essai, très
    /// probablement parce qu'on redirigeait stderr (RedirectStandardError=true, nécessaire pour capturer les
    /// erreurs) sans jamais le lire — au mieux la progression y transite réellement et n'était donc jamais vue,
    /// au pire le tube stderr non lu finit par saturer et ralentir le process. Les deux flux sont donc traités
    /// de la même façon.
    /// ⚠️ Le format exact de la sortie de progression peut varier légèrement selon la version de
    /// platform-tools installée — à ajuster si le pourcentage ne remonte toujours pas comme attendu.
    /// </summary>
    public sealed class AdbTransferProcess
    {
        private static readonly Regex DefaultProgressRegex = new Regex(@"\[\s*(\d{1,3})%\]");
        private readonly Regex _progressRegex;
        private Process _process;

        /// <summary>Progression au format de « adb push / pull » (« [ 47%] »).</summary>
        public AdbTransferProcess() : this(null) { }

        /// <summary>Progression lue avec un autre motif (ex. « (~47%) » pour « adb sideload »), groupe 1 = pourcentage.</summary>
        public AdbTransferProcess(Regex progressRegex) { _progressRegex = progressRegex ?? DefaultProgressRegex; }

        /// <summary>Appelé pour chaque ligne lue (depuis un fil d'arrière-plan), ex. pour afficher l'étape de fastboot.</summary>
        public Action<string> LineReceived { get; set; }

        /// <summary>Lance la commande et attend sa fin, en signalant chaque mise à jour de pourcentage via <paramref name="onProgress"/>.</summary>
        public async Task<(bool Success, string Output)> RunAsync(string adbExePath, string arguments, Action<int> onProgress, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = adbExePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi };
            _process = process;
            var outputBuilder = new StringBuilder();
            var outputLock = new object();

            // try/finally : le process est libéré même si le lancement ou la lecture échoue (auparavant il ne
            // l'était que sur le chemin normal). Variable locale : le callback d'annulation ne dépend plus de _process.
            try
            {
                process.Start();
                ADBToolsNew.ChildProcessTracker.AddProcess(process); // 🔒 Évite un adb.exe zombie si l'app crashe/est tuée ou en cas d'annulation

                using (cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                {
                    var stdoutTask = Task.Run(() => ReadStreamForProgress(process.StandardOutput, outputBuilder, outputLock, onProgress, _progressRegex, LineReceived));
                    var stderrTask = Task.Run(() => ReadStreamForProgress(process.StandardError, outputBuilder, outputLock, onProgress, _progressRegex, LineReceived));

                    await Task.WhenAll(stdoutTask, stderrTask);
                    try { process.WaitForExit(); } catch { }
                }

                bool success = !cancellationToken.IsCancellationRequested && process.ExitCode == 0;
                return (success, outputBuilder.ToString());
            }
            finally
            {
                _process = null;
                process.Dispose();
            }
        }

        private static void ReadStreamForProgress(StreamReader reader, StringBuilder outputBuilder, object outputLock, Action<int> onProgress, Regex progressRegex, Action<string> onLine)
        {
            var lineBuilder = new StringBuilder();
            var buffer = new char[256];
            int read;
            try
            {
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        char c = buffer[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (lineBuilder.Length > 0)
                            {
                                string chunk = lineBuilder.ToString();
                                lock (outputLock) outputBuilder.AppendLine(chunk);
                                try { onLine?.Invoke(chunk); } catch { }
                                var m = progressRegex.Match(chunk);
                                if (m.Success && int.TryParse(m.Groups[1].Value, out int pct)) onProgress?.Invoke(pct);
                                lineBuilder.Clear();
                            }
                        }
                        else
                        {
                            lineBuilder.Append(c);
                        }
                    }
                }
                if (lineBuilder.Length > 0) lock (outputLock) outputBuilder.AppendLine(lineBuilder.ToString());
            }
            catch { /* Le process a été tué (annulation) : la lecture s'interrompt, rien à faire de plus. */ }
        }
    }
}

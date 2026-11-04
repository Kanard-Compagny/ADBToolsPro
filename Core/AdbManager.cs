using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    public class AdbManager
    {
        // Timeout par défaut pour une commande ADB "standard" (shell, getprop, dumpsys, devices...).
        // Les opérations de transfert (pull/push/install) doivent passer un timeout plus long explicitement.
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        private readonly string _adbPath;

        public AdbManager()
        {
            _adbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "adb.exe");
        }

        /// <summary>
        /// Échappe un argument pour une sous-commande adb DIRECTE (pull, push, install, uninstall, bugreport...).
        /// Ces sous-commandes ne passent par AUCUN shell distant : seul l'analyseur d'arguments Windows local
        /// (celui qui découpe la chaîne <see cref="ProcessStartInfo.Arguments"/>) doit être satisfait.
        /// </summary>
        public static string QuoteLocalArg(string value)
        {
            // Règles de découpage Windows (CommandLineToArgvW) : les antislashs ne sont spéciaux que
            // lorsqu'ils précèdent un guillemet. Ceux qui précèdent un guillemet — y compris le guillemet
            // fermant ajouté ici — doivent être doublés, sinon un chemin comme "E:\" devient "E:\"" et
            // le guillemet fermant est lu comme un guillemet littéral (argument cassé).
            value = value ?? string.Empty;
            var sb = new System.Text.StringBuilder(value.Length + 2);
            sb.Append('"');
            int pendingBackslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    pendingBackslashes++;
                    continue;
                }

                if (c == '"')
                {
                    sb.Append('\\', pendingBackslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', pendingBackslashes);
                    sb.Append(c);
                }
                pendingBackslashes = 0;
            }
            sb.Append('\\', pendingBackslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Échappe un argument destiné à une commande "adb shell ...", qui EST réinterprétée par un shell
        /// POSIX sur l'appareil Android (variables, guillemets, caractères spéciaux). Utilise le style
        /// d'échappement Unix classique (guillemets simples) qui neutralise tout sauf l'apostrophe elle-même.
        /// </summary>
        public static string QuoteRemoteArg(string value)
        {
            value = value ?? string.Empty;
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        private const string SectionMarker = "__ADBTOOLS_SECTION__";

        /// <summary>
        /// Exécute plusieurs commandes shell en UN SEUL appel "adb shell" et renvoie la sortie de chacune, dans
        /// l'ordre. Lancer adb.exe coûte 50 à 150 ms à chaque fois : regrouper 5 à 10 petites commandes
        /// (getprop, settings get, cat...) divise d'autant le temps d'attente. Une commande en échec renvoie
        /// simplement une sortie vide, sans bloquer les suivantes.
        /// </summary>
        public async Task<string[]> RunShellSectionsAsync(string serial, TimeSpan? timeout, params string[] commands)
        {
            string script = string.Join($"; echo {SectionMarker}; ", commands);
            var res = await ExecuteAsync($"-s {serial} shell {QuoteLocalArg(script)}", timeout).ConfigureAwait(false);
            return SplitSections(res.StandardOutput, commands.Length);
        }

        /// <summary>Fonction pure, testable : découpe la sortie combinée sur les marqueurs de section.</summary>
        public static string[] SplitSections(string output, int expectedCount)
        {
            var sections = new string[expectedCount];
            var parts = (output ?? "").Replace("\r\n", "\n").Split(new[] { SectionMarker }, StringSplitOptions.None);
            for (int i = 0; i < expectedCount; i++)
            {
                sections[i] = i < parts.Length ? parts[i].Trim('\n', '\r', ' ') : "";
            }
            return sections;
        }

        /// <summary>
        /// Fonction pure, testable : transforme la sortie complète de "getprop" ("[clé]: [valeur]" par ligne) en
        /// dictionnaire. Un seul "getprop" remplace ainsi 5 à 10 appels "getprop &lt;clé&gt;" successifs.
        /// </summary>
        public static System.Collections.Generic.Dictionary<string, string> ParseGetprop(string getpropOutput)
        {
            var props = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rawLine in (getpropOutput ?? "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("[")) continue;
                int keyEnd = line.IndexOf("]: [", StringComparison.Ordinal);
                if (keyEnd < 0 || !line.EndsWith("]")) continue;
                string key = line.Substring(1, keyEnd - 1);
                string value = line.Substring(keyEnd + 4, line.Length - keyEnd - 5);
                props[key] = value;
            }
            return props;
        }

        /// <summary>Lit toutes les propriétés système du terminal en un seul appel.</summary>
        public async Task<System.Collections.Generic.Dictionary<string, string>> GetAllPropsAsync(string serial)
        {
            var res = await ExecuteAsync($"-s {serial} shell getprop").ConfigureAwait(false);
            return ParseGetprop(res.StandardOutput);
        }

        public async Task<AdbCommandResult> ExecuteAsync(string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(_adbPath))
            {
                throw new FileNotFoundException($"Fichier introuvable. Chemin cherché : {_adbPath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_adbPath)
            };

            TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;

            // Syntaxe "using" classique compatible C# 7.3
            using (var process = new Process { StartInfo = startInfo })
            using (var timeoutCts = new CancellationTokenSource(effectiveTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                process.Start();
                ADBToolsNew.ChildProcessTracker.AddProcess(process); // 🔒 Couverture Job Object dès le lancement, avant même l'expiration du timeout

                // Lecture asynchrone compatible anciens frameworks
                var readOutputTask = process.StandardOutput.ReadToEndAsync();
                var readErrorTask = process.StandardError.ReadToEndAsync();

                // Si le délai (ou une annulation externe) se déclenche avant la fin naturelle du process, on le tue.
                using (linkedCts.Token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                }))
                {
                    // Contournement asynchrone pour WaitForExit()
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                }

                if (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"La commande ADB « {arguments} » n'a pas répondu dans le délai imparti ({effectiveTimeout.TotalSeconds:0}s) et a été interrompue.");
                }
                cancellationToken.ThrowIfCancellationRequested();

                return new AdbCommandResult
                {
                    StandardOutput = await readOutputTask.ConfigureAwait(false),
                    StandardError = await readErrorTask.ConfigureAwait(false),
                    ExitCode = process.ExitCode
                };
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Text;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Encapsule le cycle de vie d'un process adb.exe lancé en flux continu (logcat, getevent, ping...) :
    /// démarrage, lecture asynchrone ligne par ligne, rattachement au Job Object, et arrêt propre.
    /// Remplace le pattern Start/OutputDataReceived/Stop auparavant dupliqué à chaque usage.
    /// </summary>
    public sealed class AdbStreamProcess
    {
        private Process _process;

        public bool IsRunning => _process != null && !_process.HasExited;

        /// <summary>
        /// Démarre le process. Si une instance précédente tournait encore, elle est arrêtée d'abord par sécurité.
        /// </summary>
        public void Start(string adbExePath, string arguments, Action<string> onLine, Encoding encoding = null)
        {
            Stop();

            var psi = new ProcessStartInfo
            {
                FileName = adbExePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = encoding ?? Encoding.UTF8
            };

            _process = new Process { StartInfo = psi };
            _process.OutputDataReceived += (s, ev) =>
            {
                if (!string.IsNullOrEmpty(ev.Data))
                {
                    onLine?.Invoke(ev.Data);
                }
            };

            _process.Start();
            ADBToolsNew.ChildProcessTracker.AddProcess(_process); // 🔒 Évite un adb.exe zombie si l'app crashe/est tuée
            _process.BeginOutputReadLine();
        }

        /// <summary>
        /// Arrête et libère le process s'il est actif. Sans effet s'il ne tournait pas.
        /// </summary>
        public void Stop()
        {
            if (_process == null) return;

            try { _process.CancelOutputRead(); } catch { }
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            try { _process.Dispose(); } catch { }

            _process = null;
        }
    }
}

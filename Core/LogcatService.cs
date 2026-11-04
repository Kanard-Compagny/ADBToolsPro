using System.Collections.Generic;
using System.Text.RegularExpressions;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Encapsule la capture logcat : démarrage/arrêt du process adb et parsing des lignes en <see cref="LogEntry"/>,
    /// accumulées dans un tampon thread-safe. Ne connaît rien de l'UI — c'est à l'appelant (MainWindow.Logs.cs)
    /// de vider le tampon périodiquement et d'afficher le résultat.
    /// </summary>
    public class LogcatService
    {
        private static readonly Regex LineRegex = new Regex(
            @"^(\d{2}-\d{2}\s\d{2}:\d{2}:\d{2}\.\d{3})\s+(\d+)\s+\d+\s+([VDIWEF])\s+(.*?)\s*:\s+(.*)$");

        private readonly AdbStreamProcess _process = new AdbStreamProcess();
        private readonly object _bufferLock = new object();
        private List<LogEntry> _buffer = new List<LogEntry>();

        public bool IsRunning { get; private set; }

        public void Start(string adbExePath, string serial)
        {
            lock (_bufferLock) { _buffer.Clear(); }
            IsRunning = true;

            _process.Start(adbExePath, $"-s {serial} logcat -v threadtime", line =>
            {
                if (!IsRunning) return;

                var entry = ParseLine(line);
                lock (_bufferLock) { _buffer.Add(entry); }
            });
        }

        public void Stop()
        {
            IsRunning = false;
            _process.Stop();
        }

        /// <summary>Récupère et vide le tampon accumulé depuis le dernier appel. Thread-safe.</summary>
        public List<LogEntry> DrainBuffer()
        {
            lock (_bufferLock)
            {
                if (_buffer.Count == 0) return new List<LogEntry>();
                var batch = _buffer;
                _buffer = new List<LogEntry>();
                return batch;
            }
        }

        /// <summary>
        /// Parse une ligne brute de "logcat -v threadtime" en <see cref="LogEntry"/>.
        /// Fonction pure sans dépendance UI : testable isolément.
        /// </summary>
        public static LogEntry ParseLine(string line)
        {
            var match = LineRegex.Match(line);
            if (match.Success)
            {
                return new LogEntry
                {
                    Time = match.Groups[1].Value,
                    PID = match.Groups[2].Value,
                    Level = match.Groups[3].Value,
                    Tag = match.Groups[4].Value.Trim(),
                    Message = match.Groups[5].Value,
                    RawLine = line
                };
            }

            return new LogEntry { Time = "", PID = "", Level = "V", Tag = "", Message = line, RawLine = line };
        }
    }
}

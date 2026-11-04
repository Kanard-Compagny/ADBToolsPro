using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Détecte l'application/l'activité au premier plan sur l'appareil, avec plusieurs méthodes de repli
    /// pour couvrir Android 8 à 15. Utilisé à la fois par le sniffer du Package Manager et par le filtre
    /// "premier plan" des Logs — auparavant deux fonctionnalités distinctes appelaient en silence les mêmes
    /// méthodes privées d'un troisième fichier ; c'est maintenant un point d'entrée unique et explicite.
    /// </summary>
    public class ForegroundAppService
    {
        private readonly AdbManager _adbManager;

        public ForegroundAppService(AdbManager adbManager)
        {
            _adbManager = adbManager;
        }

        /// <summary>Ne garde QUE le nom du package au premier plan (ex: com.android.settings).</summary>
        public async Task<string> GetForegroundPackageNameAsync(string serial)
        {
            try
            {
                var res1 = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys activity activities");
                string out1 = res1.StandardOutput;
                var match1 = Regex.Match(out1, @"(?:mResumedActivity|topResumedActivity|mFocusedActivity).*?([a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)+)/");
                if (match1.Success) return match1.Groups[1].Value;

                var res2 = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys window windows");
                string out2 = res2.StandardOutput;
                var match2 = Regex.Match(out2, @"(?:mCurrentFocus|mFocusedApp|mFocusedWindow).*?([a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)+)/");
                if (match2.Success) return match2.Groups[1].Value;

                var res3 = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys activity top");
                string out3 = res3.StandardOutput;
                var match3 = Regex.Match(out3, @"ACTIVITY\s+([a-zA-Z0-9_]+(?:\.[a-zA-Z0-9_]+)+)/");
                if (match3.Success) return match3.Groups[1].Value;
            }
            catch { }
            return string.Empty;
        }

        /// <summary>Garde le Package + l'Activité (ex: com.android.settings/.Wifi), utile pour l'affichage du sniffer.</summary>
        public async Task<string> GetForegroundComponentAsync(string serial)
        {
            try
            {
                var res1 = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys activity activities");
                string out1 = res1.StandardOutput;
                var match1 = Regex.Match(out1, @"(?:mResumedActivity|topResumedActivity|mFocusedActivity).*?\s([a-zA-Z0-9_.]+\/[a-zA-Z0-9_.\$]+)");
                if (match1.Success) return match1.Groups[1].Value;

                var res2 = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys window windows");
                string out2 = res2.StandardOutput;
                var match2 = Regex.Match(out2, @"(?:mCurrentFocus|mFocusedApp|mFocusedWindow).*?\s([a-zA-Z0-9_.]+\/[a-zA-Z0-9_.\$]+)");
                if (match2.Success) return match2.Groups[1].Value;

                var res3 = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys activity top");
                string out3 = res3.StandardOutput;
                var match3 = Regex.Match(out3, @"ACTIVITY\s+([a-zA-Z0-9_.]+\/[a-zA-Z0-9_.\$]+)");
                if (match3.Success) return match3.Groups[1].Value;
            }
            catch { }
            return string.Empty;
        }

        public async Task<string> GetPidForPackageAsync(string serial, string packageName)
        {
            try
            {
                // Fallback 1 : pidof
                var resPid = await _adbManager.ExecuteAsync($"-s {serial} shell pidof {AdbManager.QuoteRemoteArg(packageName)}");
                string outputPid = resPid.StandardOutput.Trim();

                if (!string.IsNullOrEmpty(outputPid))
                {
                    var pids = outputPid.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (pids.Length > 0) return pids[0];
                }

                // Fallback 2 : ps / ps -A (Anciens terminaux & surcouches)
                var resPs = await _adbManager.ExecuteAsync($"-s {serial} shell ps -A");
                string outPs = resPs.StandardOutput;
                if (string.IsNullOrEmpty(outPs) || !outPs.Contains(packageName))
                {
                    var resPsOld = await _adbManager.ExecuteAsync($"-s {serial} shell ps");
                    outPs = resPsOld.StandardOutput;
                }

                foreach (var line in outPs.Split('\n'))
                {
                    if (line.Contains(packageName))
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 1 && int.TryParse(parts[1], out int pidVal))
                        {
                            return pidVal.ToString();
                        }
                    }
                }
            }
            catch { }

            return string.Empty;
        }
    }
}

using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ADBToolsNew
{
    /// <summary>
    /// Appli de diagnostic sur le terminal (DiagApp\ADBToolsDiag.apk), partagée par « Tests du terminal » et
    /// « Test du lecteur » : installée au premier besoin (autorisations accordées d'office : adb install -g),
    /// désinstallée quand plus aucune fenêtre ne s'en sert.
    /// </summary>
    public partial class MainWindow
    {
        private string _diagAppSerial;
        private readonly HashSet<string> _diagAppUsers = new HashSet<string>();

        /// <summary>Installe l'appli si besoin. Faux (avec message) si l'installation est refusée.</summary>
        private async Task<bool> EnsureDiagAppAsync(string serial, string user)
        {
            if (!File.Exists(DiagApp.ApkPath))
            {
                await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                    Loc.F("L'appli de diagnostic est introuvable :\n{0}\n\nElle est fournie avec les versions publiées d'ADB Tools (dossier DiagApp).", DiagApp.ApkPath), Loc.T("OK"), "", false);
                return false;
            }
            if (_diagAppSerial == serial && _diagAppUsers.Count > 0) { _diagAppUsers.Add(user); return true; }

            string apk = AdbManager.QuoteLocalArg(DiagApp.ApkPath);
            var res = await _adbManager.ExecuteAsync($"-s {serial} install -r -t -g {apk}", TimeSpan.FromSeconds(90));
            if (!(res.StandardOutput ?? "").Contains("Success"))
            {
                // Ancienne version signée autrement (chaque compilation a sa clé) : on la retire d'abord —
                // sauf si elle garde une mesure Wi-Fi sans câble pas encore récupérée.
                if (await HasPendingSurveyAsync(serial))
                {
                    await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                        Loc.T("Une mesure Wi-Fi sans câble est encore sur le terminal : récupérez-la d'abord (Analyse Wi-Fi ▸ Mesure sans câble ▸ Récupérer les mesures)."),
                        Loc.T("OK"), "", false);
                    return false;
                }
                if (await HasPendingRecordingAsync(serial))
                {
                    await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                        Loc.T("Un enregistrement terrain est encore sur le terminal : récupérez-le d'abord (Analyse de fichiers ▸ Enregistreur terrain ▸ Récupérer l'enregistrement)."),
                        Loc.T("OK"), "", false);
                    return false;
                }
                await _adbManager.ExecuteAsync($"-s {serial} uninstall {DiagApp.Package}");
                res = await _adbManager.ExecuteAsync($"-s {serial} install -t -g {apk}", TimeSpan.FromSeconds(90));
            }
            if (!(res.StandardOutput ?? "").Contains("Success"))
            {
                string detail = ((res.StandardError ?? "") + " " + (res.StandardOutput ?? "")).Trim();
                await ShowCustomDialogAsync(Loc.T("🧪 Appli de diagnostic"),
                    Loc.F("L'appli de diagnostic n'a pas pu être installée sur le terminal :\n{0}\n\nCertains terminaux gérés (MDM) interdisent l'installation d'applications par ADB.", PackageManagerService.DescribeInstallFailure(detail)),
                    Loc.T("OK"), "", false);
                return false;
            }
            _diagAppSerial = serial;
            _diagAppUsers.Add(user);
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧪 Appli de diagnostic installée sur {1}.", DateTime.Now, serial));
            return true;
        }

        private Task SendDiagCommandAsync(string serial, string cmd) =>
            _adbManager.ExecuteAsync(DiagApp.CommandArgs(serial, cmd));

        /// <summary>Plus personne ne s'en sert : l'appli est fermée et désinstallée (profil DataWedge retiré).</summary>
        private async Task ReleaseDiagAppAsync(string user)
        {
            _diagAppUsers.Remove(user);
            if (_diagAppUsers.Count > 0 || string.IsNullOrEmpty(_diagAppSerial)) return;
            string serial = _diagAppSerial;
            _diagAppSerial = null;
            if (await HasPendingSurveyAsync(serial))
            {
                // Une mesure Wi-Fi sans câble n'a pas été récupérée : désinstaller l'appli effacerait le relevé.
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🚶 Appli de diagnostic laissée sur {1} : une mesure Wi-Fi sans câble n'a pas encore été récupérée (Analyse Wi-Fi ▸ Mesure sans câble).", DateTime.Now, serial));
                return;
            }
            if (await HasPendingRecordingAsync(serial))
            {
                // Un enregistrement terrain n'a pas été récupéré : désinstaller l'appli l'effacerait.
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🎙 Appli de diagnostic laissée sur {1} : un enregistrement terrain n'a pas encore été récupéré (Analyse de fichiers ▸ Enregistreur terrain).", DateTime.Now, serial));
                return;
            }
            try
            {
                await _adbManager.ExecuteAsync($"-s {serial} shell am force-stop {DiagApp.Package}");
                await _adbManager.ExecuteAsync($"-s {serial} uninstall {DiagApp.Package}");
                await _adbManager.ExecuteAsync($"-s {serial} shell am broadcast -a com.symbol.datawedge.api.ACTION --es com.symbol.datawedge.api.DELETE_PROFILE {DiagApp.DataWedgeProfile}");
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧪 Appli de diagnostic retirée de {1}.", DateTime.Now, serial));
            }
            catch { /* terminal débranché entre-temps */ }
        }
    }
}

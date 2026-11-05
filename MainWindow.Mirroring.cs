using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using System.Text;
using System.Text.Json;
using LiveCharts;
using LiveCharts.Wpf;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ADBToolsNew
{
    public partial class MainWindow : Window
    {
        // ==========================================
        // MODULE MIRRORING & CAPTURE (SCRCPY)
        // ==========================================
        private readonly MirroringService _mirroringService;
        private System.Diagnostics.Process _scrcpyProcess;

        // Sessions scrcpy lancées via "Mirroring multiple" — distinctes de _scrcpyProcess (mono-appareil)
        // pour ne toucher à aucune logique déjà validée sur le flux mono-appareil existant.
        private readonly List<System.Diagnostics.Process> _multiScrcpyProcesses = new List<System.Diagnostics.Process>();

        /// <summary>
        /// Met à jour le badge en haut de l'onglet Mirroring pour indiquer en un coup d'œil si une fenêtre
        /// scrcpy et/ou un enregistrement vidéo sont actuellement en cours (les deux sont indépendants et
        /// peuvent tourner simultanément).
        /// </summary>
        private void UpdateMirroringStatusIndicator()
        {
            bool mirroringActive = (_scrcpyProcess != null && !_scrcpyProcess.HasExited) || _multiScrcpyProcesses.Count > 0;
            bool recordingActive = _isRecording;

            if (!mirroringActive && !recordingActive)
            {
                MirroringStatusDot.Fill = (Brush)FindResource("TextSecondary");
                TxtMirroringStatus.Text = Loc.T("Aucune session active");
            }
            else if (mirroringActive && recordingActive)
            {
                MirroringStatusDot.Fill = (Brush)FindResource("DangerColor");
                TxtMirroringStatus.Text = Loc.T("🖥️ Mirroring + 🔴 Enregistrement en cours");
            }
            else if (recordingActive)
            {
                MirroringStatusDot.Fill = (Brush)FindResource("DangerColor");
                TxtMirroringStatus.Text = Loc.T("🔴 Enregistrement en cours");
            }
            else
            {
                MirroringStatusDot.Fill = (Brush)FindResource("StatusSuccess");
                TxtMirroringStatus.Text = Loc.T("🖥️ Mirroring en cours");
            }
        }

        // Ajout du mot-clé "async" ici
        private async void BtnStartMirroring_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez connecter un appareil pour le Mirroring.", DateTime.Now));
                return;
            }

            string baseFolder = AppDomain.CurrentDomain.BaseDirectory;
            string scrcpyPath = MirroringService.ResolveScrcpyPath(baseFolder);

            if (scrcpyPath == null)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ scrcpy n'est pas installé.", DateTime.Now));
                _ = OfferToInstallToolAsync(ADBToolsNew.Core.ToolComponent.Scrcpy, "scrcpy sert à afficher et contrôler l'écran du terminal.");
                return;
            }

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📺 Préparation du Mirroring...", DateTime.Now));

            bool showTouches = ChkScrcpyShowTouches.IsChecked == true;

            try
            {
                if (showTouches)
                {
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system show_touches 1");
                }
                else
                {
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system show_touches 0");
                }

                string args = MirroringService.BuildScrcpyArgs(
                    dev.Serial,
                    turnScreenOff: ChkScrcpyTurnScreenOff.IsChecked == true,
                    stayAwake: ChkScrcpyStayAwake.IsChecked == true,
                    noAudio: ChkScrcpyNoAudio.IsChecked == true,
                    showTouches: showTouches,
                    windowTitle: $"ADB Tools Pro - {dev.DisplayName}");

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚙️ Paramètres : {1}", DateTime.Now, args));

                var process = _mirroringService.StartScrcpy(scrcpyPath, args);

                if (process != null)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Mirroring démarré !", DateTime.Now));

                    _scrcpyProcess = process;
                    UpdateMirroringStatusIndicator();

                    // 🧹 GESTION DU NETTOYAGE ROBUSTE : toujours suivie (pas seulement si showTouches), pour
                    // que le badge de session active se remette à jour dès la fermeture de la fenêtre scrcpy.
                    process.EnableRaisingEvents = true;
                    process.Exited += async (s, ev) =>
                    {
                        if (showTouches)
                        {
                            try
                            {
                                // On s'assure d'avoir bien mis "await" sur la commande ADB
                                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system show_touches 0");

                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧹 Mirroring fermé. Nettoyage effectué.", DateTime.Now));
                                });
                            }
                            catch (Exception ex)
                            {
                                _ = Application.Current.Dispatcher.InvokeAsync(() => {
                                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Info : Désactivation des touchers ignorée ({1}).", DateTime.Now, ex.Message));
                                });
                            }
                        }

                        _ = Application.Current.Dispatcher.InvokeAsync(() => {
                            if (ReferenceEquals(_scrcpyProcess, process)) _scrcpyProcess = null;
                            UpdateMirroringStatusIndicator();
                        });

                        process.Dispose(); // Libération propre
                    };
                }
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur au lancement de scrcpy : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- MIRRORING MULTIPLE : ouvre une fenêtre scrcpy par appareil sélectionné, avec les mêmes
        // options que le mirroring mono-appareil ci-dessus (mêmes cases à cocher, même nettoyage à la
        // fermeture). Dupliqué depuis BtnStartMirroring_Click plutôt que factorisé, pour ne pas risquer de
        // régression sur le flux mono-appareil déjà validé sur matériel réel. ---
        private async void BtnStartMirroringMulti_Click(object sender, RoutedEventArgs e)
        {
            string baseFolder = AppDomain.CurrentDomain.BaseDirectory;
            string scrcpyPath = MirroringService.ResolveScrcpyPath(baseFolder);
            if (scrcpyPath == null)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ scrcpy n'est pas installé.", DateTime.Now));
                _ = OfferToInstallToolAsync(ADBToolsNew.Core.ToolComponent.Scrcpy, "scrcpy sert à afficher et contrôler l'écran du terminal.");
                return;
            }

            _multiDeviceAction = async (dev) =>
            {
                bool showTouches = ChkScrcpyShowTouches.IsChecked == true;
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system show_touches {(showTouches ? 1 : 0)}");

                string args = MirroringService.BuildScrcpyArgs(
                    dev.Serial,
                    turnScreenOff: ChkScrcpyTurnScreenOff.IsChecked == true,
                    stayAwake: ChkScrcpyStayAwake.IsChecked == true,
                    noAudio: ChkScrcpyNoAudio.IsChecked == true,
                    showTouches: showTouches,
                    windowTitle: $"ADB Tools Pro - {dev.DisplayName}");

                var process = _mirroringService.StartScrcpy(scrcpyPath, args);
                if (process == null) return (false, "échec du lancement de scrcpy.");

                _multiScrcpyProcesses.Add(process);
                UpdateMirroringStatusIndicator();

                process.EnableRaisingEvents = true;
                process.Exited += async (s, ev) =>
                {
                    if (showTouches)
                    {
                        try { await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system show_touches 0"); }
                        catch { /* Terminal probablement déjà déconnecté : rien à faire de plus. */ }
                    }

                    _ = Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        _multiScrcpyProcesses.Remove(process);
                        UpdateMirroringStatusIndicator();
                    });
                    process.Dispose();
                };

                return (true, "fenêtre de mirroring ouverte.");
            };
            _multiDeviceOnComplete = null; // Pas de reste d'un usage précédent du sélecteur
            _multiDeviceConfirmBody = "Une fenêtre de mirroring (scrcpy) va s'ouvrir pour chacun des appareils suivants, avec les options actuellement cochées dans cet onglet.";
            _multiDeviceConfirmIsDanger = false;

            await OpenMultiDeviceDialogAsync("📺 Mirroring sur plusieurs appareils", null, null, false);
        }

        // ==========================================
        // MODULE ENREGISTREMENT VIDÉO & WATERMARK (FFMPEG)
        // ==========================================
        private bool _isRecording = false;
        private System.Diagnostics.Process _recordingProcess;
        private string _recordingRemotePath = "/sdcard/adbtools_rec_tmp.mp4";
        private bool _recordShowTouchesEnabled = false; // Mémorise si on a forcé les clics

        // Terminal sur lequel l'enregistrement a été LANCÉ. L'arrêt (pkill, pull, rm, show_touches) doit
        // viser ce terminal-là, même si l'utilisateur a sélectionné un autre appareil entre-temps.
        private AndroidDevice _recordingDevice;

        // "screenrecord" s'arrête seul au bout de 180 s (limite par défaut et maximale d'Android).
        private static readonly TimeSpan ScreenRecordMaxDuration = TimeSpan.FromSeconds(180);
        private DateTime _recordingStartedAt;

        private async void BtnStartRecording_Click(object sender, RoutedEventArgs e)
        {
            AndroidDevice dev;

            if (_isRecording)
            {
                dev = _recordingDevice ?? DeviceComboBox.SelectedItem as AndroidDevice;
                if (dev == null) return;
            }
            else
            {
                dev = DeviceComboBox.SelectedItem as AndroidDevice;
                if (dev == null || dev.State != "device")
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez connecter un terminal valide pour enregistrer.", DateTime.Now));
                    return;
                }
            }

            // ==========================================
            // TOGGLE 1 : ARRÊTER L'ENREGISTREMENT EN COURS
            // ==========================================
            if (_isRecording)
            {
                _isRecording = false;
                UpdateMirroringStatusIndicator();
                BtnStartRecording.Content = Loc.T("⏳ Traitement de la vidéo...");
                BtnStartRecording.IsEnabled = false;

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🛑 Arrêt de l'enregistrement en cours sur {1}...", DateTime.Now, dev.Serial));

                try
                {
                    // 1. Désactivation automatique de la pastille (si on l'avait activée)
                    if (_recordShowTouchesEnabled)
                    {
                        await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system show_touches 0");
                        _recordShowTouchesEnabled = false;
                    }

                    // 2. Interruption propre de la capture
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell pkill -2 screenrecord");

                    // On attend la fin réelle de "adb shell screenrecord" (le fichier MP4 n'est finalisé qu'à ce
                    // moment) plutôt qu'un délai fixe de 2 s, avec un plafond de 10 s par sécurité.
                    var recProc = _recordingProcess;
                    if (recProc != null)
                    {
                        await System.Threading.Tasks.Task.Run(() => { try { recProc.WaitForExit(10000); } catch { } });
                        await System.Threading.Tasks.Task.Delay(500); // laisse le terminal fermer le fichier
                    }
                    else
                    {
                        await System.Threading.Tasks.Task.Delay(2000);
                    }

                    if (DateTime.Now - _recordingStartedAt > ScreenRecordMaxDuration)
                    {
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Android limite screenrecord à 3 minutes : seules les 3 premières minutes ont été enregistrées.", DateTime.Now));
                    }

                    // ==========================================
                    // 3. LOGIQUE DE SAUVEGARDE (AUTO VS MANUELLE)
                    // ==========================================
                    string defaultVideoName = $"Video_{FileNameHelper.Sanitize(dev.Serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                    string finalVideoPath = "";

                    if (ChkAutoSaveCaptures?.IsChecked == true)
                    {
                        // Mode Auto : On génère le chemin directement vers le dossier "Captures"
                        finalVideoPath = System.IO.Path.Combine(GetCaptureFolderPath(), defaultVideoName);
                    }
                    else
                    {
                        // Mode Manuel : On demande à l'utilisateur
                        var dlg = new Microsoft.Win32.SaveFileDialog
                        {
                            FileName = defaultVideoName,
                            DefaultExt = ".mp4",
                            Filter = "Fichiers Vidéo MP4 (*.mp4)|*.mp4",
                            Title = Loc.T("Enregistrer la vidéo de démonstration")
                        };

                        if (dlg.ShowDialog() == true)
                        {
                            finalVideoPath = dlg.FileName;
                        }
                        else
                        {
                            // Annuler ne doit pas perdre l'enregistrement (auparavant la vidéo restait sur le
                            // terminal et n'était jamais récupérée) : repli sur le dossier Captures.
                            finalVideoPath = System.IO.Path.Combine(GetCaptureFolderPath(), defaultVideoName);
                            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ℹ️ Emplacement non choisi : la vidéo est conservée dans le dossier Captures.", DateTime.Now));
                        }
                    }

                    // ==========================================
                    // 4. RÉCUPÉRATION ET POST-TRAITEMENT
                    // ==========================================
                    string localRawPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "raw_capture.mp4");

                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📥 Téléchargement du flux brut...", DateTime.Now));
                    if (System.IO.File.Exists(localRawPath)) System.IO.File.Delete(localRawPath); // Pas de reste d'un enregistrement précédent
                    var pullResult = await _adbManager.ExecuteAsync($"-s {dev.Serial} pull {_recordingRemotePath} {AdbManager.QuoteLocalArg(localRawPath)}", TimeSpan.FromMinutes(5));
                    if (!pullResult.IsSuccess || !System.IO.File.Exists(localRawPath))
                    {
                        // On laisse la vidéo sur le terminal pour pouvoir la récupérer à la main.
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Récupération de la vidéo impossible ({1}). Le fichier reste sur le terminal : {2}", DateTime.Now, pullResult.StandardError?.Trim(), _recordingRemotePath));
                        return;
                    }
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell rm {_recordingRemotePath}");

                    // Lancement de FFmpeg (qui sauvegarde la vidéo au bon endroit : finalVideoPath)
                    await ApplyWatermarkWithFFmpegAsync(localRawPath, finalVideoPath, dev);

                    // Mise à jour des compteurs du dossier (X fichiers - X Mo)
                    UpdateCaptureFolderStats();
                }
                catch (Exception ex)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur : {1}", DateTime.Now, ex.Message));
                }
                finally
                {
                    // Remise à zéro du bouton
                    BtnStartRecording.Content = Loc.T("🔴 Démarrer l'enregistrement");
                    BtnStartRecording.IsEnabled = true;
                    if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();

                    // 🛑 ARRÊTE LA PULSATION DE L'ONGLET
                    AnimateNavButton(BtnNavMirroring, false);

                    // Libère le handle du process "adb shell screenrecord" maintenant qu'il est arrêté
                    // (le Job Object du ChildProcessTracker garantit déjà qu'il ne devient pas un zombie,
                    // mais le handle .NET local doit quand même être libéré après un arrêt normal).
                    _recordingProcess?.Dispose();
                    _recordingProcess = null;
                    _recordingDevice = null;
                }
                return;
            }

            // ==========================================
            // TOGGLE 2 : DÉMARRER L'ENREGISTREMENT
            // ==========================================
            try
            {
                _isRecording = true;
                _recordingDevice = dev;
                UpdateMirroringStatusIndicator();
                BtnStartRecording.Content = Loc.T("⏹️ Arrêter l'enregistrement");

                // 🚀 LANCE LA PULSATION SUR L'ONGLET "MIRRORING"
                AnimateNavButton(BtnNavMirroring, true);

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🎬 Préparation de la capture vidéo sur {1}...", DateTime.Now, dev.Serial));

                // 1. Activation des clics temporaires
                if (ChkRecordShowTouches.IsChecked == true)
                {
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system show_touches 1");
                    _recordShowTouchesEnabled = true;
                }

                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell rm -f {_recordingRemotePath}");

                // 2. Détermination de la Qualité (Bitrate)
                string bitRate = "4000000"; // Standard (4 Mbps)
                if (CmbRecordQuality.SelectedIndex == 1) bitRate = "8000000"; // Haute
                else if (CmbRecordQuality.SelectedIndex == 2) bitRate = "2000000"; // Basse

                // 3. Détermination de l'Orientation (Résolution forcée)
                string sizeArg = "";
                if (CmbRecordOrientation.SelectedIndex == 1) sizeArg = "--size 720x1280 "; // Force Portrait
                else if (CmbRecordOrientation.SelectedIndex == 2) sizeArg = "--size 1280x720 "; // Force Paysage

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚙️ Options vidéo : {1} à {2} Mbps", DateTime.Now, sizeArg.Trim(), int.Parse(bitRate) / 1000000));

                _recordingProcess = _mirroringService.StartScreenRecord(_adbExePath, dev.Serial, _recordingRemotePath, bitRate, sizeArg);
                _recordingStartedAt = DateTime.Now;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⏺️ Enregistrement actif... (Cliquez à nouveau pour stopper)", DateTime.Now));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ℹ️ Limite Android : l'enregistrement s'arrête automatiquement après 3 minutes (vers {1:HH:mm:ss}).", DateTime.Now, (_recordingStartedAt + ScreenRecordMaxDuration)));
            }
            catch (Exception ex)
            {
                _isRecording = false;
                _recordingDevice = null;
                UpdateMirroringStatusIndicator();
                BtnStartRecording.Content = Loc.T("🔴 Démarrer l'enregistrement");
                AnimateNavButton(BtnNavMirroring, false); // <-- Ajoute ça ici aussi
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur au démarrage de la vidéo : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- METHODE MOTEUR DE POST-TRAITEMENT FFMPEG (WATERMARK) ---
        private async System.Threading.Tasks.Task ApplyWatermarkWithFFmpegAsync(string rawVideoPath, string outputVideoPath, AndroidDevice device)
        {
            string baseFolder = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegPath = MirroringService.ResolveFfmpegPath(baseFolder);

            if (ffmpegPath == null)
            {
                // FFmpeg n'est plus livré (≈ 100 Mo) : on le propose ici, au moment où il sert vraiment.
                if (await OfferToInstallToolAsync(ADBToolsNew.Core.ToolComponent.FFmpeg,
                        "FFmpeg sert à incruster le filigrane (société, modèle, date) dans la vidéo. Sans lui, la vidéo est enregistrée sans filigrane."))
                {
                    ffmpegPath = MirroringService.ResolveFfmpegPath(baseFolder);
                }
            }

            if (ffmpegPath == null)
            {
                await _mirroringService.ApplyWatermarkAsync(null, rawVideoPath, outputVideoPath, null, null);
                if (System.IO.File.Exists(outputVideoPath)) OpenWithShell(outputVideoPath);
                return;
            }

            string company = TxtWatermarkCompany?.Text?.Trim() ?? "";
            bool includeModel = ChkWatermarkIncludeModel?.IsChecked == true;
            string model = TxtModel?.Text?.Trim() ?? device.Serial;
            bool includeDate = ChkWatermarkIncludeDate?.IsChecked == true;

            string watermarkText = MirroringService.BuildWatermarkText(company, model, includeModel, includeDate);

            // Détermination des coordonnées (X,Y) pour FFmpeg selon la liste déroulante
            int positionIndex = 0;
            Application.Current.Dispatcher.Invoke(() =>
            {
                positionIndex = CmbWatermarkPosition.SelectedIndex;
            });
            string positionConfig = MirroringService.BuildWatermarkPosition(positionIndex);

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🎨 Incrustation du filigrane...", DateTime.Now));

            bool watermarkOk = await _mirroringService.ApplyWatermarkAsync(ffmpegPath, rawVideoPath, outputVideoPath, watermarkText, positionConfig);

            AppendConsole(watermarkOk
                ? Loc.F("\n[{0:HH:mm:ss}] ✅ Vidéo finale générée avec succès !", DateTime.Now)
                : Loc.F("\n[{0:HH:mm:ss}] ⚠️ Échec de l'incrustation du filigrane (ffmpeg) : la vidéo est conservée sans filigrane.", DateTime.Now));
            if (System.IO.File.Exists(outputVideoPath)) OpenWithShell(outputVideoPath);
        }

        private async void BtnTakeScreenshot_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            string savePath = "";

            // Logique Auto-Save
            if (ChkAutoSaveCaptures.IsChecked == true)
            {
                savePath = System.IO.Path.Combine(GetCaptureFolderPath(), $"Screenshot_{FileNameHelper.Sanitize(dev.Serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            }
            else
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"Screenshot_{FileNameHelper.Sanitize(dev.Serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.png", DefaultExt = ".png", Filter = "Images PNG|*.png" };
                if (dlg.ShowDialog() == true) savePath = dlg.FileName;
                else return; // Annulé par l'utilisateur
            }

            BtnTakeScreenshot.IsEnabled = false;
            BtnTakeScreenshot.Content = Loc.T("⏳ Capture en cours...");
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📸 Prise de la capture d'écran...", DateTime.Now));

            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell screencap -p /sdcard/screen_tmp.png");
                await _adbManager.ExecuteAsync($"-s {dev.Serial} pull /sdcard/screen_tmp.png {AdbManager.QuoteLocalArg(savePath)}");
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell rm /sdcard/screen_tmp.png");

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Capture sauvegardée avec succès !", DateTime.Now));

                UpdateCaptureFolderStats(); // Mettre à jour les stats

                // On ouvre la photo uniquement si ce n'est pas en sauvegarde auto massive
                if (ChkAutoSaveCaptures.IsChecked == false) OpenWithShell(savePath);
            }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur : {1}", DateTime.Now, ex.Message)); }
            finally { BtnTakeScreenshot.IsEnabled = true; BtnTakeScreenshot.Content = Loc.T("📷 Capturer l'écran maintenant"); }
        }

        // ==========================================
        // --- GESTION DU DOSSIER LOCAL DE CAPTURES ---
        // ==========================================
        private string GetCaptureFolderPath()
        {
            return MirroringService.GetCaptureFolderPath(AppDomain.CurrentDomain.BaseDirectory);
        }

        private void UpdateCaptureFolderStats()
        {
            try
            {
                var stats = MirroringService.GetCaptureFolderStats(GetCaptureFolderPath());

                // Met à jour le texte dans ton XAML
                if (TxtCaptureStats != null)
                {
                    TxtCaptureStats.Text = Loc.F("{0} fichier(s)  -  {1:0.00} Mo", stats.Count, stats.Megabytes);
                }
            }
            catch { }
        }

        // Méthode manquante 1 : Ouvrir le dossier
        private void BtnOpenCaptureFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder(GetCaptureFolderPath());
        }

        // Méthode manquante 2 : Vider le dossier
        private async void BtnClearCaptureFolder_Click(object sender, RoutedEventArgs e)
        {
            bool confirm = await ShowCustomDialogAsync(Loc.T("Vider les captures"), Loc.T("Voulez-vous vraiment supprimer tous les fichiers de ce dossier ?\nCette action est irréversible."), Loc.T("Supprimer"), Loc.T("Annuler"), true);
            if (confirm)
            {
                try
                {
                    MirroringService.ClearCaptureFolder(GetCaptureFolderPath());

                    UpdateCaptureFolderStats();
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗑️ Dossier des captures vidé avec succès.", DateTime.Now));
                }
                catch (Exception ex)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur lors de la suppression : {1}", DateTime.Now, ex.Message));
                }
            }
        }
    }
}

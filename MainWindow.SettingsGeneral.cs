using ADBToolsNew.Core;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ADBToolsNew
{
    /// <summary>
    /// Paramètres ▸ Général (page ouverte au démarrage, taille de l'interface, console), ▸ À propos (diagnostic,
    /// dossier des données) et ▸ Désinstaller. Les réglages sont enregistrés par <see cref="AppSettings"/>.
    /// </summary>
    public partial class MainWindow
    {
        private bool _updatingGeneralUi;

        // --------------------------------------------------------------- Démarrage

        /// <summary>Applique les réglages d'affichage au démarrage (après le chargement des préférences).</summary>
        private void ApplyDisplaySettingsAtStartup()
        {
            // Fenêtre transparente : un dernier passage de mise en page une fois tout affiché, pour qu'aucune zone
            // ne reste non redessinée avec une taille d'interface différente de 100 %.
            if (Math.Abs(AppSettings.Current.UiScale - 1.0) > 0.01)
                Dispatcher.BeginInvoke(new Action(() => { InvalidateMeasure(); UpdateLayout(); RootBorder.InvalidateVisual(); }),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (!AppSettings.Current.ConsoleExpandedAtStartup && !_isConsoleCollapsed)
                BtnToggleConsole_Click(null, null);
        }

        /// <summary>Ouvre la page choisie dans Paramètres ▸ Général (Device Info par défaut).</summary>
        private void OpenStartPage()
        {
            try
            {
                switch (AppSettings.Current.StartPage)
                {
                    case "Mirroring": NavMirroring_Click(null, null); break;
                    case "Packages": NavPackages_Click(null, null); break;
                    case "Explorer": NavExplorer_Click(null, null); break;
                    case "Tools": NavTools_Click(null, null); break;
                    case "Diag": NavDiag_Click(null, null); break;
                    case "Radar": NavRadar_Click(null, null); break;
                    case "Library": NavLibrary_Click(null, null); break;
                    case "Logs": NavLogs_Click(null, null); break;
                    case "Compare": NavCompare_Click(null, null); break;
                    default: break; // Device Info : déjà affiché
                }
            }
            catch
            {
                // Une page qui n'arrive pas à s'ouvrir au démarrage ne doit jamais bloquer l'application.
            }
        }

        // --------------------------------------------------------------- Général

        private void UpdateGeneralUi()
        {
            _updatingGeneralUi = true;
            try
            {
                foreach (var child in StartPageChoices.Children)
                    if (child is RadioButton rb) rb.IsChecked = (rb.Tag as string) == AppSettings.Current.StartPage;
                foreach (var child in UiScaleChoices.Children)
                    if (child is RadioButton rb) rb.IsChecked = Math.Abs(ParseScale(rb.Tag) - AppSettings.Current.UiScale) < 0.01;
                ChkConsoleAtStartup.IsChecked = AppSettings.Current.ConsoleExpandedAtStartup;
                var area = SystemParameters.WorkArea;
                double rec = UiScaleAdvisor.Recommend(area.Width, area.Height);
                TxtUiScaleAdvice.Text = UiScaleAdvisor.Fits(AppSettings.Current.UiScale, area.Width, area.Height)
                    ? Loc.F("Conseillé pour cet écran : {0} %.", (int)Math.Round(rec * 100))
                    : Loc.F("Conseillé pour cet écran : {0} %. La taille actuelle est trop grande : l'affichage est à l'étroit.", (int)Math.Round(rec * 100));
                foreach (var child in LanguageChoices.Children)
                    if (child is RadioButton rb) rb.IsChecked = (rb.Tag as string) == (AppSettings.Current.Language == "en" ? "en" : "fr");
            }
            finally { _updatingGeneralUi = false; }
        }

        /// <summary>Langue de l'interface : enregistrée, puis proposée au redémarrage (comme Scanalyse).</summary>
        private async void Language_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingGeneralUi || !IsLoaded || !(sender is RadioButton rb)) return;
            string lang = rb.Tag as string == "en" ? "en" : "fr";
            if (AppSettings.Current.Language == lang) return;
            AppSettings.Current.Language = lang;
            AppSettings.Save();
            // Message dans la langue choisie : c'est celle que l'utilisateur lit le mieux.
            bool restart = lang == "en"
                ? await ShowCustomDialogAsync("🌐 Language", "English will be used after ADB Tools restarts.\n\nRestart now?", "Restart now", "Later", false)
                : await ShowCustomDialogAsync("🌐 Langue", "Le français sera utilisé après le redémarrage d'ADB Tools.\n\nRedémarrer maintenant ?", "Redémarrer maintenant", "Plus tard", false);
            if (!restart) return;
            if (_isSideloading)
            {
                await ShowCustomDialogAsync("⏳", lang == "en" ? "A sideload is in progress: restart ADB Tools once it is finished." : "Un Sideload est en cours : redémarrez ADB Tools une fois qu'il est terminé.", "OK", null, false);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
            catch { }
        }

        private void StartPage_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingGeneralUi || !IsLoaded || !(sender is RadioButton rb)) return;
            AppSettings.Current.StartPage = rb.Tag as string ?? "Dashboard";
            AppSettings.Save();
        }

        private void UiScale_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingGeneralUi || !IsLoaded || !(sender is RadioButton rb)) return;
            AppSettings.Current.UiScale = ParseScale(rb.Tag);
            AppSettings.Save();
            ApplyUiScale(AppSettings.Current.UiScale);
            UpdateGeneralUi();   // conseil de taille mis à jour
        }

        private void ChkConsoleAtStartup_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingGeneralUi || !IsLoaded) return;
            AppSettings.Current.ConsoleExpandedAtStartup = ChkConsoleAtStartup.IsChecked == true;
            AppSettings.Save();
        }

        private static double ParseScale(object tag) =>
            double.TryParse(tag as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.0;

        /// <summary>Agrandit tout le contenu de la fenêtre (100 %, 115 % ou 130 %), comme le réglage de Scanalyse.</summary>
        private double _uiScale = 1.0;

        private void ApplyUiScale(double scale)
        {
            if (AppContentGrid == null) return;
            _uiScale = scale;
            AppContentGrid.LayoutTransform = Math.Abs(scale - 1.0) < 0.01 ? Transform.Identity : new ScaleTransform(scale, scale);
            // La barre de titre (nom, réduire, agrandir, fermer) garde toujours sa taille : zoom inverse sur elle seule.
            if (TitleBarGrid != null && TitleBarRow != null)
            {
                bool one = Math.Abs(scale - 1.0) < 0.01;
                TitleBarGrid.LayoutTransform = one ? Transform.Identity : new ScaleTransform(1 / scale, 1 / scale);
                TitleBarRow.Height = new GridLength(one ? 35 : 35 / scale);
            }
            // Texte net malgré l'agrandissement / la réduction : positions arrondies au pixel près.
            AppContentGrid.UseLayoutRounding = true;
            FitScaledContent();
            UpdateRootClip();
        }

        /// <summary>
        /// Taille explicite du contenu agrandi / réduit : avec une simple LayoutTransform, WPF ne recalculait pas
        /// toujours la place disponible (au lancement notamment) et le contenu réduit à 80 % n'occupait plus que
        /// 80 % de la fenêtre. On lui donne donc exactement la taille de la fenêtre divisée par l'échelle, à chaque
        /// changement de taille de la fenêtre.
        /// </summary>
        private void FitScaledContent()
        {
            if (AppContentGrid == null || RootBorder == null) return;
            if (Math.Abs(_uiScale - 1.0) < 0.01)
            {
                AppContentGrid.Width = double.NaN;
                AppContentGrid.Height = double.NaN;
                return;
            }
            var b = RootBorder.BorderThickness;
            double w = RootBorder.ActualWidth - b.Left - b.Right;
            double h = RootBorder.ActualHeight - b.Top - b.Bottom;
            if (w <= 0 || h <= 0) return; // pas encore mesurée : SizeChanged rappellera
            AppContentGrid.Width = Math.Floor(w / _uiScale);
            AppContentGrid.Height = Math.Floor(h / _uiScale);
        }

        /// <summary>Vrai sélecteur de dossier de Windows (voir <see cref="FolderPicker"/>). Renvoie null si annulé.</summary>
        private string PickFolder(string title, string initialFolder) => FolderPicker.Pick(this, title, initialFolder);

        // --------------------------------------------------------------- Rapports : en-tête et dossier

        /// <summary>Dossier des rapports Word / RMA : celui choisi dans Paramètres, sinon le dossier des captures.</summary>
        private string GetReportsFolderPath()
        {
            string custom = AppSettings.Current.ReportsFolder;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    Directory.CreateDirectory(custom);
                    return custom;
                }
                catch
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Dossier des rapports inaccessible ({1}) : enregistrement dans le dossier des captures.", DateTime.Now, custom));
                }
            }
            return GetCaptureFolderPath();
        }

        private void UpdateReportSettingsUi()
        {
            var s = AppSettings.Current;
            if (!TxtReportCompany.IsKeyboardFocusWithin) TxtReportCompany.Text = s.ReportCompany;
            if (!TxtReportContact.IsKeyboardFocusWithin) TxtReportContact.Text = s.ReportContact;
            if (!TxtReportTechnician.IsKeyboardFocusWithin) TxtReportTechnician.Text = s.ReportTechnician;

            string logo = ReportTemplates.GetLogoPath();
            TxtReportLogo.Text = logo == null ? Loc.T("Aucun") : Path.GetFileName(logo);
            BtnReportLogoClear.Visibility = logo == null ? Visibility.Collapsed : Visibility.Visible;
            ImgReportLogo.Source = LoadImageUnlocked(logo);

            bool custom = !string.IsNullOrWhiteSpace(s.ReportsFolder);
            TxtReportsFolder.Text = custom ? s.ReportsFolder : GetCaptureFolderPath() + Loc.T("   (dossier des captures)");
            BtnReportsFolderReset.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Charge une image sans garder le fichier ouvert (on doit pouvoir la remplacer ensuite).</summary>
        private static System.Windows.Media.ImageSource LoadImageUnlocked(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelHeight = 96;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void ReportHeader_LostFocus(object sender, RoutedEventArgs e)
        {
            var s = AppSettings.Current;
            s.ReportCompany = TxtReportCompany.Text.Trim();
            s.ReportContact = TxtReportContact.Text.Trim();
            s.ReportTechnician = TxtReportTechnician.Text.Trim();
            AppSettings.Save();
        }

        private void BtnReportLogoChoose_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = Loc.T("Logo des rapports"),
                Filter = Loc.T("Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg")
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ReportTemplates.SetLogo(dlg.FileName);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🖼️ Logo des rapports mis à jour.", DateTime.Now));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Logo non enregistré : {1}", DateTime.Now, ex.Message));
            }
            UpdateReportSettingsUi();
        }

        private void BtnReportLogoClear_Click(object sender, RoutedEventArgs e)
        {
            try { ReportTemplates.ClearLogo(); }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Logo non retiré : {1}", DateTime.Now, ex.Message)); }
            UpdateReportSettingsUi();
        }

        private void BtnReportsFolderChange_Click(object sender, RoutedEventArgs e)
        {
            string folder = PickFolder("Dossier des rapports", GetReportsFolderPath());
            if (folder == null) return;
            AppSettings.Current.ReportsFolder = folder;
            AppSettings.Save();
            UpdateReportSettingsUi();
        }

        private void BtnReportsFolderOpen_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(GetReportsFolderPath()) { UseShellExecute = true }); } catch { }
        }

        private void BtnReportsFolderReset_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.ReportsFolder = "";
            AppSettings.Save();
            UpdateReportSettingsUi();
        }

        // --------------------------------------------------------------- À propos : diagnostic

        private void UpdateDiagnosticUi()
        {
            if (TxtDiagnostic != null) TxtDiagnostic.Text = BuildDiagnosticText();
        }

        /// <summary>Versions et emplacements utiles pour une demande d'aide (rien de personnel hormis les chemins).</summary>
        private string BuildDiagnosticText()
        {
            string Tool(ToolComponent c)
            {
                if (!ToolsManager.IsInstalled(c)) return "non installé";
                string v = ToolsManager.GetInstalledVersion(c);
                return string.IsNullOrEmpty(v) ? "installé" : v;
            }

            var sb = new StringBuilder();
            void Line(string label, string value) => sb.AppendLine(label.PadRight(12) + value);

            Line("ADB Tools", App.VersionText);
            Line("Windows", $"{Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64" : "32")} bits)");
            Line(".NET", DotNetFrameworkVersion());
            Line("ADB", Tool(ToolComponent.PlatformTools) + "  —  " + _adbExePath);
            Line("scrcpy", Tool(ToolComponent.Scrcpy));
            Line("FFmpeg", Tool(ToolComponent.FFmpeg));
            Line("Programme", Process.GetCurrentProcess().MainModule?.FileName ?? AppDomain.CurrentDomain.BaseDirectory);
            Line("Outils", ToolsManager.ToolsFolder);
            Line("Captures", GetCaptureFolderPath());
            if (!string.IsNullOrWhiteSpace(AppSettings.Current.ReportsFolder)) Line("Rapports", AppSettings.Current.ReportsFolder);
            Line("Historique", DiagHistory.Folder);
            Line("Réglages", AppSettings.DataFolder);
            string share = AppUpdater.ShareFolder;
            if (!string.IsNullOrWhiteSpace(share)) Line("Diffusion", share);
            return sb.ToString().TrimEnd();
        }

        /// <summary>Version du .NET Framework installée (lue dans le registre, comme le recommande Microsoft).</summary>
        private static string DotNetFrameworkVersion()
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                                            .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full\"))
                {
                    if (key?.GetValue("Release") is int release)
                    {
                        string v = release >= 533320 ? "4.8.1" : release >= 528040 ? "4.8" : release >= 461808 ? "4.7.2" :
                                   release >= 461308 ? "4.7.1" : release >= 460798 ? "4.7" : "4.6.2 ou antérieur";
                        return $"Framework {v} (release {release})";
                    }
                }
            }
            catch { }
            return "Framework " + Environment.Version;
        }

        private void BtnCopyDiagnostic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(BuildDiagnosticText());
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📋 Diagnostic copié dans le presse-papiers.", DateTime.Now));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Copie impossible : {1}", DateTime.Now, ex.Message));
            }
        }

        /// <summary>Lien de don (PayPal.me de Mr.Kanard), comme dans Scanalyse.</summary>
        private const string DonateUrl = "https://www.paypal.me/MrKanard";

        private void BtnDonate_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(DonateUrl) { UseShellExecute = true }); } catch { }
        }

        private void BtnOpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(AppSettings.DataFolder) { UseShellExecute = true }); } catch { }
        }

        // --------------------------------------------------------------- Désinstaller

        private async void BtnUninstall_Click(object sender, RoutedEventArgs e)
        {
            bool withData = ChkUninstallData.IsChecked == true;
            if (_isSideloading)
            {
                await ShowCustomDialogAsync(Loc.T("⏳ Opération en cours"), Loc.T("Une mise à jour du terminal (Sideload) est en cours. Attendez qu'elle soit terminée."), "OK", null, false);
                return;
            }

            string detail = withData
                ? "Les réglages de ce poste ET vos données (captures, vidéos, logs, rapports, modèles personnels, bibliothèque de commandes, outils téléchargés) seront définitivement supprimés."
                : "Les réglages de ce poste seront supprimés. Vos données (captures, rapports, modèles, outils téléchargés) restent dans le dossier du programme.";
            bool ok = await ShowCustomDialogAsync(Loc.T("🗑️ Désinstaller ADB Tools ?"),
                detail + Loc.T("\n\nADB Tools va se fermer, puis l'Explorateur s'ouvrira sur le dossier du programme : il ne restera qu'à le supprimer."),
                Loc.T("Désinstaller"), Loc.T("Annuler"), true);
            if (!ok) return;

            try
            {
                try { ToolsManager.StopAdbServer(); } catch { }
                StartUninstallScript(withData);
                await Task.Delay(300);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("❌ Désinstallation impossible"), ex.Message, "OK", null, true);
            }
        }

        /// <summary>
        /// Le nettoyage se fait APRÈS la fermeture (les préférences sont réécrites à la fermeture et certains
        /// fichiers sont verrouillés tant que l'application tourne) : un petit script attend la fin du processus,
        /// supprime les réglages (et les données si demandé), puis ouvre l'Explorateur sur le programme.
        /// </summary>
        private void StartUninstallScript(bool withData)
        {
            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            int pid = Process.GetCurrentProcess().Id;
            string script = Path.Combine(Path.GetTempPath(), $"ADBToolsPro-desinstaller-{pid}.cmd");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine(":attente");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("  timeout /t 1 /nobreak >nul");
            sb.AppendLine("  goto attente");
            sb.AppendLine(")");
            // Réglages : dossier %AppData%\ADBToolsPro (préférences, réglages, profil) + fichiers à côté du programme.
            sb.AppendLine($"rmdir /s /q \"{AppSettings.DataFolder}\" 2>nul");
            sb.AppendLine($"del /q \"{Path.Combine(appDir, "theme.txt")}\" 2>nul");
            sb.AppendLine($"del /q \"{Path.Combine(appDir, "crash.log")}*\" 2>nul");
            if (withData)
            {
                sb.AppendLine($"rmdir /s /q \"{GetCaptureFolderPath().TrimEnd('\\')}\" 2>nul");
                sb.AppendLine($"rmdir /s /q \"{ReportTemplates.UserFolder}\" 2>nul");
                sb.AppendLine($"rmdir /s /q \"{ToolsManager.ToolsFolder}\" 2>nul");
                sb.AppendLine($"del /q \"{Path.Combine(appDir, "AdbLibrary.json")}\" 2>nul");
                // Historique : seulement le dossier à côté du programme (jamais un dossier réseau partagé par l'équipe).
                sb.AppendLine($"rmdir /s /q \"{DiagHistory.DefaultFolder}\" 2>nul");
            }
            sb.AppendLine($"start \"\" explorer.exe /select,\"{exe}\"");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            });
        }
    }
}

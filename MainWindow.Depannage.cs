using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ADBToolsNew.Core;

namespace ADBToolsNew
{
    /// <summary>
    /// Page « Dépannage » : partir du symptôme (terminal non détecté, non autorisé, écran cassé ou tactile mort, débogage
    /// USB désactivé, recovery / fastboot, combinaisons de touches) et proposer ce qui est possible dans ce cas, pour toutes
    /// les marques. Rien n'est propre à un fabricant dans la logique : les marques ne servent qu'à nommer les appareils et à
    /// retrouver les combinaisons de touches.
    /// </summary>
    public partial class MainWindow
    {
        private const int DepannageTabIndex = 14;
        private string _depView = "detect";
        private bool _depBusy;
        private Process _depOtgProcess;

        private static readonly (string Key, string Label)[] DepViews =
        {
            ("detect", "🔌 Le terminal n'est pas détecté"),
            ("auth", "🔒 Non autorisé ou hors ligne"),
            ("screen", "📱 Écran cassé ou tactile mort"),
            ("otg", "🚫 Pas de débogage USB"),
            ("recovery", "♻️ Recovery, réinitialisation, fastboot"),
            ("keys", "⌨️ Combinaisons de touches"),
        };

        private void NavDepannage_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl == null) return;
            MainTabControl.SelectedIndex = DepannageTabIndex;
            UpdateNavButtonsUI(BtnNavDepannage);
            if (DepMenu.Children.Count == 0)
            {
                foreach (var v in DepViews)
                {
                    var rb = new RadioButton { GroupName = "DepNav", Tag = v.Key, Content = Loc.T(v.Label), Style = (Style)FindResource("SettingsNavStyle"), IsChecked = v.Key == _depView };
                    rb.Checked += (s, a) => { _depView = (string)((RadioButton)s).Tag; RenderDepannage(); };
                    DepMenu.Children.Add(rb);
                }
            }
            RenderDepannage();
        }

        private void BtnDepRefresh_Click(object sender, RoutedEventArgs e) => RenderDepannage();

        // ------------------------------------------------------------------ Éléments d'affichage

        private TextBlock DepTitle(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return t;
        }

        private TextBlock DepHeading(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4), TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            return t;
        }

        private TextBlock DepText(string text, bool muted = true, double size = 12.5)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink");
            return t;
        }

        /// <summary>Liste d'étapes numérotées (une vraie suite d'actions).</summary>
        private StackPanel DepSteps(IEnumerable<string> steps)
        {
            var p = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
            int n = 0;
            foreach (var s in steps)
            {
                n++;
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var num = new TextBlock { Text = n + ".", FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
                num.SetResourceReference(TextBlock.ForegroundProperty, "BrandText");
                g.Children.Add(num);
                var t = DepText(s, false); t.Margin = new Thickness(0);
                Grid.SetColumn(t, 1); g.Children.Add(t);
                p.Children.Add(g);
            }
            return p;
        }

        private Button DepButton(string text, Action click, string tooltip = null)
        {
            var b = new Button { Content = text, Style = (Style)FindResource("OutlineButtonStyle"), Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 8), ToolTip = tooltip };
            b.Click += (s, e) => click();
            return b;
        }

        private WrapPanel DepButtons(params Button[] buttons)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 6, 0, 2) };
            foreach (var b in buttons.Where(x => x != null)) w.Children.Add(b);
            return w;
        }

        /// <summary>Carte d'un résultat : pastille d'état, titre, explication, actions.</summary>
        private Border DepCard(string status, string title, string detail, params Button[] actions)
        {
            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 8), Margin = new Thickness(0, 6, 0, 6) };
            card.SetResourceReference(Border.BackgroundProperty, status == "ok" ? "OkBg" : status == "warn" ? "WarnBg" : status == "bad" ? "BadBg" : "Surface2");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            var p = new StackPanel();
            var t = new TextBlock { Text = (status == "ok" ? "✅ " : status == "warn" ? "⚠️ " : status == "bad" ? "❌ " : "") + title, FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, status == "ok" ? "OkFg" : status == "warn" ? "WarnFg" : status == "bad" ? "BadFg" : "Ink");
            p.Children.Add(t);
            if (!string.IsNullOrEmpty(detail)) p.Children.Add(DepText(detail, false));
            if (actions.Any(a => a != null)) p.Children.Add(DepButtons(actions));
            card.Child = p;
            return card;
        }

        private void DepGo(string view)
        {
            foreach (RadioButton rb in DepMenu.Children)
                if ((string)rb.Tag == view) { rb.IsChecked = true; return; }
        }

        private void DepBusy(bool busy)
        {
            _depBusy = busy;
            AnimateNavButton(BtnNavDepannage, busy);
            BtnDepRefresh.IsEnabled = !busy;
            if (!busy && MainTabControl.SelectedIndex == DepannageTabIndex) UpdateNavButtonsUI(BtnNavDepannage);
        }

        private void RenderDepannage()
        {
            DepContent.Children.Clear();
            DepScroll.ScrollToTop();
            switch (_depView)
            {
                case "detect": _ = RenderDepDetectAsync(); break;
                case "auth": RenderDepAuth(); break;
                case "screen": _ = RenderDepScreenAsync(); break;
                case "otg": RenderDepOtg(); break;
                case "recovery": _ = RenderDepRecoveryAsync(); break;
                default: _ = RenderDepKeysAsync(); break;
            }
        }

        // ------------------------------------------------------------------ 1. Terminal non détecté

        private async Task<(List<AndroidDevice> Adb, List<string> Fastboot, List<UsbAndroidDevice> Usb)> DepReadAllAsync()
        {
            var adb = new List<AndroidDevice>();
            try
            {
                var res = await _adbManager.ExecuteAsync("devices");
                if (res.IsSuccess) adb = ParseAdbDevices(res.StandardOutput);
            }
            catch { }
            var fb = File.Exists(FastbootExePath) ? OsUpdatePackage.ParseFastbootDevices(await RunFastbootAsync("devices", 10)) : new List<string>();
            var usb = await Task.Run(() => UsbDeviceScanner.Scan());
            return (adb, fb, usb);
        }

        private async Task RenderDepDetectAsync()
        {
            DepContent.Children.Add(DepTitle(Loc.T("🔌 Le terminal n'est pas détecté")));
            DepContent.Children.Add(DepText(Loc.T("ADB Tools compare ce que voit Windows sur l'USB et ce que voient ADB et fastboot, puis explique chaque cas : câble de charge seule, débogage USB désactivé, pilote manquant, terminal non autorisé, mode recovery ou bootloader.")));
            var wait = DepText(Loc.T("Analyse en cours…"), false);
            DepContent.Children.Add(wait);
            DepBusy(true);
            try
            {
                var (adb, fb, usb) = await DepReadAllAsync();
                if (_depView != "detect") return;
                DepContent.Children.Remove(wait);
                var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var u in usb)
                {
                    string name = (u.Brand.Length > 0 ? u.Brand + " · " : "") + u.DisplayName + (u.Serial.Length > 0 ? " · " + u.Serial : "");
                    var a = adb.FirstOrDefault(d => u.Serial.Length > 0 && d.Serial.Equals(u.Serial, StringComparison.OrdinalIgnoreCase));
                    bool inFastboot = fb.Any(s => u.Serial.Length > 0 && s.Equals(u.Serial, StringComparison.OrdinalIgnoreCase)) || (a == null && u.HasBootloaderInterface && fb.Count > 0);
                    if (a != null) shown.Add(a.Serial);
                    if (a != null) DepContent.Children.Add(DepAdbStateCard(a.Serial, a.State, name));
                    else if (inFastboot)
                        DepContent.Children.Add(DepCard("info", Loc.F("{0} : en mode bootloader (fastboot)", name), Loc.T("Le terminal attend des commandes fastboot."),
                            DepButton(Loc.T("♻️ Ouvrir Recovery / fastboot"), () => DepGo("recovery"))));
                    else if (u.DriverMissing)
                        DepContent.Children.Add(DepCard("bad", Loc.F("{0} : pilote manquant ou en erreur", name),
                            Loc.T("Windows voit le terminal mais n'a pas de pilote qui fonctionne. Installez le pilote USB du fabricant (ex. pilote USB Zebra, Honeywell, Samsung) ou le « Google USB Driver » pour les autres, puis débranchez et rebranchez."),
                            DepButton(Loc.T("Ouvrir le Gestionnaire de périphériques"), () => { try { Process.Start("devmgmt.msc"); } catch { } })));
                    else if (u.HasAdbInterface)
                        DepContent.Children.Add(DepCard("warn", Loc.F("{0} : interface ADB présente, mais ADB ne la voit pas", name),
                            Loc.T("Le débogage USB est actif côté terminal. Relancez le serveur ADB ; si ça ne suffit pas, changez de port USB."),
                            DepButton(Loc.T("↻ Relancer le serveur ADB"), async () => { await DepRestartAdbAsync(); RenderDepannage(); })));
                    else
                        DepContent.Children.Add(DepCard("warn", Loc.F("{0} : vu par Windows, mais sans débogage USB", name),
                            u.HasMtp ? Loc.T("Le terminal est branché en transfert de fichiers, sans débogage USB. Activez-le (Paramètres > Options pour les développeurs > Débogage USB). Écran inutilisable : prenez la main en clavier/souris par câble.")
                                     : Loc.T("Débogage USB désactivé, ou mode USB « Recharge seulement ». Activez le débogage USB, ou prenez la main en clavier/souris par câble si l'écran est inutilisable."),
                            DepButton(Loc.T("🚫 Pas de débogage USB : que faire ?"), () => DepGo("otg"))));
                }
                // Terminaux vus par ADB mais pas reconnus sur l'USB (Wi-Fi, ou fabricant inconnu).
                foreach (var a in adb.Where(d => !shown.Contains(d.Serial)))
                    DepContent.Children.Add(DepAdbStateCard(a.Serial, a.State, a.Serial.Contains(":") ? Loc.F("{0} (par Wi-Fi)", a.Serial) : a.Serial));
                foreach (var s in fb.Where(x => !usb.Any(u => u.Serial.Equals(x, StringComparison.OrdinalIgnoreCase))))
                    DepContent.Children.Add(DepCard("info", Loc.F("{0} : en mode bootloader (fastboot)", s), "", DepButton(Loc.T("♻️ Ouvrir Recovery / fastboot"), () => DepGo("recovery"))));

                if (usb.Count == 0 && adb.Count == 0 && fb.Count == 0)
                {
                    DepContent.Children.Add(DepCard("bad", Loc.T("Aucun terminal vu par Windows"), Loc.T("Le PC ne voit rien sur l'USB : le problème est physique ou électrique.")));
                    DepContent.Children.Add(DepHeading(Loc.T("À vérifier, dans l'ordre")));
                    DepContent.Children.Add(DepSteps(new[]
                    {
                        Loc.T("Le câble transmet les données (beaucoup de câbles ne font que la charge) : essayez-en un autre."),
                        Loc.T("Branchez directement sur le PC, sans hub ni rallonge, et essayez un autre port USB."),
                        Loc.T("Socle ou berceau : certains ne font que la charge ; branchez le câble sur le terminal pour tester."),
                        Loc.T("Le terminal est allumé et déverrouillé ; la batterie n'est pas à plat."),
                        Loc.T("Toujours rien : essayez sur un autre PC pour savoir si le problème vient du terminal ou du PC."),
                    }));
                }
                DepContent.Children.Add(DepButtons(DepButton(Loc.T("↻ Relancer le diagnostic"), RenderDepannage), DepButton(Loc.T("↻ Relancer le serveur ADB"), async () => { await DepRestartAdbAsync(); RenderDepannage(); })));
            }
            finally { DepBusy(false); }
        }

        /// <summary>Carte selon l'état ADB : device, unauthorized, offline, recovery, sideload…</summary>
        private Border DepAdbStateCard(string serial, string state, string name)
        {
            switch (state)
            {
                case "device":
                    return DepCard("ok", Loc.F("{0} : reconnu par ADB", name), Loc.T("Tout va bien côté connexion."),
                        DepButton(Loc.T("📱 Écran cassé ou tactile mort ?"), () => DepGo("screen")));
                case "unauthorized":
                    return DepCard("warn", Loc.F("{0} : non autorisé", name), Loc.T("Le terminal attend que l'on accepte « Autoriser le débogage USB » sur son écran."),
                        DepButton(Loc.T("🔒 Que faire ?"), () => DepGo("auth")));
                case "offline":
                    return DepCard("warn", Loc.F("{0} : hors ligne", name), Loc.T("ADB voit le terminal mais ne peut pas lui parler : relancez le serveur ADB, débranchez et rebranchez, ou redémarrez le terminal."),
                        DepButton(Loc.T("↻ Relancer le serveur ADB"), async () => { await DepRestartAdbAsync(); RenderDepannage(); }));
                case "recovery":
                case "sideload":
                case "rescue":
                    return DepCard("info", Loc.F("{0} : en mode {1}", name, state), Loc.T("Le terminal est en recovery : on peut lui envoyer un paquet (réinitialisation, mise à jour) ou le redémarrer."),
                        DepButton(Loc.T("♻️ Ouvrir Recovery / fastboot"), () => DepGo("recovery")));
                default:
                    return DepCard("warn", Loc.F("{0} : état « {1} »", name, state), "");
            }
        }

        private async Task DepRestartAdbAsync()
        {
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ↻ Relance du serveur ADB…", DateTime.Now));
            await _adbManager.ExecuteAsync("kill-server", TimeSpan.FromSeconds(15));
            await _adbManager.ExecuteAsync("start-server", TimeSpan.FromSeconds(30));
        }

        // ------------------------------------------------------------------ 2. Non autorisé ou hors ligne

        private void RenderDepAuth()
        {
            DepContent.Children.Add(DepTitle(Loc.T("🔒 Non autorisé ou hors ligne")));
            DepContent.Children.Add(DepText(Loc.T("« Non autorisé » : le terminal n'a pas encore accepté ce PC. « Hors ligne » : la connexion est établie mais ne répond pas.")));
            DepContent.Children.Add(DepHeading(Loc.T("L'écran du terminal est utilisable")));
            DepContent.Children.Add(DepSteps(new[]
            {
                Loc.T("Déverrouillez le terminal : la fenêtre « Autoriser le débogage USB ? » s'affiche."),
                Loc.T("Cochez « Toujours autoriser depuis cet ordinateur », puis « Autoriser »."),
                Loc.T("Pas de fenêtre : débranchez et rebranchez ; sinon, Options pour les développeurs > « Révoquer les autorisations de débogage USB », puis rebranchez."),
                Loc.T("Toujours « hors ligne » : relancez le serveur ADB, changez de câble ou de port, redémarrez le terminal."),
            }));
            DepContent.Children.Add(DepButtons(
                DepButton(Loc.T("↻ Relancer le serveur ADB"), async () => { await DepRestartAdbAsync(); AppendConsole("\n" + Loc.T("Serveur ADB relancé.")); }),
                DepButton(Loc.T("🔑 Régénérer la clé ADB de ce PC"), async () => await DepResetAdbKeyAsync(), Loc.T("Utile si l'autorisation est refusée en boucle : chaque terminal devra de nouveau autoriser ce PC."))));
            DepContent.Children.Add(DepHeading(Loc.T("L'écran est cassé ou le tactile ne répond plus")));
            DepContent.Children.Add(DepText(Loc.T("Impossible de toucher « Autoriser » ? Prenez la main en clavier et souris par le câble USB : la fenêtre se valide alors comme avec une souris branchée sur le terminal, sans débogage USB."), false));
            DepContent.Children.Add(DepButtons(DepButton(Loc.T("🖱️ Prendre la main en clavier/souris"), () => DepGo("otg"))));
        }

        /// <summary>Met de côté la clé ADB de ce PC (adbkey → .bak) : une nouvelle est créée, chaque terminal redemande l'autorisation.</summary>
        private async Task DepResetAdbKeyAsync()
        {
            if (!await ShowCustomDialogAsync(Loc.T("🔑 Régénérer la clé ADB"),
                    Loc.T("La clé ADB de ce PC sera mise de côté (renommée en .bak) et une nouvelle sera créée. Tous les terminaux devront de nouveau autoriser ce PC. Continuer ?"),
                    Loc.T("Régénérer"), Loc.T("Annuler"), false)) return;
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android");
            await _adbManager.ExecuteAsync("kill-server", TimeSpan.FromSeconds(15));
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            foreach (var f in new[] { "adbkey", "adbkey.pub" })
            {
                string p = Path.Combine(dir, f);
                try { if (File.Exists(p)) File.Move(p, p + "." + stamp + ".bak"); } catch (Exception ex) { AppendConsole("\n" + ex.Message); }
            }
            await _adbManager.ExecuteAsync("start-server", TimeSpan.FromSeconds(30));
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔑 Nouvelle clé ADB créée (ancienne gardée dans {1}). Acceptez l'autorisation sur chaque terminal.", DateTime.Now, dir));
        }

        // ------------------------------------------------------------------ 3. Écran cassé ou tactile mort (débogage USB actif)

        private ComboBox _depDeviceCombo;

        private async Task<ComboBox> DepDevicePickerAsync()
        {
            var res = await _adbManager.ExecuteAsync("devices");
            var devices = (res.IsSuccess ? ParseAdbDevices(res.StandardOutput) : new List<AndroidDevice>()).Where(d => d.State == "device").ToList();
            var cmb = new ComboBox { Height = 30, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
            foreach (var d in devices)
            {
                string model = "";
                try { model = (await _adbManager.RunShellSectionsAsync(d.Serial, TimeSpan.FromSeconds(8), "getprop ro.product.model"))[0]; } catch { }
                cmb.Items.Add(new ComboBoxItem { Content = (model.Length > 0 ? model + " — " : "") + d.Serial, Tag = d.Serial });
            }
            string current = (DeviceComboBox.SelectedItem as AndroidDevice)?.Serial;
            foreach (ComboBoxItem it in cmb.Items) if ((string)it.Tag == current) cmb.SelectedItem = it;
            if (cmb.SelectedItem == null && cmb.Items.Count > 0) cmb.SelectedIndex = 0;
            return cmb;
        }

        private string DepSerial => (_depDeviceCombo?.SelectedItem as ComboBoxItem)?.Tag as string;

        private async Task DepShellAsync(string cmd)
        {
            string serial = DepSerial;
            if (serial == null) return;
            await _adbManager.ExecuteAsync($"-s {serial} shell {cmd}", TimeSpan.FromSeconds(20));
        }

        private async Task RenderDepScreenAsync()
        {
            DepContent.Children.Add(DepTitle(Loc.T("📱 Écran cassé ou tactile mort")));
            DepContent.Children.Add(DepText(Loc.T("Le débogage USB est actif et le terminal est autorisé : on peut tout faire depuis le PC. (Pas de débogage USB : voir « Pas de débogage USB ».)")));
            _depDeviceCombo = await DepDevicePickerAsync();
            if (_depView != "screen") return;
            if (_depDeviceCombo.Items.Count == 0)
            {
                DepContent.Children.Add(DepCard("warn", Loc.T("Aucun terminal autorisé n'est branché"), Loc.T("Branchez le terminal ; s'il n'apparaît pas, passez par « Le terminal n'est pas détecté »."),
                    DepButton(Loc.T("🔌 Diagnostic de connexion"), () => DepGo("detect"))));
                return;
            }
            DepContent.Children.Add(_depDeviceCombo);

            DepContent.Children.Add(DepHeading(Loc.T("Voir et contrôler l'écran sur le PC")));
            DepContent.Children.Add(DepText(Loc.T("L'écran du terminal s'affiche dans une fenêtre : la souris remplace le tactile, le clavier du PC tape sur le terminal. Fonctionne même écran noir ou cassé.")));
            DepContent.Children.Add(DepButtons(DepButton(Loc.T("🖥️ Afficher et contrôler"), () =>
            {
                string serial = DepSerial;
                var dev = DeviceComboBox.Items.OfType<AndroidDevice>().FirstOrDefault(d => d.Serial == serial);
                if (dev != null) DeviceComboBox.SelectedItem = dev;
                BtnStartMirroring_Click(null, null);
            })));

            DepContent.Children.Add(DepHeading(Loc.T("Touches à distance")));
            DepContent.Children.Add(DepButtons(
                DepButton(Loc.T("💡 Réveiller"), async () => await DepShellAsync("input keyevent KEYCODE_WAKEUP")),
                DepButton(Loc.T("🔓 Déverrouiller (sans code)"), async () => { await DepShellAsync("input keyevent KEYCODE_WAKEUP"); await DepShellAsync("wm dismiss-keyguard"); await DepShellAsync("input keyevent 82"); }),
                DepButton(Loc.T("🔢 Saisir le code…"), async () =>
                {
                    if (!await ShowCustomDialogAsync(Loc.T("🔢 Code de déverrouillage"), Loc.T("Code PIN ou mot de passe du terminal (il n'est pas enregistré) :"), Loc.T("Envoyer"), Loc.T("Annuler"), false, true, "")) return;
                    string code = TxtDialogInput.Text;
                    if (code.Length == 0) return;
                    await DepShellAsync("input keyevent KEYCODE_WAKEUP");
                    await DepShellAsync("wm dismiss-keyguard");
                    await Task.Delay(600);
                    await DepShellAsync("input text " + MasterProfile.ShellQuote(code));
                    await DepShellAsync("input keyevent KEYCODE_ENTER");
                }),
                DepButton(Loc.T("🏠 Accueil"), async () => await DepShellAsync("input keyevent KEYCODE_HOME")),
                DepButton(Loc.T("◀ Retour"), async () => await DepShellAsync("input keyevent KEYCODE_BACK")),
                DepButton(Loc.T("🗂️ Applis récentes"), async () => await DepShellAsync("input keyevent KEYCODE_APP_SWITCH")),
                DepButton(Loc.T("⚙️ Paramètres"), async () => await DepShellAsync("am start -a android.settings.SETTINGS")),
                DepButton(Loc.T("🔆 Luminosité maximale"), async () => { await DepShellAsync("settings put system screen_brightness_mode 0"); await DepShellAsync("settings put system screen_brightness 255"); }),
                DepButton(Loc.T("☀️ Écran toujours allumé en charge"), async () => await DepShellAsync("svc power stayon usb"))));

            DepContent.Children.Add(DepHeading(Loc.T("Récupérer les données avant réparation ou wipe")));
            DepContent.Children.Add(DepText(Loc.T("Les dossiers cochés sont copiés dans le dossier Captures, dans un sous-dossier au nom du terminal. Les données internes des applis ne sont pas accessibles sans root.")));
            var folders = new[] { ("/sdcard/DCIM", Loc.T("Photos et vidéos (DCIM)")), ("/sdcard/Pictures", Loc.T("Images")), ("/sdcard/Documents", Loc.T("Documents")),
                                  ("/sdcard/Download", Loc.T("Téléchargements")), ("/sdcard/Movies", Loc.T("Vidéos")), ("/sdcard/Music", Loc.T("Musique")) };
            var boxes = new List<(CheckBox Box, string Path)>();
            var wrap = new WrapPanel();
            foreach (var f in folders)
            {
                var cb = new CheckBox { Content = f.Item2, IsChecked = f.Item1 != "/sdcard/Music", Style = (Style)FindResource("ModernCheckBoxStyle"), Margin = new Thickness(0, 0, 18, 6), ToolTip = f.Item1 };
                boxes.Add((cb, f.Item1));
                wrap.Children.Add(cb);
            }
            DepContent.Children.Add(wrap);
            var other = new TextBox { Height = 30, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left, Width = 460, VerticalContentAlignment = VerticalAlignment.Center, Tag = Loc.T("Autre dossier du terminal (facultatif), ex. /sdcard/Android/media"), Style = (Style)FindResource("PlaceholderTextBoxStyle"), Margin = new Thickness(0, 2, 0, 6) };
            DepContent.Children.Add(other);
            var progress = DepText("", false);
            DepContent.Children.Add(DepButtons(
                DepButton(Loc.T("💾 Récupérer les données"), async () =>
                {
                    var paths = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Path).ToList();
                    if (other.Text.Trim().StartsWith("/")) paths.Add(other.Text.Trim().TrimEnd('/'));
                    await DepBackupAsync(paths, progress);
                }),
                DepButton(Loc.T("🐞 Rapport de bug Android (zip)"), async () => await DepBugReportAsync(progress), Loc.T("Rapport complet du système (journaux, état) : plusieurs minutes."))));
            DepContent.Children.Add(progress);
        }

        private string DepBackupFolder(string serial, string kind)
        {
            string root = MirroringService.GetCaptureFolderPath(AppDomain.CurrentDomain.BaseDirectory);
            string dir = Path.Combine(root, $"{kind}_{FileNameHelper.Sanitize(serial)}_{DateTime.Now:yyyyMMdd_HHmm}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private async Task DepBackupAsync(List<string> paths, TextBlock progress)
        {
            string serial = DepSerial;
            if (serial == null || paths.Count == 0 || _depBusy) return;
            string dest = DepBackupFolder(serial, "Sauvegarde");
            DepBusy(true);
            int ok = 0;
            var failed = new List<string>();
            try
            {
                foreach (var p in paths)
                {
                    progress.Text = Loc.F("Copie de {0}…", p);
                    var res = await _adbManager.ExecuteAsync($"-s {serial} pull {AdbManager.QuoteLocalArg(p)} {AdbManager.QuoteLocalArg(dest)}", TimeSpan.FromMinutes(30));
                    string outp = (res.StandardOutput ?? "") + (res.StandardError ?? "");
                    if (res.IsSuccess && !outp.Contains("does not exist")) ok++;
                    else failed.Add(p + (outp.Contains("does not exist") ? Loc.T(" (absent)") : ""));
                }
                progress.Text = Loc.F("✅ {0} dossier(s) copié(s) dans {1}", ok, dest) + (failed.Count > 0 ? "\n" + Loc.F("Non copiés : {0}", string.Join(", ", failed)) : "");
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💾 Données de {1} copiées dans {2}", DateTime.Now, serial, dest));
                try { Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true }); } catch { }
            }
            finally { DepBusy(false); }
        }

        private async Task DepBugReportAsync(TextBlock progress)
        {
            string serial = DepSerial;
            if (serial == null || _depBusy) return;
            string dest = DepBackupFolder(serial, "RapportBug");
            DepBusy(true);
            try
            {
                progress.Text = Loc.T("Rapport de bug en cours (plusieurs minutes)…");
                var res = await _adbManager.ExecuteAsync($"-s {serial} bugreport {AdbManager.QuoteLocalArg(dest)}", TimeSpan.FromMinutes(15));
                bool any = Directory.Exists(dest) && Directory.EnumerateFiles(dest).Any();
                progress.Text = any ? Loc.F("✅ Rapport de bug enregistré dans {0}", dest) : Loc.F("Rapport impossible : {0}", LastLines(res.StandardError + res.StandardOutput, 1));
                if (any) try { Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true }); } catch { }
            }
            finally { DepBusy(false); }
        }

        // ------------------------------------------------------------------ 4. Pas de débogage USB : clavier/souris par câble (OTG)

        private void RenderDepOtg()
        {
            DepContent.Children.Add(DepTitle(Loc.T("🚫 Pas de débogage USB")));
            DepContent.Children.Add(DepText(Loc.T("Sans débogage USB, ADB ne peut rien faire sur le terminal allumé. Deux solutions : l'activer (au besoin en prenant la main en clavier/souris par le câble), ou passer par la recovery (réinitialisation, mise à jour).")));

            DepContent.Children.Add(DepHeading(Loc.T("Prendre la main en clavier et souris par le câble")));
            DepContent.Children.Add(DepText(Loc.T("Le PC se fait passer pour un clavier et une souris branchés sur le terminal (mode OTG de scrcpy) : pas besoin du débogage USB. Il n'y a pas d'image sur le PC : regardez l'écran du terminal (s'il affiche encore quelque chose) et déplacez le pointeur avec la souris. Idéal quand le tactile est mort ou pour accepter « Autoriser le débogage USB »."), false));
            DepContent.Children.Add(DepSteps(new[]
            {
                Loc.T("Branchez le terminal en USB (un seul terminal à la fois, de préférence)."),
                Loc.T("Cliquez sur « Prendre la main » : le serveur ADB est arrêté le temps de la prise en main (il bloque le mode OTG sous Windows), puis relancé."),
                Loc.T("Une petite fenêtre s'ouvre : cliquez dedans, puis utilisez la souris et le clavier comme s'ils étaient branchés sur le terminal. Fermez-la pour terminer."),
            }));
            var status = DepText("", false);
            var choices = new WrapPanel();
            DepContent.Children.Add(DepButtons(DepButton(Loc.T("🖱️ Prendre la main (clavier et souris)"), async () => await DepStartOtgAsync(status, choices, null))));
            DepContent.Children.Add(status);
            DepContent.Children.Add(choices);
            DepContent.Children.Add(DepText(Loc.T("Selon le terminal et le pilote USB de Windows, le mode OTG peut être refusé : le message de scrcpy s'affiche alors dans la console.")));

            DepContent.Children.Add(DepHeading(Loc.T("Activer le débogage USB (toutes marques)")));
            DepContent.Children.Add(DepSteps(new[]
            {
                Loc.T("Paramètres > À propos du téléphone (ou du terminal)."),
                Loc.T("Touchez 7 fois « Numéro de build » : « Vous êtes développeur »."),
                Loc.T("Paramètres > Système > Options pour les développeurs > Débogage USB : activé."),
                Loc.T("Rebranchez et acceptez « Autoriser le débogage USB » (cochez « Toujours autoriser »)."),
            }));
            DepContent.Children.Add(DepText(Loc.T("Un MDM (SOTI, Intune, Workspace ONE…) peut interdire les options pour les développeurs : il faut alors l'autoriser depuis la console du MDM.")));
            DepContent.Children.Add(DepButtons(DepButton(Loc.T("♻️ Recovery et réinitialisation"), () => DepGo("recovery")), DepButton(Loc.T("⌨️ Combinaisons de touches"), () => DepGo("keys"))));
        }

        /// <summary>
        /// Lance scrcpy en mode OTG. serial : numéro USB du terminal ; vide = on le cherche (Windows, puis liste de scrcpy).
        /// Plusieurs appareils USB (webcam, clavier…) : scrcpy refuse de choisir, on garde le terminal ou on demande lequel.
        /// </summary>
        private async Task DepStartOtgAsync(TextBlock status, WrapPanel choices, string serial)
        {
            if (_depOtgProcess != null && !_depOtgProcess.HasExited) { status.Text = Loc.T("La prise en main est déjà ouverte."); return; }
            choices.Children.Clear();
            string scrcpy = MirroringService.ResolveScrcpyPath(AppDomain.CurrentDomain.BaseDirectory);
            if (scrcpy == null)
            {
                status.Text = Loc.T("scrcpy n'est pas installé.");
                _ = OfferToInstallToolAsync(ToolComponent.Scrcpy, "scrcpy sert à prendre la main sur le terminal en clavier et souris.");
                return;
            }
            if (string.IsNullOrEmpty(serial))
            {
                // Un seul terminal d'un fabricant connu vu par Windows : on le désigne d'emblée.
                try
                {
                    var usb = await Task.Run(() => UsbDeviceScanner.Scan());
                    var phones = usb.Where(d => d.Serial.Length > 0 && d.Brand.Length > 0).ToList();
                    if (phones.Count == 1) serial = phones[0].Serial;
                }
                catch { }
            }
            status.Text = Loc.T("Arrêt du serveur ADB, puis ouverture de la prise en main…");
            await _adbManager.ExecuteAsync("kill-server", TimeSpan.FromSeconds(15));
            try
            {
                string args = "--otg --window-title \"ADB Tools Pro - OTG\"";
                if (!string.IsNullOrEmpty(serial)) args += " -s \"" + serial.Replace("\"", "") + "\"";
                var psi = new ProcessStartInfo
                {
                    FileName = scrcpy,
                    WorkingDirectory = Path.GetDirectoryName(scrcpy),
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var log = new System.Text.StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
                bool hadSerial = !string.IsNullOrEmpty(serial);
                p.Exited += (s, e) => Dispatcher.BeginInvoke(new Action(async () =>
                {
                    string text; lock (log) text = log.ToString().Trim();
                    _depOtgProcess = null;
                    if (text.Length > 0) AppendConsole("\n[scrcpy --otg] " + text);
                    if (p.ExitCode != 0 && !hadSerial && ScrcpyOtg.NeedsSerial(text))
                    {
                        var all = ScrcpyOtg.ParseDevices(text);
                        var cand = ScrcpyOtg.Candidates(all);
                        if (cand.Count == 1)
                        {
                            await DepStartOtgAsync(status, choices, cand[0].Serial);
                            return;
                        }
                        status.Text = cand.Count > 1
                            ? Loc.T("Plusieurs appareils USB sont branchés : choisissez le terminal.")
                            : Loc.T("Plusieurs appareils USB sont branchés et aucun ne ressemble à un terminal : vérifiez que le câble transfère les données, puis choisissez le terminal dans la liste.");
                        foreach (var d in (cand.Count > 0 ? cand : all))
                        {
                            string label = (d.Brand.Length > 0 ? d.Brand + " · " : "") + (d.Label.Length > 0 ? d.Label + " · " : "") + d.Serial;
                            string sn = d.Serial;
                            choices.Children.Add(DepButton("🖱️ " + label, async () => await DepStartOtgAsync(status, choices, sn),
                                Loc.F("Identifiant USB {0}:{1}", d.Vid, d.Pid)));
                        }
                    }
                    else
                        status.Text = p.ExitCode != 0 ? Loc.F("La prise en main n'a pas pu démarrer : {0}", LastLines(text, 2)) : Loc.T("Prise en main terminée.");
                    await _adbManager.ExecuteAsync("start-server", TimeSpan.FromSeconds(30));
                }));
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                ChildProcessTracker.AddProcess(p);
                _depOtgProcess = p;
                status.Text = Loc.T("Prise en main ouverte : cliquez dans la petite fenêtre « ADB Tools Pro - OTG », puis utilisez la souris et le clavier. Fermez-la pour terminer.");
            }
            catch (Exception ex)
            {
                status.Text = Loc.F("La prise en main n'a pas pu démarrer : {0}", ex.Message);
                await _adbManager.ExecuteAsync("start-server", TimeSpan.FromSeconds(30));
            }
        }

        // ------------------------------------------------------------------ 5. Recovery, réinitialisation, fastboot

        private async Task RenderDepRecoveryAsync()
        {
            DepContent.Children.Add(DepTitle(Loc.T("♻️ Recovery, réinitialisation, fastboot")));
            DepContent.Children.Add(DepText(Loc.T("En recovery, ADB accepte un paquet (« Apply update from ADB ») même sans débogage USB : c'est ainsi qu'on réinitialise un terminal inutilisable avec le paquet du fabricant (ex. Factory Reset / Enterprise Reset chez Zebra), ou qu'on le met à jour.")));
            var wait = DepText(Loc.T("Recherche des terminaux…"), false);
            DepContent.Children.Add(wait);
            DepBusy(true);
            List<AndroidDevice> adb; List<string> fb;
            try
            {
                var all = await DepReadAllAsync();
                adb = all.Adb; fb = all.Fastboot;
            }
            finally { DepBusy(false); }
            if (_depView != "recovery") return;
            DepContent.Children.Remove(wait);

            foreach (var d in adb)
            {
                string s = d.Serial;
                switch (d.State)
                {
                    case "device":
                        DepContent.Children.Add(DepCard("ok", Loc.F("{0} : démarré normalement (débogage USB actif)", s), Loc.T("On peut le redémarrer directement en recovery ou en bootloader."),
                            DepButton(Loc.T("Redémarrer en recovery"), async () => { await _adbManager.ExecuteAsync($"-s {s} reboot recovery", TimeSpan.FromSeconds(30)); await Task.Delay(4000); RenderDepannage(); }),
                            DepButton(Loc.T("Redémarrer en bootloader (fastboot)"), async () => { await _adbManager.ExecuteAsync($"-s {s} reboot bootloader", TimeSpan.FromSeconds(30)); await Task.Delay(4000); RenderDepannage(); })));
                        break;
                    case "sideload":
                        DepContent.Children.Add(DepCard("info", Loc.F("{0} : prêt à recevoir un paquet (sideload)", s), Loc.T("Choisissez le paquet (zip) du fabricant : réinitialisation ou mise à jour."),
                            DepButton(Loc.T("📦 Envoyer un paquet…"), async () => await DepSideloadAsync(s))));
                        break;
                    case "recovery":
                    case "rescue":
                        DepContent.Children.Add(DepCard("info", Loc.F("{0} : en recovery", s), Loc.T("Sur le terminal, choisissez « Apply update from ADB » (Volume pour se déplacer, Marche pour valider), puis actualisez : il passera en « sideload »."),
                            DepButton(Loc.T("↻ Actualiser"), RenderDepannage),
                            DepButton(Loc.T("Redémarrer normalement"), async () => { await _adbManager.ExecuteAsync($"-s {s} reboot", TimeSpan.FromSeconds(30)); RenderDepannage(); })));
                        break;
                    default:
                        DepContent.Children.Add(DepAdbStateCard(s, d.State, s));
                        break;
                }
            }
            foreach (var s in fb)
            {
                var vars = OsUpdatePackage.ParseFastbootVars(await RunFastbootAsync($"-s {s} getvar all", 20));
                string info = string.Join("\n", RecoveryKeyGuide.FastbootSummary(vars).Select(x => Loc.T(x.Label) + " : " + Loc.T(x.Value)));
                string serial = s;
                DepContent.Children.Add(DepCard("info", Loc.F("{0} : en bootloader (fastboot)", s), info,
                    DepButton(Loc.T("Redémarrer normalement"), async () => { await RunFastbootAsync($"-s {serial} reboot", 30); RenderDepannage(); }),
                    DepButton(Loc.T("Redémarrer en recovery"), async () => { await RunFastbootAsync($"-s {serial} reboot recovery", 30); await Task.Delay(4000); RenderDepannage(); })));
            }
            if (adb.Count == 0 && fb.Count == 0)
                DepContent.Children.Add(DepCard("warn", Loc.T("Aucun terminal vu par ADB ni par fastboot"), Loc.T("Pour entrer en recovery sans débogage USB : combinaison de touches au démarrage (voir « Combinaisons de touches »), puis « Apply update from ADB ».")));

            DepContent.Children.Add(DepHeading(Loc.T("Réinitialiser un terminal sans débogage USB")));
            DepContent.Children.Add(DepSteps(new[]
            {
                Loc.T("Munissez-vous du paquet de réinitialisation fourni par le fabricant pour ce modèle (ex. Factory Reset ou Enterprise Reset chez Zebra)."),
                Loc.T("Entrez en recovery avec la combinaison de touches du modèle."),
                Loc.T("Choisissez « Apply update from ADB » (ou « Apply upgrade from ADB »), branchez le câble, actualisez cette page."),
                Loc.T("« Envoyer un paquet… » : le terminal applique le paquet puis revient au menu ; choisissez « Reboot system now »."),
            }));
            DepContent.Children.Add(DepText(Loc.T("Une réinitialisation d'usine efface tout le terminal. Pour une mise à jour d'Android sur plusieurs terminaux, utilisez la page « Mise à jour OS ».")));
            DepContent.Children.Add(DepButtons(DepButton(Loc.T("⌨️ Combinaisons de touches"), () => DepGo("keys")), DepButton(Loc.T("📦 Page Mise à jour OS"), () => NavOsUpdate_Click(null, null))));
        }

        private async Task DepSideloadAsync(string serial)
        {
            if (_depBusy) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Paquet Android (*.zip)|*.zip"), Title = Loc.T("Paquet à envoyer au terminal (réinitialisation, mise à jour)") };
            if (dlg.ShowDialog() != true) return;
            if (!await ShowCustomDialogAsync(Loc.T("📦 Envoyer le paquet"),
                    Loc.F("« {0} » va être envoyé à {1}. S'il s'agit d'une réinitialisation, toutes les données du terminal seront effacées. Continuer ?", Path.GetFileName(dlg.FileName), serial),
                    Loc.T("Envoyer"), Loc.T("Annuler"), true)) return;
            var status = DepText(Loc.T("Envoi du paquet…"), false);
            DepContent.Children.Insert(Math.Min(2, DepContent.Children.Count), status);
            DepBusy(true);
            try
            {
                var transfer = new AdbTransferProcess(OsUpdatePackage.SideloadProgress);
                var (ok, output) = await transfer.RunAsync(_adbExePath, $"-s {serial} sideload {AdbManager.QuoteLocalArg(dlg.FileName)}",
                    pct => Dispatcher.BeginInvoke(new Action(() => status.Text = Loc.F("Envoi du paquet… {0} %", pct))), CancellationToken.None);
                status.Text = ok ? Loc.T("✅ Paquet envoyé : suivez l'écran du terminal, puis « Reboot system now ».") : Loc.F("Envoi refusé ou interrompu : {0}", LastLines(output, 2));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Sideload {1} → {2} : {3}", DateTime.Now, Path.GetFileName(dlg.FileName), serial, status.Text));
            }
            finally { DepBusy(false); }
        }

        // ------------------------------------------------------------------ 6. Combinaisons de touches (toutes marques)

        private static string DepUserKeysPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, RecoveryKeyGuide.UserFileName);

        private async Task RenderDepKeysAsync()
        {
            DepContent.Children.Add(DepTitle(Loc.T("⌨️ Combinaisons de touches")));
            DepContent.Children.Add(DepText(Loc.T("Pour entrer en recovery ou en bootloader (fastboot) sans débogage USB. Tout est enregistré dans le programme : la page fonctionne sans connexion. Chaque procédure indique d'où elle vient, et vous pouvez ajouter les vôtres.")));
            var all = RecoveryKeyGuide.LoadUser(DepUserKeysPath).Concat(RecoveryKeyGuide.BuiltIn).ToList();

            // Marque et modèle : ceux du terminal branché si possible.
            string brand = "", model = "";
            try
            {
                var res = await _adbManager.ExecuteAsync("devices");
                var dev = (res.IsSuccess ? ParseAdbDevices(res.StandardOutput) : new List<AndroidDevice>()).FirstOrDefault(d => d.State == "device");
                if (dev != null)
                {
                    var p = await _adbManager.RunShellSectionsAsync(dev.Serial, TimeSpan.FromSeconds(8), "getprop ro.product.manufacturer", "getprop ro.product.model");
                    brand = RecoveryKeyGuide.NormalizeBrand(p[0]); model = p[1];
                }
            }
            catch { }
            if (_depView != "keys") return;

            var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
            var cmbBrand = new ComboBox { Width = 220, Height = 30, Margin = new Thickness(0, 0, 8, 6), ToolTip = Loc.T("Marque") };
            cmbBrand.Items.Add(new ComboBoxItem { Content = Loc.T("Toutes les marques"), Tag = "" });
            foreach (var b in RecoveryKeyGuide.Brands(all))
            {
                var it = new ComboBoxItem { Content = Loc.T(b), Tag = b };
                cmbBrand.Items.Add(it);
                if (brand.Length > 0 && (b.Equals(brand, StringComparison.OrdinalIgnoreCase) || b.StartsWith(brand + " ", StringComparison.OrdinalIgnoreCase))) cmbBrand.SelectedItem = it;
            }
            if (cmbBrand.SelectedItem == null) cmbBrand.SelectedIndex = 0;
            var txtModel = new TextBox { Width = 220, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Text = model, Tag = Loc.T("Modèle (ex. TC53, MC3300, CT45)"), Style = (Style)FindResource("PlaceholderTextBoxStyle"), Margin = new Thickness(0, 0, 8, 6) };
            row.Children.Add(cmbBrand); row.Children.Add(txtModel);
            row.Children.Add(DepButton(Loc.T("✏️ Ajouter mes combinaisons"), () =>
            {
                try
                {
                    if (!File.Exists(DepUserKeysPath)) File.WriteAllText(DepUserKeysPath, RecoveryKeyGuide.UserTemplate(), new System.Text.UTF8Encoding(false));
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + DepUserKeysPath + "\"") { UseShellExecute = true });
                }
                catch (Exception ex) { AppendConsole("\n" + ex.Message); }
            }, Loc.F("Fichier {0}, à côté du programme : jamais remplacé par les mises à jour. Rouvrez cette page après l'avoir enregistré.", RecoveryKeyGuide.UserFileName)));
            DepContent.Children.Add(row);
            if (brand.Length > 0) DepContent.Children.Add(DepText(Loc.F("Terminal branché : {0} {1}.", brand, model)));
            var count = DepText("");
            DepContent.Children.Add(count);

            var list = new StackPanel();
            DepContent.Children.Add(list);
            void Fill()
            {
                list.Children.Clear();
                string b = (cmbBrand.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                var found = RecoveryKeyGuide.Find(all, b, txtModel.Text);
                count.Text = txtModel.Text.Trim().Length > 0 && !found.Any(e => e.Models.Trim() != "*")
                    ? Loc.T("Pas de procédure enregistrée pour ce modèle précis : voici celles de la marque et les plus courantes.")
                    : Loc.F("{0} procédure(s).", found.Count);
                foreach (var e in found)
                {
                    string where = (Loc.T(e.Brand) + (e.Models.Trim() == "*" ? "" : " · " + e.Models.Replace("*", "…"))).Trim();
                    string mode = e.Mode == "fastboot" ? Loc.T("Bootloader (fastboot)") : Loc.T("Recovery");
                    var card = DepCard(e.Verified || e.Custom ? "ok" : "info", $"{mode} — {where}", "");
                    var p = (StackPanel)card.Child;
                    p.Children.Add(DepSteps(e.Steps.Select(Loc.T)));
                    p.Children.Add(DepText(
                        e.Custom ? Loc.T("Ajoutée par vous.") + (e.Source.Length > 0 ? " " + Loc.F("Origine : {0}.", e.Source) : "")
                        : e.Verified ? Loc.F("Documentation du fabricant : {0}.", Loc.T(e.Source))
                        : e.Source.Length > 0 ? Loc.F("Source tierce, à confirmer sur votre modèle : {0}.", Loc.T(e.Source))
                        : Loc.T("Procédure usuelle pour cette marque, non vérifiée modèle par modèle."), true, 11.5));
                    list.Children.Add(card);
                }
            }
            cmbBrand.SelectionChanged += (s, a) => Fill();
            txtModel.TextChanged += (s, a) => Fill();
            Fill();
        }
    }
}

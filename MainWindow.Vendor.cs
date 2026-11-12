using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ADBToolsNew
{
    /// <summary>
    /// Master : réglages constructeur. Un bloc par fichier constructeur (profil StageNow Zebra pour commencer), lu en
    /// étapes à cocher, modifiable dans un éditeur XML séparé, appliqué par l'appli d'aide (EMDK) seulement sur les
    /// terminaux de la marque. Les étapes sensibles (remise à zéro, débogage USB…) sont décochées au départ, demandent
    /// une confirmation et passent en dernier.
    /// </summary>
    public partial class MainWindow
    {
        private readonly Dictionary<MasterVendorBlock, MasterXmlEditor> _vendorEditors = new Dictionary<MasterVendorBlock, MasterXmlEditor>();
        private readonly Dictionary<string, MxProfile> _vendorParseCache = new Dictionary<string, MxProfile>();
        private bool _vendorSkipSensitive;

        private MxProfile VendorProfile(MasterVendorBlock b)
        {
            string xml = b.Xml ?? "";
            if (_vendorParseCache.TryGetValue(xml, out var p)) return p;
            if (_vendorParseCache.Count > 20) _vendorParseCache.Clear();
            p = StageNowReader.Read(xml);
            _vendorParseCache[xml] = p;
            return p;
        }

        /// <summary>Étapes cochées d'un Master (blocs inclus, XML valide).</summary>
        private int VendorCheckedCount() => _master == null ? 0 : _master.Vendor.Where(b => b.Include).Sum(b =>
        {
            var p = VendorProfile(b);
            return p.Ok ? p.Steps.Count(b.IsChecked) : 0;
        });

        private static string VendorFileName(string devicePath) => (devicePath ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";

        private bool VendorFileMissing(MasterVendorBlock b, string devicePath) =>
            string.IsNullOrEmpty(b.Files.FirstOrDefault(f => f.DevicePath == devicePath)?.Local);

        /// <summary>Fichiers lus par des étapes cochées et non joints, tous blocs confondus.</summary>
        private int VendorMissingFiles() => _master == null ? 0 : _master.Vendor.Where(b => b.Include).Sum(b =>
        {
            var p = VendorProfile(b);
            if (!p.Ok) return 0;
            return p.Steps.Where(b.IsChecked).SelectMany(s => s.DeviceFiles).Distinct().Count(path => VendorFileMissing(b, path));
        });

        private bool VendorHasInvalidXml() => _master != null && _master.Vendor.Any(b => b.Include && !VendorProfile(b).Ok);

        /// <summary>Liste des fichiers du bloc alignée sur le XML : chemins nouveaux ajoutés, chemins disparus et non joints retirés.</summary>
        private void SyncVendorFiles(MasterVendorBlock b)
        {
            var p = VendorProfile(b);
            if (!p.Ok) return;
            var paths = p.Steps.SelectMany(s => s.DeviceFiles).Distinct().ToList();
            b.Files.RemoveAll(f => !paths.Contains(f.DevicePath) && string.IsNullOrEmpty(f.Local));
            foreach (var path in paths)
                if (!b.Files.Any(f => f.DevicePath == path)) b.Files.Add(new MasterVendorFile { DevicePath = path });
        }

        private void CopyVendorFile(MasterVendorFile f, string source)
        {
            string rel = $"vendor/{Guid.NewGuid().ToString("N").Substring(0, 8)}/{Path.GetFileName(source)}";
            string dest = Path.Combine(_masterDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(source, dest, true);
            f.Local = rel;
        }

        // ------------------------------------------------------------------ Onglet « Constructeur »

        private void RenderMasterVendorView()
        {
            var m = _master;
            // Éditeurs restés ouverts sur un bloc qui n'est plus dans le Master (autre Master ouvert, bloc retiré) : fermés.
            foreach (var gone in _vendorEditors.Keys.Where(b => !m.Vendor.Contains(b)).ToList())
            {
                try { _vendorEditors[gone].Close(); } catch { }
                _vendorEditors.Remove(gone);
            }
            AddMasterSection(Loc.T("🏭 Réglages constructeur"),
                Loc.T("Fichiers propres à une marque, appliqués seulement sur les terminaux de cette marque (ailleurs le bloc est ignoré, et le bilan le dit). Zebra : profil StageNow exporté pour MDM (StageNow ▸ le profil ▸ Export to MDM ▸ XML), appliqué par la mini appli ADB Tools Helper via EMDK."),
                new List<MasterRow>(), async () => await ImportStageNowAsync(), Loc.T("＋ Importer un profil StageNow"));

            foreach (var b in m.Vendor.ToList())
            {
                var block = b;
                var p = VendorProfile(block);
                _masterViewHost.Children.Add(VendorBlockHeader(block, p));
                if (!p.Ok)
                {
                    var err = MasterMutedText(Loc.F("XML invalide (ligne {0}) : {1}. Corrigez-le avec « Voir le XML » : le bloc n'est pas appliqué tant qu'il est invalide.", p.ErrorLine, p.Error), 12);
                    err.SetResourceReference(TextBlock.ForegroundProperty, "BadFg");
                    _masterViewHost.Children.Add(err);
                    continue;
                }
                foreach (var theme in StageNowReader.ThemeOrder)
                {
                    var steps = p.Steps.Where(s => !s.Sensitive && s.Theme == theme).ToList();
                    if (steps.Count == 0) continue;
                    AddMasterSection(Loc.T(theme), "", steps.Select(s => VendorStepRow(block, s)).ToList(), null, null);
                }
                var sensitive = p.Steps.Where(s => s.Sensitive).ToList();
                if (sensitive.Count > 0)
                    AddMasterSection(Loc.T("⚠️ Étapes sensibles · confirmation demandée"),
                        Loc.T("Décochées au départ. Cochées, elles demandent une confirmation au moment d'appliquer et passent en dernier, après tout le reste du Master."),
                        sensitive.Select(s => VendorStepRow(block, s)).ToList(), null, null);
                if (block.Files.Count > 0)
                    AddMasterSection(Loc.T("📎 Fichiers lus par le profil"),
                        Loc.T("Le profil lit ces fichiers sur le terminal : joignez-les, ADB Tools les y dépose avant d'appliquer (inutile s'ils y sont déjà)."),
                        block.Files.Select(f => new MasterRow
                        {
                            Label = VendorFileName(f.DevicePath),
                            Sub = "→ " + f.DevicePath + (string.IsNullOrEmpty(f.Local) ? "" : " · " + Loc.T("joint")),
                            Locked = true,
                            Get = () => !string.IsNullOrEmpty(f.Local), Set = v => { },
                            Warn = () => string.IsNullOrEmpty(f.Local) ? Loc.T("à joindre") : "",
                            Edit = () => { AttachVendorFile(block, f); return Task.CompletedTask; },
                            Remove = string.IsNullOrEmpty(f.Local) ? (Action)null : () => { f.Local = ""; RenderMasterView(); },
                        }).ToList(), null, null);
            }
        }

        private MasterRow VendorStepRow(MasterVendorBlock block, MxStep s) => new MasterRow
        {
            Label = s.Label,
            Sub = s.Type + " · " + Loc.F("ligne {0}", s.Line) + (s.Recognized ? "" : " · " + Loc.T("non reconnue : appliquée telle quelle")),
            Get = () => block.IsChecked(s),
            Set = v => { block.SetChecked(s, v); RefreshMasterPending(); UpdateMasterApplyButton(); },
            Warn = () =>
            {
                if (s.Sensitive) return s.SensitiveReason;
                var missing = s.DeviceFiles.Where(path => VendorFileMissing(block, path)).Select(VendorFileName).ToList();
                if (missing.Count > 0) return Loc.F("fichier à joindre : {0}", string.Join(", ", missing));
                return s.Warning;
            },
            Edit = () => { OpenVendorEditor(block, s.Line); return Task.CompletedTask; },
        };

        /// <summary>En-tête d'un bloc : marque, nom, résumé, « Voir le XML », retrait.</summary>
        private FrameworkElement VendorBlockHeader(MasterVendorBlock block, MxProfile p)
        {
            var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "Surface2");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var include = new CheckBox { IsChecked = block.Include, Style = (Style)FindResource("ModernCheckBoxStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = Loc.T("Appliquer ce bloc") };
            include.Checked += (s, a) => { block.Include = true; RefreshMasterPending(); UpdateMasterApplyButton(); };
            include.Unchecked += (s, a) => { block.Include = false; RefreshMasterPending(); UpdateMasterApplyButton(); };
            g.Children.Add(include);

            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "BrandSoft");
            var badgeText = new TextBlock { Text = block.Brand.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "BrandText");
            badge.Child = badgeText;
            Grid.SetColumn(badge, 1); g.Children.Add(badge);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = Loc.F("Profil StageNow « {0} »", block.Name), FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            names.Children.Add(title);
            var parts = new List<string> { Loc.F("importé le {0:dd/MM/yyyy}", block.ImportedAt) };
            if (p.Ok)
            {
                if (p.MinVersion.Length > 0) parts.Add(Loc.F("MX {0} minimum", p.MinVersion));
                parts.Add(Loc.F("{0} étape(s), {1} cochée(s)", p.Steps.Count, p.Steps.Count(block.IsChecked)));
            }
            if (block.Xml != block.OriginalXml) parts.Add(Loc.T("XML modifié"));
            names.Children.Add(MasterSub(string.Join(" · ", parts)));
            Grid.SetColumn(names, 2); g.Children.Add(names);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var xml = new Button { Content = Loc.T("Voir le XML"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 28, Padding = new Thickness(10, 0, 10, 0) };
            xml.Click += (s, a) => OpenVendorEditor(block, 0);
            buttons.Children.Add(xml);
            var rm = new Button { Content = "✕", ToolTip = Loc.T("Retirer du Master"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 28, Width = 30, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = !_masterBusy };
            rm.Click += async (s, a) =>
            {
                if (!await ShowCustomDialogAsync(Loc.T("Retirer le bloc"), Loc.F("Retirer le profil « {0} » de ce Master ?", block.Name), Loc.T("Retirer"), Loc.T("Annuler"), true)) return;
                if (_vendorEditors.TryGetValue(block, out var ed)) { try { ed.Close(); } catch { } _vendorEditors.Remove(block); }
                _master.Vendor.Remove(block);
                RerenderMaster();
            };
            buttons.Children.Add(rm);
            Grid.SetColumn(buttons, 3); g.Children.Add(buttons);
            card.Child = g;
            return card;
        }

        // ------------------------------------------------------------------ Import, fichiers, éditeur

        private async Task ImportStageNowAsync()
        {
            if (_master == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Profil StageNow exporté (*.xml)|*.xml|Tous les fichiers (*.*)|*.*"), Title = Loc.T("Importer un profil StageNow") };
            if (dlg.ShowDialog() != true) return;
            string xml;
            try { xml = File.ReadAllText(dlg.FileName); }
            catch (Exception ex) { await ShowCustomDialogAsync(Loc.T("Profil StageNow"), ex.Message, "OK", null, false); return; }
            var p = StageNowReader.Read(xml);
            if (!p.Ok)
            {
                await ShowCustomDialogAsync(Loc.T("Profil StageNow"), Loc.F("Ce fichier ne peut pas être lu (ligne {0}) : {1}", p.ErrorLine, p.Error), "OK", null, false);
                return;
            }
            SyncMasterFromUi();
            var b = new MasterVendorBlock
            {
                Brand = VendorModules.Zebra,
                Kind = "stagenow",
                Name = p.Name.Length > 0 ? p.Name : Path.GetFileNameWithoutExtension(dlg.FileName),
                SourceFile = Path.GetFileName(dlg.FileName),
                ImportedAt = DateTime.Now,
                Xml = xml,
                OriginalXml = xml,
            };
            SyncVendorFiles(b);
            // Fichiers posés à côté du XML (même nom que dans le profil) : joints d'office.
            string dir = Path.GetDirectoryName(dlg.FileName);
            int auto = 0;
            foreach (var f in b.Files)
            {
                string candidate = Path.Combine(dir, VendorFileName(f.DevicePath));
                if (VendorFileName(f.DevicePath).Length > 0 && File.Exists(candidate)) { try { CopyVendorFile(f, candidate); auto++; } catch { } }
            }
            _master.Vendor.Add(b);
            _masterView = "vendor";
            RenderMasterContent();

            var lines = new List<string>();
            foreach (var g in p.Steps.Where(s => !s.Sensitive).GroupBy(s => s.Theme).OrderBy(g => Array.IndexOf(StageNowReader.ThemeOrder, g.Key)))
                lines.Add(Loc.F("• {0} : {1} étape(s)", Loc.T(g.Key), g.Count()));
            int sens = p.Steps.Count(s => s.Sensitive), unknown = p.Steps.Count(s => !s.Recognized);
            if (sens > 0) lines.Add(Loc.F("• Étapes sensibles : {0}, décochées (confirmation demandée si vous les cochez)", sens));
            if (unknown > 0) lines.Add(Loc.F("• Non reconnues : {0} (gardées telles quelles, appliquées sans aperçu)", unknown));
            if (b.Files.Count > 0)
                lines.Add(Loc.F("• Fichiers lus par le profil : {0}, dont {1} joint(s) automatiquement (trouvés à côté du XML)", b.Files.Count, auto));
            await ShowCustomDialogAsync(Loc.T("🏭 Profil StageNow importé"),
                Loc.F("« {0} » : {1} étape(s) lue(s){2}.\n\n{3}", b.Name, p.Steps.Count, p.MinVersion.Length > 0 ? Loc.F(", faite(s) pour MX {0}", p.MinVersion) : "", string.Join("\n", lines))
                + (b.Files.Count > auto ? "\n\n" + Loc.T("Joignez les fichiers manquants dans « Fichiers lus par le profil » (✏️).") : ""),
                "OK", null, false);
        }

        private void AttachVendorFile(MasterVendorBlock b, MasterVendorFile f)
        {
            string name = VendorFileName(f.DevicePath);
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.F("Fichier à déposer en {0}", f.DevicePath), FileName = name };
            if (dlg.ShowDialog() != true) return;
            try { CopyVendorFile(f, dlg.FileName); }
            catch (Exception ex) { TxtMasterSummary.Text = ex.Message; return; }
            RenderMasterView();
        }

        /// <summary>Ouvre (ou ramène) l'éditeur du XML d'un bloc, à une ligne donnée.</summary>
        private void OpenVendorEditor(MasterVendorBlock block, int line)
        {
            if (!_vendorEditors.TryGetValue(block, out var ed))
            {
                ed = new MasterXmlEditor(block) { Owner = this };
                ed.Changed += () => Dispatcher.BeginInvoke(new Action(() => OnVendorXmlChanged(block)));
                ed.Closed += (s, a) => _vendorEditors.Remove(block);
                _vendorEditors[block] = ed;
                ed.Show();
            }
            else
            {
                if (ed.WindowState == WindowState.Minimized) ed.WindowState = WindowState.Normal;
                ed.Activate();
            }
            if (line > 0) ed.GoToLine(line);
        }

        /// <summary>XML modifié (et valide) dans l'éditeur : fichiers et affichage du Master mis à jour, sans perdre la position.</summary>
        private void OnVendorXmlChanged(MasterVendorBlock block)
        {
            if (_master == null || !_master.Vendor.Contains(block)) return;
            SyncVendorFiles(block);
            if (_masterView == "vendor")
            {
                double offset = MasterScroll.VerticalOffset;
                RenderMasterView();
                Dispatcher.BeginInvoke(new Action(() => MasterScroll.ScrollToVerticalOffset(offset)), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else RefreshMasterPending();
            UpdateMasterApplyButton();
        }

        // ------------------------------------------------------------------ Application

        /// <summary>Étapes sensibles cochées (blocs inclus) : confirmation tapée, sinon le Master part sans elles.</summary>
        private async Task<bool> ConfirmVendorSensitiveAsync()
        {
            _vendorSkipSensitive = false;
            var list = _master.Vendor.Where(b => b.Include).SelectMany(b =>
            {
                var p = VendorProfile(b);
                return p.Ok ? p.Steps.Where(s => s.Sensitive && b.IsChecked(s)) : Enumerable.Empty<MxStep>();
            }).ToList();
            if (list.Count == 0) return true;
            string detail = string.Join("\n", list.Select(s => "• " + s.Label + " : " + s.SensitiveReason));
            bool typed = await ShowCustomDialogAsync(Loc.F("⚠️ {0} étape(s) sensible(s) cochée(s)", list.Count),
                Loc.F("Elles seront appliquées sur les terminaux de la marque, en dernier, après tout le reste du Master :\n\n{0}\n\nPour confirmer, tapez APPLIQUER.", detail),
                Loc.T("Tout appliquer"), Loc.T("Annuler"), true, true, "");
            if (typed && string.Equals(TxtDialogInput.Text.Trim(), "APPLIQUER", StringComparison.OrdinalIgnoreCase)) return true;
            if (!await ShowCustomDialogAsync(Loc.T("Étapes sensibles non confirmées"), Loc.T("Appliquer le Master sans les étapes sensibles ?"), Loc.T("Appliquer sans ces étapes"), Loc.T("Annuler"), false))
                return false;
            _vendorSkipSensitive = true;
            return true;
        }

        /// <summary>Il y a des étapes constructeur à appliquer sur ce terminal (marque concernée) : l'appli d'aide est préparée tôt.</summary>
        private bool VendorAppliesTo(MasterTarget t, MasterProfile m) =>
            m.Vendor.Any(b => b.Include && b.Brand == t.Brand && VendorProfile(b) is MxProfile p && p.Ok && p.Steps.Any(b.IsChecked));

        /// <summary>Applique les étapes cochées des blocs (ordinaires, ou sensibles en dernier), une par une, avec la réponse du terminal.</summary>
        private async Task ApplyVendorAsync(MasterTarget t, MasterProfile m, bool sensitive)
        {
            foreach (var b in m.Vendor.Where(x => x.Include))
            {
                string cat = Loc.F("Constructeur · {0}", b.Name);
                var p = VendorProfile(b);
                if (!p.Ok)
                {
                    if (!sensitive) MasterAdd(t, cat, Loc.T("Profil"), "ko", Loc.F("XML invalide (ligne {0}) : {1}", p.ErrorLine, p.Error));
                    continue;
                }
                var steps = p.Steps.Where(s => s.Sensitive == sensitive && b.IsChecked(s)).ToList();
                if (steps.Count == 0) continue;
                if (t.Brand != b.Brand)
                {
                    MasterAdd(t, cat, Loc.F("{0} étape(s)", steps.Count), "skip", Loc.F("réglages {0} ignorés sur ce terminal ({1})", b.Brand, t.Manufacturer.Length > 0 ? t.Manufacturer : Loc.T("autre marque")));
                    continue;
                }
                if (sensitive && _vendorSkipSensitive)
                {
                    foreach (var s in steps) MasterAdd(t, cat, s.Label, "skip", Loc.T("étape sensible non confirmée"));
                    continue;
                }
                if (!sensitive)
                {
                    foreach (var path in steps.SelectMany(s => s.DeviceFiles).Distinct())
                    {
                        var f = b.Files.FirstOrDefault(x => x.DevicePath == path);
                        if (string.IsNullOrEmpty(f?.Local)) continue;
                        string local = Path.Combine(_masterDir, f.Local.Replace('/', Path.DirectorySeparatorChar));
                        string dir = path.Substring(0, Math.Max(1, path.LastIndexOf('/')));
                        MasterStatus(t, Loc.F("Fichier : {0}…", VendorFileName(path)));
                        await _adbManager.ExecuteAsync($"-s {t.Serial} shell mkdir -p {MasterProfile.ShellQuote(dir)}", TimeSpan.FromSeconds(15));
                        var res = await _adbManager.ExecuteAsync($"-s {t.Serial} push {AdbManager.QuoteLocalArg(local)} {AdbManager.QuoteLocalArg(path)}", TimeSpan.FromMinutes(10));
                        MasterAdd(t, cat, Loc.F("Fichier {0} → {1}", VendorFileName(path), path), res.IsSuccess ? "ok" : "ko", res.IsSuccess ? "" : LastLines(res.StandardError + res.StandardOutput, 1));
                    }
                }
                if (!await EnsureMasterHelperAsync(t, MasterHelperApp.MxMinVersion))
                {
                    foreach (var s in steps) MasterAdd(t, cat, s.Label, "ko", Loc.T("mini appli ADB Tools Helper non installée"));
                    continue;
                }
                bool noEmdk = false;
                foreach (var s in steps)
                {
                    if (noEmdk) { MasterAdd(t, cat, s.Label, "skip", Loc.T("EMDK absent")); continue; }
                    MasterStatus(t, Loc.F("Constructeur : {0}…", s.Label));
                    var output = await RunMasterScriptAsync(t.Serial, MasterHelperApp.ApplyMxCommand(StageNowReader.StepPayload(s)) + " 2>&1\n");
                    string raw = string.Join("\n", output);
                    var br = MasterProfile.ParseBroadcastResult(raw);
                    var (status, response) = StageNowReader.ParseHelperData(br.Data);
                    if (br.Code == 1) { MasterAdd(t, cat, s.Label, "ok"); continue; }
                    if (status == "NO_EMDK")
                    {
                        noEmdk = true;
                        MasterAdd(t, cat, s.Label, "ko", Loc.T("EMDK absent sur ce terminal : les réglages Zebra ne peuvent pas passer par la mini appli"));
                        continue;
                    }
                    string why = br.Code == 0
                        ? Loc.T("pas de réponse de la mini appli") + (sensitive ? " " + Loc.T("(normal si l'étape a coupé le débogage USB ou redémarré le terminal)") : "") + (raw.Length > 0 ? " — " + LastLines(raw, 1) : "")
                        : StageNowReader.ErrorSummary(response) is string sum && sum.Length > 0 ? sum : status;
                    MasterAdd(t, cat, s.Label, "ko", why);
                    if (br.Code != 1) AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🏭 {t.Serial} · {s.Label} : {status} {response}");
                }
            }
        }
    }
}

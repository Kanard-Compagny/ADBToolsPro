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
            string key = (b.Kind ?? "") + "\u0001" + xml;
            if (_vendorParseCache.TryGetValue(key, out var p)) return p;
            if (_vendorParseCache.Count > 20) _vendorParseCache.Clear();
            p = VendorKinds.Read(b.Kind, xml, true); // un profil créé vide reste valide
            _vendorParseCache[key] = p;
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
            !(b.Files.FirstOrDefault(f => f.DevicePath == devicePath)?.Provided ?? false);

        private MasterVendorFile VendorFileFor(MasterVendorBlock b, string devicePath) => b.Files.FirstOrDefault(f => f.DevicePath == devicePath);

        /// <summary>Fichiers que le profil fait télécharger depuis un lien, ni fournis ni laissés au terminal en connaissance de cause.</summary>
        private int VendorLinkFiles() => _master == null ? 0 : _master.Vendor.Where(b => b.Include).Sum(b =>
        {
            var p = VendorProfile(b);
            if (!p.Ok) return 0;
            return p.Steps.Where(s => s.DownloadTarget.Length > 0 && b.IsChecked(s)).Select(s => VendorFileFor(b, s.DownloadTarget))
                .Count(f => f != null && !f.Provided && !f.KeepDownload);
        });

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
            var reads = p.Steps.SelectMany(s => s.DeviceFiles).Distinct().ToList();
            var downloads = p.Steps.Where(s => s.DownloadTarget.Length > 0).Select(s => s.DownloadTarget).Distinct().ToList();
            var paths = reads.Concat(downloads).Distinct().ToList();
            b.Files.RemoveAll(f => !paths.Contains(f.DevicePath) && !f.Provided);
            foreach (var path in paths)
            {
                var f = VendorFileFor(b, path);
                if (f == null) b.Files.Add(f = new MasterVendorFile { DevicePath = path });
                f.Download = downloads.Contains(path);
            }
        }

        private void CopyVendorFile(MasterVendorFile f, string source)
        {
            string rel = $"vendor/{Guid.NewGuid().ToString("N").Substring(0, 8)}/{Path.GetFileName(source)}";
            string dest = Path.Combine(_masterDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.Copy(source, dest, true);
            f.Local = rel;
            f.AppPackage = "";
            f.KeepDownload = false;
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
                Loc.T("Fichiers propres à une marque, appliqués seulement sur les terminaux de cette marque (ailleurs le bloc est ignoré, et le bilan le dit). Zebra : profil StageNow exporté pour MDM (StageNow ▸ le profil ▸ Export to MDM ▸ XML), appliqué par la mini appli ADB Tools Helper via EMDK. Datalogic : profil Scan2Deploy (.json), appliqué par l'agent Scan2Deploy du terminal. Honeywell : DeviceConfig.xml (EZConfig), déposé dans honeywell/persist et appliqué au redémarrage."),
                new List<MasterRow>(), async () => await ImportStageNowAsync(), Loc.T("＋ Importer un profil constructeur"),
                async () => await CreateEmptyVendorBlockAsync(), Loc.T("＋ Créer un profil Zebra vide"));

            foreach (var b in m.Vendor.ToList())
            {
                var block = b;
                var p = VendorProfile(block);
                _masterViewHost.Children.Add(VendorBlockHeader(block, p));
                if (!p.Ok)
                {
                    var err = MasterMutedText(VendorKinds.IsJson(block.Kind)
                        ? Loc.F("JSON invalide (ligne {0}) : {1}. Corrigez-le avec « Voir le JSON » : le bloc n'est pas appliqué tant qu'il est invalide.", p.ErrorLine, p.Error)
                        : Loc.F("XML invalide (ligne {0}) : {1}. Corrigez-le avec « Voir le XML » : le bloc n'est pas appliqué tant qu'il est invalide.", p.ErrorLine, p.Error), 12);
                    err.SetResourceReference(TextBlock.ForegroundProperty, "BadFg");
                    _masterViewHost.Children.Add(err);
                    continue;
                }
                if (p.Steps.Count == 0 && block.Kind == VendorKinds.StageNow)
                    _masterViewHost.Children.Add(MasterMutedText(Loc.T("Profil vide : « Ajouter une étape » ouvre le catalogue des modèles dans l'éditeur XML."), 12));
                if (block.Kind == VendorKinds.Scan2Deploy)
                    _masterViewHost.Children.Add(MasterMutedText(Loc.T("Appliqué en une fois par l'agent Scan2Deploy du terminal (Datalogic, Android 9 et plus), avec les seules étapes cochées. Le bilan reprend le compte rendu de l'agent."), 12));
                if (block.Kind == VendorKinds.DeviceConfig)
                    _masterViewHost.Children.Add(MasterMutedText(Loc.T("Déposé dans honeywell/persist avec les seules étapes cochées (le mode Provisioning doit être activé sur le terminal), puis appliqué par Honeywell au redémarrage : le terminal redémarre à la fin du Master."), 12));
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
                    AddMasterSection(Loc.T("📎 Fichiers du profil"),
                        Loc.T("Fichiers que le profil lit ou fait télécharger sur le terminal. Fournissez-les (✏️ : fichier du PC ou apk d'une appli de l'onglet Applications) : ADB Tools les dépose au bon endroit avant d'appliquer, et l'étape de téléchargement correspondante est alors sautée. Le reste du profil s'en sert comme prévu."),
                        block.Files.Select(f => new MasterRow
                        {
                            Label = VendorFileName(f.DevicePath),
                            Sub = "→ " + f.DevicePath + " · " + VendorFileSource(f),
                            Locked = true,
                            Get = () => f.Provided, Set = v => { },
                            Warn = () => f.Provided ? "" : f.Download
                                ? (f.KeepDownload ? "" : Loc.T("lien : le terminal doit joindre le serveur, ou fournissez le fichier"))
                                : Loc.T("à joindre"),
                            Edit = () => { ShowVendorFileMenu(block, f); return Task.CompletedTask; },
                            Remove = f.Provided ? () => { f.Local = ""; f.AppPackage = ""; RenderMasterView(); } : (Action)null,
                        }).ToList(), null, null);
            }
        }

        private MasterRow VendorStepRow(MasterVendorBlock block, MxStep s) => new MasterRow
        {
            Label = s.Label,
            Sub = s.Type + " · " + Loc.F("ligne {0}", s.Line) + (s.Recognized ? "" : " · " + Loc.T("non reconnue : appliquée telle quelle"))
                + (s.DownloadTarget.Length > 0 && (VendorFileFor(block, s.DownloadTarget)?.Provided ?? false) ? " · " + Loc.T("remplacée par le fichier fourni (sautée)") : ""),
            Get = () => block.IsChecked(s),
            Set = v => { block.SetChecked(s, v); RefreshMasterPending(); UpdateMasterApplyButton(); },
            Warn = () =>
            {
                if (s.Sensitive) return s.SensitiveReason;
                if (s.DownloadTarget.Length > 0)
                {
                    var f = VendorFileFor(block, s.DownloadTarget);
                    if (f != null && !f.Provided && !f.KeepDownload)
                        return s.DownloadEncrypted ? Loc.T("téléchargé depuis un lien chiffré : voir 📎 Fichiers du profil") : Loc.T("téléchargé depuis un lien : voir 📎 Fichiers du profil");
                }
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
            bool created = string.IsNullOrEmpty(block.SourceFile);
            string titleText = block.Kind == VendorKinds.Scan2Deploy ? Loc.F("Profil Scan2Deploy « {0} »", block.Name)
                : block.Kind == VendorKinds.DeviceConfig ? Loc.F("DeviceConfig Honeywell « {0} »", block.Name)
                : created ? Loc.F("Profil Zebra « {0} »", block.Name) : Loc.F("Profil StageNow « {0} »", block.Name);
            var title = new TextBlock { Text = titleText, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            names.Children.Add(title);
            var parts = new List<string> { created ? Loc.F("créé le {0:dd/MM/yyyy}", block.ImportedAt) : Loc.F("importé le {0:dd/MM/yyyy}", block.ImportedAt) };
            if (p.Ok)
            {
                if (p.MinVersion.Length > 0) parts.Add(Loc.F("MX {0} minimum", p.MinVersion));
                parts.Add(Loc.F("{0} étape(s), {1} cochée(s)", p.Steps.Count, p.Steps.Count(block.IsChecked)));
            }
            if (block.Xml != block.OriginalXml) parts.Add(VendorKinds.IsJson(block.Kind) ? Loc.T("JSON modifié") : Loc.T("XML modifié"));
            names.Children.Add(MasterSub(string.Join(" · ", parts)));
            Grid.SetColumn(names, 2); g.Children.Add(names);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (block.Kind == VendorKinds.StageNow)
            {
                var addStep = new Button { Content = Loc.T("Ajouter une étape"), ToolTip = Loc.T("Catalogue de modèles d'étapes MX, dans l'éditeur XML"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0) };
                addStep.SetResourceReference(Control.BorderBrushProperty, "Accent");
                addStep.Click += (s, a) => OpenVendorEditor(block, 0, true);
                buttons.Children.Add(addStep);
            }
            var xml = new Button { Content = VendorKinds.IsJson(block.Kind) ? Loc.T("Voir le JSON") : Loc.T("Voir le XML"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 28, Padding = new Thickness(10, 0, 10, 0) };
            xml.Click += (s, a) => OpenVendorEditor(block, 0);
            buttons.Children.Add(xml);
            // Fusion : profils StageNow seulement (les autres formats s'appliquent d'un bloc).
            var others = block.Kind != VendorKinds.StageNow ? new List<MasterVendorBlock>() : _master.Vendor.Where(x => x != block && x.Brand == block.Brand && x.Kind == block.Kind).ToList();
            if (others.Count > 0 && p.Ok)
            {
                var mergeBtn = new Button { Content = Loc.T("Fusionner dans…"), ToolTip = Loc.T("Réunir ce profil avec un autre profil de la même marque (un seul XML)"), Style = (Style)FindResource("OutlineButtonStyle"), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = !_masterBusy };
                mergeBtn.Click += (s, a) =>
                {
                    var menu = new ContextMenu { PlacementTarget = mergeBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                    foreach (var o in others)
                    {
                        var other = o;
                        var item = new MenuItem { Header = Loc.F("Profil « {0} »", other.Name), IsEnabled = VendorProfile(other).Ok };
                        item.Click += async (s2, a2) => await MergeVendorBlocksAsync(block, other);
                        menu.Items.Add(item);
                    }
                    menu.IsOpen = true;
                };
                buttons.Children.Add(mergeBtn);
            }
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
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Loc.T("Profils constructeur (StageNow .xml, Scan2Deploy .json, DeviceConfig.xml)|*.xml;*.json|Tous les fichiers (*.*)|*.*"),
                Title = Loc.T("Importer un ou plusieurs profils constructeur"),
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;
            var loaded = new List<(string Path, string Xml, MxProfile P)>();
            var others = new List<(string Path, string Text, string Kind, MxProfile P)>();
            var bad = new List<string>();
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    string xml = File.ReadAllText(file);
                    string kind = VendorKinds.Detect(xml);
                    var p = VendorKinds.Read(kind, xml);
                    if (!p.Ok) bad.Add(Loc.F("• {0} : ligne {1} : {2}", Path.GetFileName(file), p.ErrorLine, p.Error));
                    else if (kind == VendorKinds.StageNow) loaded.Add((file, xml, p));
                    else others.Add((file, xml, kind, p));
                }
                catch (Exception ex) { bad.Add("• " + Path.GetFileName(file) + " : " + ex.Message); }
            }
            if (loaded.Count == 0 && others.Count == 0)
            {
                await ShowCustomDialogAsync(Loc.T("Profil constructeur"), Loc.T("Aucun fichier n'a pu être lu :") + "\n\n" + string.Join("\n", bad), "OK", null, false);
                return;
            }
            SyncMasterFromUi();

            // Scan2Deploy (Datalogic) et DeviceConfig (Honeywell) : un bloc par fichier, sans fusion.
            var touched = new List<MasterVendorBlock>();
            foreach (var item in others)
            {
                var b = new MasterVendorBlock
                {
                    Brand = VendorKinds.BrandOf(item.Kind),
                    Kind = item.Kind,
                    Name = item.P.Name.Length > 0 ? item.P.Name : Path.GetFileNameWithoutExtension(item.Path),
                    SourceFile = Path.GetFileName(item.Path),
                    ImportedAt = DateTime.Now,
                    Xml = item.Text,
                    OriginalXml = item.Text,
                };
                _master.Vendor.Add(b);
                touched.Add(b);
            }
            if (loaded.Count == 0)
            {
                _masterView = "vendor";
                RenderMasterContent();
                var t0 = touched.Select(VendorImportSummary).ToList();
                if (bad.Count > 0) t0.Add(Loc.T("Non lus :") + "\n" + string.Join("\n", bad));
                await ShowCustomDialogAsync(Loc.T("🏭 Profil constructeur importé"), string.Join("\n\n", t0), "OK", null, false);
                return;
            }

            // Fusion : dans le profil Zebra déjà présent, ou entre les fichiers choisis ensemble.
            var existing = _master.Vendor.LastOrDefault(b => b.Brand == VendorModules.Zebra && b.Kind == VendorKinds.StageNow && VendorProfile(b).Ok && !touched.Contains(b));
            string names = string.Join(", ", loaded.Select(x => "« " + Path.GetFileNameWithoutExtension(x.Path) + " »"));
            bool merge = false;
            if (existing != null)
                merge = await ShowCustomDialogAsync(Loc.T("Fusionner les profils ?"),
                    Loc.F("Ce Master contient déjà le profil « {0} ».\n\nFusionner {1} avec lui (un seul XML, une seule liste d'étapes), ou l'ajouter à part ?", existing.Name, names),
                    Loc.T("Fusionner"), Loc.T("Garder à part"), false);
            else if (loaded.Count > 1)
                merge = await ShowCustomDialogAsync(Loc.T("Fusionner les profils ?"),
                    Loc.F("{0} profils choisis : {1}.\n\nLes fusionner en un seul (un seul XML, une seule liste d'étapes), ou les garder séparés ?", loaded.Count, names),
                    Loc.T("Fusionner en un seul"), Loc.T("Garder séparés"), false);

            MasterVendorBlock target = merge ? existing : null;
            var mergeNotes = new List<string>();
            int auto = 0;
            foreach (var item in loaded)
            {
                string name = item.P.Name.Length > 0 ? item.P.Name : Path.GetFileNameWithoutExtension(item.Path);
                if (target != null)
                {
                    var r = StageNowReader.Merge(target.Xml, item.Xml);
                    target.Xml = r.Xml;
                    try { target.OriginalXml = StageNowReader.Merge(target.OriginalXml, item.Xml).Xml; } catch { target.OriginalXml = r.Xml; }
                    target.Name += " + " + name;
                    target.SourceFile += ", " + Path.GetFileName(item.Path);
                    mergeNotes.Add(Loc.F("• « {0} » : {1} étape(s) ajoutée(s), {2} déjà présente(s)", name, r.Added, r.Duplicates));
                    if (r.SameTypes.Count > 0)
                        mergeNotes.Add(Loc.F("  ⚠️ Réglés des deux côtés : {0}. MX applique dans l'ordre : la dernière valeur l'emporte (à vérifier dans le XML).", string.Join(", ", r.SameTypes)));
                }
                else
                {
                    var b = new MasterVendorBlock
                    {
                        Brand = VendorModules.Zebra,
                        Kind = "stagenow",
                        Name = name,
                        SourceFile = Path.GetFileName(item.Path),
                        ImportedAt = DateTime.Now,
                        Xml = item.Xml,
                        OriginalXml = item.Xml,
                    };
                    _master.Vendor.Add(b);
                    // Fusion entre fichiers choisis ensemble : le premier devient la base, les suivants s'y ajoutent.
                    if (merge) target = b;
                }
                var dest = target ?? _master.Vendor.Last();
                if (!touched.Contains(dest)) touched.Add(dest);
                SyncVendorFiles(dest);
                // Fichiers posés à côté du XML (même nom que dans le profil) : fournis d'office.
                string dir = Path.GetDirectoryName(item.Path);
                foreach (var f in dest.Files.Where(f => !f.Provided))
                {
                    string candidate = Path.Combine(dir, VendorFileName(f.DevicePath));
                    if (VendorFileName(f.DevicePath).Length > 0 && File.Exists(candidate)) { try { CopyVendorFile(f, candidate); auto++; } catch { } }
                }
            }
            _masterView = "vendor";
            RenderMasterContent();

            var text = new List<string>();
            foreach (var b in touched) text.Add(VendorImportSummary(b));
            if (mergeNotes.Count > 0) text.Add(Loc.T("Fusion :") + "\n" + string.Join("\n", mergeNotes));
            if (auto > 0) text.Add(Loc.F("{0} fichier(s) fourni(s) automatiquement (trouvés à côté du XML).", auto));
            if (bad.Count > 0) text.Add(Loc.T("Non lus :") + "\n" + string.Join("\n", bad));
            await ShowCustomDialogAsync(others.Count > 0 ? Loc.T("🏭 Profil constructeur importé") : Loc.T("🏭 Profil StageNow importé"), string.Join("\n\n", text), "OK", null, false);
        }

        /// <summary>Résumé d'un bloc après import : étapes par thème, sensibles, non reconnues, fichiers.</summary>
        private string VendorImportSummary(MasterVendorBlock b)
        {
            var p = VendorProfile(b);
            if (!p.Ok) return Loc.F("« {0} » : XML invalide (ligne {1}) : {2}", b.Name, p.ErrorLine, p.Error);
            var lines = new List<string>();
            if (b.Kind != VendorKinds.StageNow) lines.Add(Loc.F("• Format : {0} ({1})", VendorKinds.FormatName(b.Kind), b.Brand));
            foreach (var g in p.Steps.Where(s => !s.Sensitive).GroupBy(s => s.Theme).OrderBy(g => Array.IndexOf(StageNowReader.ThemeOrder, g.Key)))
                lines.Add(Loc.F("• {0} : {1} étape(s)", Loc.T(g.Key), g.Count()));
            int sens = p.Steps.Count(s => s.Sensitive), unknown = p.Steps.Count(s => !s.Recognized);
            if (sens > 0) lines.Add(Loc.F("• Étapes sensibles : {0}, décochées (confirmation demandée si vous les cochez)", sens));
            if (unknown > 0) lines.Add(Loc.F("• Non reconnues : {0} (gardées telles quelles, appliquées sans aperçu)", unknown));
            if (b.Files.Count > 0) lines.Add(Loc.F("• Fichiers du profil : {0}, dont {1} fourni(s)", b.Files.Count, b.Files.Count(f => f.Provided)));
            string text = Loc.F("« {0} » : {1} étape(s) lue(s){2}.\n\n{3}", b.Name, p.Steps.Count, p.MinVersion.Length > 0 ? Loc.F(", faite(s) pour MX {0}", p.MinVersion) : "", string.Join("\n", lines));
            if (b.Files.Any(f => f.Download && !f.Provided))
                text += "\n\n" + Loc.F("⚠️ {0} fichier(s) téléchargé(s) par le terminal depuis un lien (souvent chiffré par StageNow) : le terminal devra joindre ce serveur. Pour ne pas en dépendre, fournissez le fichier dans « 📎 Fichiers du profil » (✏️ : fichier du PC ou apk d'une appli de l'onglet Applications) : il sera déposé au même endroit et le reste du profil s'en servira.", b.Files.Count(f => f.Download && !f.Provided));
            if (b.Files.Any(f => !f.Download && !f.Provided))
                text += "\n\n" + Loc.T("Joignez les fichiers manquants dans « 📎 Fichiers du profil » (✏️).");
            return text;
        }

        /// <summary>Fusionne un bloc dans un autre (même marque) : étapes, fichiers fournis et choix des cases.</summary>
        private async Task MergeVendorBlocksAsync(MasterVendorBlock from, MasterVendorBlock into)
        {
            MxMergeResult r;
            try { r = StageNowReader.Merge(into.Xml, from.Xml); }
            catch (Exception ex) { await ShowCustomDialogAsync(Loc.T("Fusionner les profils ?"), ex.Message, "OK", null, false); return; }
            into.Xml = r.Xml;
            try { into.OriginalXml = StageNowReader.Merge(into.OriginalXml, from.OriginalXml).Xml; } catch { into.OriginalXml = r.Xml; }
            into.Name += " + " + from.Name;
            into.SourceFile += ", " + from.SourceFile;
            foreach (var k in from.Unchecked) if (!into.Unchecked.Contains(k)) into.Unchecked.Add(k);
            foreach (var k in from.CheckedSensitive) if (!into.CheckedSensitive.Contains(k)) into.CheckedSensitive.Add(k);
            foreach (var f in from.Files.Where(f => f.Provided))
                if (!into.Files.Any(x => x.DevicePath == f.DevicePath && x.Provided)) { into.Files.RemoveAll(x => x.DevicePath == f.DevicePath); into.Files.Add(f); }
            if (_vendorEditors.TryGetValue(from, out var ed)) { try { ed.Close(); } catch { } _vendorEditors.Remove(from); }
            if (_vendorEditors.TryGetValue(into, out var ed2)) { try { ed2.Close(); } catch { } _vendorEditors.Remove(into); }
            _master.Vendor.Remove(from);
            SyncVendorFiles(into);
            RerenderMaster();
            string msg = Loc.F("« {0} » : {1} étape(s) ajoutée(s), {2} déjà présente(s).", from.Name, r.Added, r.Duplicates);
            if (r.SameTypes.Count > 0)
                msg += "\n\n" + Loc.F("⚠️ Réglés des deux côtés : {0}. MX applique dans l'ordre : la dernière valeur l'emporte (à vérifier dans le XML).", string.Join(", ", r.SameTypes));
            await ShowCustomDialogAsync(Loc.T("Profils fusionnés"), msg, "OK", null, false);
        }

        private string VendorFileSource(MasterVendorFile f)
        {
            if (!string.IsNullOrEmpty(f.AppPackage)) return Loc.F("fourni par l'appli {0} (onglet Applications)", f.AppPackage);
            if (!string.IsNullOrEmpty(f.Local)) return Loc.F("fourni : {0}", Path.GetFileName(f.Local));
            if (f.Download) return f.KeepDownload ? Loc.T("téléchargé par le terminal (choisi)") : Loc.T("téléchargé par le terminal depuis un lien");
            return Loc.T("lu sur le terminal");
        }

        /// <summary>Choix de la source d'un fichier du profil : fichier du PC, apk d'une appli du Master, ou téléchargement par le terminal.</summary>
        private void ShowVendorFileMenu(MasterVendorBlock b, MasterVendorFile f)
        {
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            var pc = new MenuItem { Header = Loc.T("📂 Fichier du PC…") };
            pc.Click += (s, a) => AttachVendorFile(b, f);
            menu.Items.Add(pc);

            // Applis du Master à une seule apk (les applis en plusieurs morceaux ne se déposent pas en un fichier) ;
            // celles dont le nom ressemble au fichier en premier.
            string name = Path.GetFileNameWithoutExtension(VendorFileName(f.DevicePath)).ToLowerInvariant();
            var tokens = name.Split(new[] { '_', '-', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 4).ToList();
            var apps = _master.Apps.Where(x => x.Files.Count == 1)
                .OrderByDescending(x => tokens.Count(t => x.Package.ToLowerInvariant().Contains(t)))
                .ThenBy(x => x.Package).ToList();
            var fromApp = new MenuItem { Header = Loc.T("📦 Apk d'une appli du Master") };
            if (apps.Count == 0 || !VendorFileName(f.DevicePath).EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                fromApp.IsEnabled = false;
                fromApp.Header = Loc.T("📦 Apk d'une appli du Master (aucune utilisable ici)");
            }
            foreach (var app in apps)
            {
                var item = new MenuItem { Header = string.IsNullOrEmpty(app.VersionName) ? app.Package : $"{app.Package} ({app.VersionName})", IsChecked = f.AppPackage == app.Package };
                item.Click += (s, a) =>
                {
                    f.AppPackage = app.Package;
                    f.Local = "";
                    f.KeepDownload = false;
                    RenderMasterView();
                };
                fromApp.Items.Add(item);
            }
            menu.Items.Add(fromApp);

            if (f.Download)
            {
                var keep = new MenuItem { Header = Loc.T("🌐 Laisser le terminal le télécharger"), IsChecked = !f.Provided && f.KeepDownload };
                keep.Click += (s, a) =>
                {
                    f.Local = "";
                    f.AppPackage = "";
                    f.KeepDownload = true;
                    RenderMasterView();
                };
                menu.Items.Add(keep);
            }
            menu.IsOpen = true;
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
        private void OpenVendorEditor(MasterVendorBlock block, int line, bool catalog = false)
        {
            if (!_vendorEditors.TryGetValue(block, out var ed))
            {
                ed = new MasterXmlEditor(block) { Owner = this };
                ed.Changed += () => Dispatcher.BeginInvoke(new Action(() => OnVendorXmlChanged(block)));
                ed.FileAttached += (devicePath, local) => AttachTemplateFile(block, devicePath, local);
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
            if (catalog) ed.ShowCatalog();
        }

        /// <summary>« Créer un profil Zebra vide » : un bloc sans étape, ouvert dans l'éditeur sur le catalogue des modèles.</summary>
        private async Task CreateEmptyVendorBlockAsync()
        {
            string name = await MasterPromptAsync(Loc.T("Nouveau profil Zebra"), Loc.T("Nom du profil, pour s'y retrouver dans le Master :"), Loc.T("Nouveau profil"), Loc.T("Créer"));
            if (name == null) return;
            name = name.Trim().Length > 0 ? name.Trim() : Loc.T("Nouveau profil");
            string xml = MxTemplates.EmptyProfile();
            var b = new MasterVendorBlock { Brand = VendorModules.Zebra, Kind = "stagenow", Name = name, SourceFile = "", ImportedAt = DateTime.Now, Xml = xml, OriginalXml = xml };
            _master.Vendor.Add(b);
            RerenderMaster();
            OpenVendorEditor(b, 0, true);
        }

        /// <summary>Fichier du PC choisi dans un modèle d'étape : joint au bloc pour le chemin que l'étape lit sur le terminal.</summary>
        private void AttachTemplateFile(MasterVendorBlock block, string devicePath, string local)
        {
            if (_master == null || !_master.Vendor.Contains(block)) return;
            try
            {
                SyncVendorFiles(block);
                var f = VendorFileFor(block, devicePath);
                if (f == null) block.Files.Add(f = new MasterVendorFile { DevicePath = devicePath });
                CopyVendorFile(f, local);
                OnVendorXmlChanged(block);
            }
            catch (Exception ex)
            {
                _ = ShowCustomDialogAsync(Loc.T("Fichier du profil"), Loc.F("Le fichier n'a pas pu être joint : {0}", ex.Message), "OK", null, false);
            }
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
            m.Vendor.Any(b => b.Include && b.Kind == VendorKinds.StageNow && b.Brand == t.Brand && VendorProfile(b) is MxProfile p && p.Ok && p.Steps.Any(b.IsChecked))
            || (t.Brand == VendorModules.Zebra && m.Files.Any(f => f.Include && !f.IsFolder && IsProtectedZebraPath(f.DevicePath)));

        /// <summary>Dossier de Zebra fermé à ADB (/enterprise…) : on y dépose par MX (FileMgr), sans root.</summary>
        private static bool IsProtectedZebraPath(string devicePath) =>
            (devicePath ?? "").StartsWith("/enterprise/", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Dépose un fichier dans un dossier protégé d'un Zebra : copié d'abord dans /sdcard/Download, puis recopié à sa
        /// place par une étape MX FileMgr (mini appli, EMDK), enfin la copie temporaire est effacée.
        /// </summary>
        private async Task<(bool Ok, string Why)> PushViaMxAsync(MasterTarget t, string local, string targetFile)
        {
            if (!await EnsureMasterHelperAsync(t, MasterHelperApp.MxMinVersion)) return (false, Loc.T("mini appli ADB Tools Helper non installée"));
            string stage = "/sdcard/Download/adbtools_mx_" + Path.GetFileName(local).Replace(' ', '_');
            var push = await _adbManager.ExecuteAsync($"-s {t.Serial} push {AdbManager.QuoteLocalArg(local)} {AdbManager.QuoteLocalArg(stage)}", TimeSpan.FromMinutes(10));
            if (!push.IsSuccess) return (false, LastLines(push.StandardError + push.StandardOutput, 1));
            string E(string v) => System.Security.SecurityElement.Escape(v);
            string xml = "<characteristic type=\"FileMgr\"><parm name=\"FileAction\" value=\"1\"/><characteristic type=\"file-details\">"
                + "<parm name=\"TargetAccessMethod\" value=\"2\"/><parm name=\"TargetPathAndFileName\" value=\"" + E(targetFile) + "\"/>"
                + "<parm name=\"IfDuplicate\" value=\"1\"/><parm name=\"SourceAccessMethod\" value=\"2\"/>"
                + "<parm name=\"SourcePathAndFileName\" value=\"" + E(stage) + "\"/></characteristic></characteristic>";
            var output = await RunMasterScriptAsync(t.Serial, MasterHelperApp.ApplyMxCommand(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(xml))) + " 2>&1\n");
            await _adbManager.ExecuteAsync($"-s {t.Serial} shell rm -f {MasterProfile.ShellQuote(stage)}", TimeSpan.FromSeconds(15));
            string raw = string.Join("\n", output);
            var br = MasterProfile.ParseBroadcastResult(raw);
            var (status, response) = StageNowReader.ParseHelperData(br.Data);
            if (StageNowReader.IsMxSuccess(br.Code, status, response)) return (true, "");
            if (status == "NO_EMDK") return (false, Loc.T("EMDK absent sur ce terminal : les réglages Zebra ne peuvent pas passer par la mini appli"));
            if (br.Code == 0) return (false, Loc.T("pas de réponse de la mini appli") + (raw.Length > 0 ? " — " + LastLines(raw, 1) : ""));
            return (false, StageNowReader.RefusalText(status, response));
        }

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
                // Formats appliqués d'un bloc (Datalogic, Honeywell) : envoyés une seule fois, au premier passage
                // s'il n'y a pas d'étape sensible confirmée, sinon au passage des étapes sensibles (en dernier).
                if (b.Kind != VendorKinds.StageNow)
                {
                    bool hasSensitive = p.Steps.Any(s => s.Sensitive && b.IsChecked(s)) && !_vendorSkipSensitive;
                    if (sensitive != hasSensitive || !p.Steps.Any(b.IsChecked)) continue;
                    if (t.Brand != b.Brand)
                    {
                        MasterAdd(t, cat, Loc.F("{0} étape(s)", p.Steps.Count(b.IsChecked)), "skip", Loc.F("réglages {0} ignorés sur ce terminal ({1})", b.Brand, t.Manufacturer.Length > 0 ? t.Manufacturer : Loc.T("autre marque")));
                        continue;
                    }
                    await ApplyWholeVendorBlockAsync(t, b, p, cat);
                    continue;
                }
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

                // Téléchargements remplacés par un fichier fourni : l'étape est sautée, le fichier est déposé à sa place.
                var replaced = steps.Where(s => s.DownloadTarget.Length > 0 && (VendorFileFor(b, s.DownloadTarget)?.Provided ?? false)).ToList();
                foreach (var s in replaced) MasterAdd(t, cat, s.Label, "skip", Loc.T("remplacée par le fichier fourni"));
                steps = steps.Except(replaced).ToList();
                if (!sensitive)
                {
                    foreach (var path in steps.SelectMany(s => s.DeviceFiles).Concat(replaced.Select(s => s.DownloadTarget)).Distinct())
                    {
                        var f = VendorFileFor(b, path);
                        if (f == null || !f.Provided) continue;
                        string local;
                        if (!string.IsNullOrEmpty(f.AppPackage))
                        {
                            var app = m.Apps.FirstOrDefault(x => x.Package == f.AppPackage && x.Files.Count == 1);
                            if (app == null)
                            {
                                MasterAdd(t, cat, Loc.F("Fichier {0} → {1}", VendorFileName(path), path), "ko", Loc.F("appli {0} absente de l'onglet Applications", f.AppPackage));
                                continue;
                            }
                            local = Path.Combine(_masterDir, app.Files[0].Replace('/', Path.DirectorySeparatorChar));
                        }
                        else local = Path.Combine(_masterDir, f.Local.Replace('/', Path.DirectorySeparatorChar));
                        string dir = path.Substring(0, Math.Max(1, path.LastIndexOf('/')));
                        MasterStatus(t, Loc.F("Fichier : {0}…", VendorFileName(path)));
                        await _adbManager.ExecuteAsync($"-s {t.Serial} shell mkdir -p {MasterProfile.ShellQuote(dir)}", TimeSpan.FromSeconds(15));
                        var res = await _adbManager.ExecuteAsync($"-s {t.Serial} push {AdbManager.QuoteLocalArg(local)} {AdbManager.QuoteLocalArg(path)}", TimeSpan.FromMinutes(10));
                        MasterAdd(t, cat, Loc.F("Fichier {0} → {1}", VendorFileName(path), path), res.IsSuccess ? "ok" : "ko", res.IsSuccess ? "" : LastLines(res.StandardError + res.StandardOutput, 1));
                    }
                }
                if (steps.Count == 0) continue;
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
                    if (StageNowReader.IsMxSuccess(br.Code, status, response)) { MasterAdd(t, cat, s.Label, "ok"); continue; }
                    if (status == "NO_EMDK")
                    {
                        noEmdk = true;
                        MasterAdd(t, cat, s.Label, "ko", Loc.T("EMDK absent sur ce terminal : les réglages Zebra ne peuvent pas passer par la mini appli"));
                        continue;
                    }
                    string why = br.Code == 0
                        ? Loc.T("pas de réponse de la mini appli") + (sensitive ? " " + Loc.T("(normal si l'étape a coupé le débogage USB ou redémarré le terminal)") : "") + (raw.Length > 0 ? " — " + LastLines(raw, 1) : "")
                        : StageNowReader.RefusalText(status, response);
                    MasterAdd(t, cat, s.Label, "ko", why);
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🏭 {t.Serial} · {s.Label} : {status} {response}");
                }
            }
        }
        /// <summary>
        /// Datalogic (Scan2Deploy) et Honeywell (DeviceConfig) : le profil part en une fois, avec les seules étapes cochées
        /// (et sans les sensibles si elles n'ont pas été confirmées). Chaque étape envoyée a sa ligne au bilan.
        /// </summary>
        private async Task ApplyWholeVendorBlockAsync(MasterTarget t, MasterVendorBlock b, MxProfile p, string cat)
        {
            bool Keep(MxStep s) => b.IsChecked(s) && !(s.Sensitive && _vendorSkipSensitive);
            var sent = p.Steps.Where(Keep).ToList();
            foreach (var s in p.Steps.Where(s => s.Sensitive && b.IsChecked(s) && _vendorSkipSensitive))
                MasterAdd(t, cat, s.Label, "skip", Loc.T("étape sensible non confirmée"));
            if (sent.Count == 0) return;
            string content;
            try { content = VendorKinds.BuildChecked(b.Kind, b.Xml, Keep); }
            catch (Exception ex) { MasterAdd(t, cat, Loc.T("Profil"), "ko", ex.Message); return; }
            void All(string status, string why) { foreach (var s in sent) MasterAdd(t, cat, s.Label, status, why); }

            string local = Path.Combine(Path.GetTempPath(), "adbtools_vendor_" + Guid.NewGuid().ToString("N") + (VendorKinds.IsJson(b.Kind) ? ".json" : ".xml"));
            try
            {
                File.WriteAllText(local, content, new System.Text.UTF8Encoding(false));
                if (b.Kind == VendorKinds.Scan2Deploy)
                {
                    var agent = await _adbManager.ExecuteAsync($"-s {t.Serial} shell pm list packages {Scan2DeployReader.Agent}", TimeSpan.FromSeconds(20));
                    if (!(agent.StandardOutput ?? "").Contains("package:" + Scan2DeployReader.Agent))
                    {
                        All("ko", Loc.T("agent Scan2Deploy absent de ce terminal (Datalogic Android 9 et plus)"));
                        return;
                    }
                    MasterStatus(t, Loc.T("Constructeur : profil Scan2Deploy…"));
                    string remote = "/sdcard/Download/adbtools_s2d.json";
                    var push = await _adbManager.ExecuteAsync($"-s {t.Serial} push {AdbManager.QuoteLocalArg(local)} {remote}", TimeSpan.FromMinutes(2));
                    if (!push.IsSuccess) { All("ko", LastLines(push.StandardError + push.StandardOutput, 1)); return; }
                    string before = (await _adbManager.ExecuteAsync($"-s {t.Serial} shell cat {Scan2DeployReader.StatusFile} 2>/dev/null", TimeSpan.FromSeconds(15))).StandardOutput ?? "";
                    var br = await _adbManager.ExecuteAsync($"-s {t.Serial} shell {AdbManager.QuoteLocalArg(Scan2DeployReader.ApplyCommand(remote))}", TimeSpan.FromSeconds(30));
                    if (!br.IsSuccess) { All("ko", LastLines(br.StandardError + br.StandardOutput, 1)); return; }
                    if (Scan2DeployReader.EndsWithRestart(content))
                    {
                        All("ok", Loc.T("envoyé à l'agent ; le terminal redémarre ou se réinitialise à la fin : pas de compte rendu"));
                        return;
                    }
                    // Compte rendu : S2DStatus.log est réécrit à la fin de chaque session de l'agent.
                    var limit = DateTime.Now.AddSeconds(Scan2DeployReader.HasDeployment(content) ? 300 : 90);
                    (bool Success, string Timestamp, string Details)? status = null;
                    while (DateTime.Now < limit)
                    {
                        await Task.Delay(2000);
                        string now = (await _adbManager.ExecuteAsync($"-s {t.Serial} shell cat {Scan2DeployReader.StatusFile} 2>/dev/null", TimeSpan.FromSeconds(15))).StandardOutput ?? "";
                        if (now.Trim().Length > 0 && now != before) { status = Scan2DeployReader.ParseStatus(now); if (status != null) break; }
                    }
                    await _adbManager.ExecuteAsync($"-s {t.Serial} shell rm -f {remote}", TimeSpan.FromSeconds(15));
                    if (status == null) All("ko", Loc.T("pas de compte rendu de l'agent Scan2Deploy (S2DStatus.log) dans le délai : vérifier sur le terminal"));
                    else if (status.Value.Success) All("ok", "");
                    else
                    {
                        All("ko", Loc.F("l'agent signale une erreur : {0}", LastLines(status.Value.Details, 2)));
                        AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🏭 {t.Serial} · Scan2Deploy :\n{status.Value.Details}");
                    }
                }
                else if (b.Kind == VendorKinds.DeviceConfig)
                {
                    MasterStatus(t, Loc.T("Constructeur : DeviceConfig Honeywell…"));
                    string dir = DeviceConfigReader.PersistDir, remote = dir + "/" + DeviceConfigReader.FileName;
                    // Le dossier honeywell n'est accessible qu'en mode Provisioning.
                    var probe = await _adbManager.ExecuteAsync($"-s {t.Serial} shell \"mkdir -p {dir} && touch {dir}/.adbtools && rm -f {dir}/.adbtools && echo OK\"", TimeSpan.FromSeconds(20));
                    if (!(probe.StandardOutput ?? "").Contains("OK"))
                    {
                        All("ko", Loc.T("dossier honeywell/persist inaccessible : activez le mode Provisioning sur le terminal (Paramètres ▸ Honeywell Settings ▸ Provisioning mode), puis relancez"));
                        return;
                    }
                    // Un DeviceConfig.xml déjà là est gardé à côté (.bak) avant d'être remplacé.
                    await _adbManager.ExecuteAsync($"-s {t.Serial} shell \"[ -f {remote} ] && cp -f {remote} {remote}.bak\"", TimeSpan.FromSeconds(15));
                    var push = await _adbManager.ExecuteAsync($"-s {t.Serial} push {AdbManager.QuoteLocalArg(local)} {remote}", TimeSpan.FromMinutes(2));
                    if (!push.IsSuccess) { All("ko", LastLines(push.StandardError + push.StandardOutput, 1)); return; }
                    All("ok", Loc.T("déposé ; appliqué au redémarrage, à la fin du Master"));
                    t.RebootAtEnd = true;
                }
            }
            catch (Exception ex) { All("ko", ex.Message); }
            finally { try { File.Delete(local); } catch { } }
        }
    }
}

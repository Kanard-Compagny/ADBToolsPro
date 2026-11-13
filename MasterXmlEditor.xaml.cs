using ADBToolsNew.Core;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ADBToolsNew
{
    /// <summary>
    /// Éditeur du XML d'un bloc « réglages constructeur » (profil StageNow), dans une fenêtre séparée qu'on peut garder
    /// sur un autre écran. Le XML est relu à chaque frappe (après une courte pause) : valide, il remplace celui du bloc
    /// et le Master se met à jour (Changed) ; invalide, la ligne fautive est soulignée et le bloc garde la dernière version
    /// correcte. L'original importé reste dans le bloc (« Revenir à l'original », « Voir les changements »).
    /// </summary>
    public partial class MasterXmlEditor : Window
    {
        private readonly MasterVendorBlock _block;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private readonly LineMarks _marks = new LineMarks();
        private SearchPanel _search;
        private MxProfile _last;
        private bool _loading;

        /// <summary>Le XML du bloc a changé et il est valide : le Master doit se redessiner.</summary>
        public event Action Changed;

        /// <summary>Un modèle d'étape a joint un fichier du PC : (chemin lu sur le terminal, fichier du PC).</summary>
        public event Action<string, string> FileAttached;

        public MasterVendorBlock Block => _block;
        private bool _json;

        public MasterXmlEditor(MasterVendorBlock block)
        {
            InitializeComponent();
            Loc.TranslateTree(this);
            _block = block;
            _json = VendorKinds.IsJson(block.Kind);
            string title = _json ? Loc.F("JSON du profil · {0}", block.Name) : Loc.F("XML du profil · {0}", block.Name);
            // Catalogue de modèles : étapes MX (Zebra) seulement.
            if (block.Kind != VendorKinds.StageNow) BtnAddStep.Visibility = Visibility.Collapsed;
            TxtWindowTitle.Text = title;
            Title = title + Loc.T(" — ADB Tools");
            MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
            StateChanged += (s, e) =>
            {
                BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
                UpdateFrame();
            };

            ApplyColors();
            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (s, e) => ThemeManager.ThemeChanged -= OnThemeChanged;
            Editor.TextArea.TextView.BackgroundRenderers.Add(_marks);
            _search = SearchPanel.Install(Editor);

            _loading = true;
            Editor.Text = block.Xml ?? "";
            _loading = false;
            Editor.TextChanged += (s, e) => { if (!_loading) { _timer.Stop(); _timer.Start(); } };
            Editor.TextArea.Caret.PositionChanged += (s, e) => UpdateCaret();
            _timer.Tick += (s, e) => { _timer.Stop(); Reparse(true); };
            Reparse(false);
            UpdateCaret();
        }

        private void OnThemeChanged(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() => { ApplyColors(); Editor.TextArea.TextView.Redraw(); }));

        /// <summary>Couleurs de la coloration XML accordées au thème (vert pour les balises, orange pour les valeurs).</summary>
        private void ApplyColors()
        {
            var def = _json ? (HighlightingManager.Instance.GetDefinition("Json") ?? HighlightingManager.Instance.GetDefinition("JavaScript"))
                            : HighlightingManager.Instance.GetDefinition("XML");
            if (def == null) return;
            bool dark = ThemeManager.Current == AppTheme.Dark;
            void Set(string name, string darkHex, string lightHex)
            {
                var c = def.GetNamedColor(name);
                if (c == null) return;
                c.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(dark ? darkHex : lightHex));
            }
            Set("XmlTag", "#73D9A9", "#1F7454");
            Set("AttributeName", "#C5CDD2", "#3E474D");
            Set("AttributeValue", "#FFB868", "#A35200");
            Set("Comment", "#7A848A", "#6B747A");
            Set("XmlDeclaration", "#9AA3A9", "#5D666C");
            Set("CData", "#9CC3F0", "#1D5C9E");
            Set("Entity", "#FFB868", "#A35200");
            // JSON (Scan2Deploy) : clés en vert, valeurs en orange.
            Set("FieldName", "#73D9A9", "#1F7454");
            Set("String", "#FFB868", "#A35200");
            Set("Number", "#9CC3F0", "#1D5C9E");
            Set("Digits", "#9CC3F0", "#1D5C9E");
            Set("Bool", "#9CC3F0", "#1D5C9E");
            Set("Null", "#9CC3F0", "#1D5C9E");
            Set("JavaScriptLiterals", "#9CC3F0", "#1D5C9E");
            Editor.SyntaxHighlighting = null;
            Editor.SyntaxHighlighting = def;
            _marks.Modified = (Brush)FindResource("Accent");
            _marks.Sensitive = (Brush)FindResource("Action");
            _marks.Error = (Brush)FindResource("BadFg");
        }

        /// <summary>Relit le XML ; s'il est valide et a changé, il devient celui du bloc.</summary>
        private void Reparse(bool fromTyping)
        {
            string text = Editor.Text ?? "";
            var p = VendorKinds.Read(_block.Kind, text, true);
            if (!p.Ok)
            {
                _marks.ErrorLine = Math.Max(1, p.ErrorLine);
                DotState.SetResourceReference(Shape_Fill, "BadFg");
                TxtState.SetResourceReference(TextBlock.ForegroundProperty, "BadFg");
                TxtState.Text = _json ? Loc.F("JSON invalide (ligne {0}) · le Master garde la dernière version correcte", p.ErrorLine)
                                      : Loc.F("XML invalide (ligne {0}) · le Master garde la dernière version correcte", p.ErrorLine);
                TxtError.Text = Loc.F("Ligne {0} : {1}", p.ErrorLine, p.Error);
                ErrorBar.Visibility = Visibility.Visible;
            }
            else
            {
                _marks.ErrorLine = 0;
                ErrorBar.Visibility = Visibility.Collapsed;
                DotState.SetResourceReference(Shape_Fill, "OkFg");
                TxtState.SetResourceReference(TextBlock.ForegroundProperty, "OkFg");
                TxtState.Text = _json ? Loc.T("JSON valide · synchronisé avec le Master") : Loc.T("XML valide · synchronisé avec le Master");
                _last = p;
                if (fromTyping && text != _block.Xml)
                {
                    _block.Xml = text;
                    Changed?.Invoke();
                }
                RenderSteps(p);
            }
            // Repères : lignes absentes de l'original (modifiées ou ajoutées) et débuts d'étapes sensibles.
            var original = new HashSet<string>((_block.OriginalXml ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()));
            var lines = text.Replace("\r", "").Split('\n');
            _marks.ModifiedLines = new HashSet<int>(Enumerable.Range(1, lines.Length).Where(i => lines[i - 1].Trim().Length > 0 && !original.Contains(lines[i - 1].Trim())));
            _marks.SensitiveLines = new HashSet<int>((_last?.Steps ?? new List<MxStep>()).Where(s => s.Sensitive).Select(s => s.Line));
            int changes = _marks.ModifiedLines.Count + original.Count(o => o.Length > 0 && !lines.Any(l => l.Trim() == o));
            BtnChanges.Content = changes > 0 ? Loc.F("Voir les changements ({0})", changes) : Loc.T("Voir les changements");
            BtnChanges.IsEnabled = changes > 0;
            BtnRevert.IsEnabled = text != (_block.OriginalXml ?? "");
            if (_last != null)
                TxtCount.Text = Loc.F("{0} étape(s) · {1} sensible(s)", _last.Steps.Count, _last.Steps.Count(s => s.Sensitive));
            Editor.TextArea.TextView.Redraw();
        }

        private static readonly DependencyProperty Shape_Fill = System.Windows.Shapes.Shape.FillProperty;

        private void RenderSteps(MxProfile p)
        {
            StepsHost.Children.Clear();
            if (p.Steps.Count == 0)
            {
                var empty = new TextBlock { Text = Loc.T("Profil vide : « Ajouter une étape » ouvre le catalogue des modèles."), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                StepsHost.Children.Add(empty);
            }
            foreach (var s in p.Steps)
            {
                var step = s;
                var b = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 3), Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
                b.Template = StepTemplate();
                b.SetResourceReference(BackgroundProperty, step.Sensitive ? "WarnBg" : "Surface");
                b.SetResourceReference(BorderBrushProperty, step.Sensitive ? "Action" : "Surface");
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var num = new TextBlock { Text = step.Line.ToString(), FontSize = 12 };
                num.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                g.Children.Add(num);
                var texts = new StackPanel();
                var label = new TextBlock { Text = step.Label, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
                label.SetResourceReference(TextBlock.ForegroundProperty, step.Sensitive ? "WarnFg" : "Ink");
                texts.Children.Add(label);
                string sub = step.Sensitive ? Loc.T("sensible · confirmation demandée") : !step.Recognized ? Loc.T("non reconnue : appliquée telle quelle") : step.Type;
                var subText = new TextBlock { Text = sub, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
                subText.SetResourceReference(TextBlock.ForegroundProperty, step.Sensitive ? "WarnFg" : "Muted");
                texts.Children.Add(subText);
                Grid.SetColumn(texts, 1);
                g.Children.Add(texts);
                b.Content = g;
                b.Click += (s2, e2) => GoToLine(step.Line);
                StepsHost.Children.Add(b);
            }
        }

        private static ControlTemplate StepTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            bd.AppendChild(cp);
            t.VisualTree = bd;
            return t;
        }

        /// <summary>Amène l'éditeur à une ligne et la sélectionne (clic sur une étape, ici ou dans le Master).</summary>
        public void GoToLine(int line)
        {
            if (line < 1 || line > Editor.Document.LineCount) return;
            var l = Editor.Document.GetLineByNumber(line);
            Editor.TextArea.Caret.Offset = l.Offset;
            Editor.Select(l.Offset, l.Length);
            Editor.ScrollToLine(line);
            Editor.Focus();
        }

        private void UpdateCaret()
        {
            var c = Editor.TextArea.Caret;
            TxtCaret.Text = Loc.F("Ligne {0}, col. {1}", c.Line, c.Column);
        }

        // ------------------------------------------------------------------ Modèles d'étapes (« Ajouter une étape »)

        private MxTemplate _template;
        private Dictionary<string, string> _values = new Dictionary<string, string>();
        /// <summary>Fichiers du PC choisis dans le formulaire : clé du champ → fichier local.</summary>
        private readonly Dictionary<string, string> _pendingFiles = new Dictionary<string, string>();
        private bool _insertAtCaret;
        private TextBox _preview;

        private void ShowPanel(FrameworkElement panel)
        {
            StepsPanel.Visibility = panel == StepsPanel ? Visibility.Visible : Visibility.Collapsed;
            CatalogPanel.Visibility = panel == CatalogPanel ? Visibility.Visible : Visibility.Collapsed;
            FormPanel.Visibility = panel == FormPanel ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Ouvre le catalogue des modèles (bouton de la barre, ou « Ajouter une étape » du Master).</summary>
        public void ShowCatalog()
        {
            ShowPanel(CatalogPanel);
            RenderCatalog();
            TxtCatalogSearch.Focus();
        }

        private void BtnAddStep_Click(object sender, RoutedEventArgs e) => ShowCatalog();
        private void BtnShowSteps_Click(object sender, RoutedEventArgs e) => ShowPanel(StepsPanel);
        private void BtnBackToCatalog_Click(object sender, RoutedEventArgs e) => ShowCatalog();

        private void TxtCatalogSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtCatalogHint.Visibility = string.IsNullOrEmpty(TxtCatalogSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (CatalogPanel.Visibility == Visibility.Visible) RenderCatalog();
        }

        private static string Fold(string s)
        {
            var d = (s ?? "").ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
            return new string(d.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        }

        private TextBlock Muted(string text, double size = 12)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return t;
        }

        private void RenderCatalog()
        {
            CatalogHost.Children.Clear();
            var profile = StageNowReader.Read(Editor.Text ?? "", true);
            string q = Fold(TxtCatalogSearch.Text).Trim();
            int shown = 0;
            foreach (var group in MxTemplates.Groups)
            {
                var items = MxTemplates.All.Where(t => t.Group == group && (q.Length == 0
                    || Fold(Loc.T(t.Label) + " " + Loc.T(t.Description) + " " + Loc.T(t.Group) + " " + t.Id).Contains(q))).ToList();
                if (items.Count == 0) continue;
                var head = new TextBlock { Text = Loc.T(group), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, shown == 0 ? 0 : 10, 0, 4) };
                head.SetResourceReference(TextBlock.ForegroundProperty, "BrandText");
                CatalogHost.Children.Add(head);
                foreach (var t in items)
                {
                    var tpl = t;
                    int count = profile.Ok ? MxTemplates.CountIn(tpl, profile) : 0;
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var name = new TextBlock { Text = Loc.T(tpl.Label), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                    g.Children.Add(name);
                    var right = Muted(count > 0 ? Loc.F("déjà {0}", count) : Loc.F("MX {0}+", tpl.MinMx), 11.5);
                    right.VerticalAlignment = VerticalAlignment.Center;
                    right.Margin = new Thickness(8, 0, 0, 0);
                    if (count > 0) right.SetResourceReference(TextBlock.ForegroundProperty, "BrandText");
                    Grid.SetColumn(right, 1);
                    g.Children.Add(right);
                    var b = new Button { Content = g, Style = (Style)FindResource("XmlCatalogItemStyle"), ToolTip = Loc.T(tpl.Description).Length > 0 ? Loc.T(tpl.Description) : null };
                    b.Click += (s, e) => OpenTemplate(tpl);
                    CatalogHost.Children.Add(b);
                    shown++;
                }
            }
            if (shown == 0) CatalogHost.Children.Add(Muted(Loc.T("Aucun modèle ne correspond."), 12.5));
            var note = Muted(Loc.T("Les étapes qui effacent ou réinitialisent le terminal ne sont pas proposées ici : elles restent possibles à la main dans le XML, avec la confirmation APPLIQUER."), 11.5);
            note.Margin = new Thickness(0, 14, 0, 0);
            CatalogHost.Children.Add(note);
        }

        private void OpenTemplate(MxTemplate t)
        {
            _template = t;
            _values = t.Defaults();
            _pendingFiles.Clear();
            _insertAtCaret = false;
            TxtFormTitle.Text = Loc.T(t.Label);
            TxtFormError.Visibility = Visibility.Collapsed;
            ShowPanel(FormPanel);
            RenderForm();
        }

        private void RenderForm()
        {
            FormHost.Children.Clear();
            var t = _template;
            if (t == null) return;
            var intro = Muted((Loc.T(t.Description) + " " + Loc.F("MX {0} et plus.", t.MinMx)).Trim(), 12);
            intro.Margin = new Thickness(0, 6, 0, 10);
            FormHost.Children.Add(intro);

            foreach (var f in t.Fields.Where(f => f.IsVisible(_values)))
            {
                var field = f;
                var label = Muted(Loc.T(field.Label), 12);
                label.Margin = new Thickness(0, 0, 0, 4);
                FormHost.Children.Add(label);
                _values.TryGetValue(field.Key, out string current);
                switch (field.Kind)
                {
                    case MxFieldKind.Choice:
                    {
                        var wrap = new WrapPanel();
                        string group = "tpl_" + field.Key + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                        foreach (var (value, text) in field.Options)
                        {
                            var v = value;
                            var rb = new RadioButton { Content = Loc.T(text), GroupName = group, IsChecked = v == current, Style = (Style)FindResource("XmlPillStyle") };
                            rb.Checked += (s, e) =>
                            {
                                _values[field.Key] = v;
                                // Un choix peut montrer ou cacher d'autres champs (mot de passe d'un réseau ouvert…).
                                if (t.Fields.Any(x => x.VisibleWhen != null)) Dispatcher.BeginInvoke(new Action(RenderForm));
                                else UpdatePreview();
                            };
                            wrap.Children.Add(rb);
                        }
                        FormHost.Children.Add(wrap);
                        break;
                    }
                    case MxFieldKind.Password:
                    {
                        var pb = new PasswordBox { Password = current ?? "" };
                        pb.PasswordChanged += (s, e) => { _values[field.Key] = pb.Password; UpdatePreview(); };
                        FormHost.Children.Add(pb);
                        break;
                    }
                    case MxFieldKind.DeviceFile:
                    {
                        var dock = new DockPanel();
                        var pick = new Button { Content = Loc.T("Choisir sur le PC…"), Style = (Style)FindResource("XmlToolButtonStyle"), Margin = new Thickness(6, 0, 0, 0) };
                        DockPanel.SetDock(pick, Dock.Right);
                        var tb = new TextBox { Text = current ?? "", ToolTip = Loc.T("Chemin du fichier sur le terminal") };
                        var attached = Muted(_pendingFiles.TryGetValue(field.Key, out string l0) ? Loc.F("Joint au Master : {0}", System.IO.Path.GetFileName(l0)) : (field.Hint.Length > 0 ? Loc.F("ex. : {0}", field.Hint) : ""), 11.5);
                        attached.Margin = new Thickness(0, 3, 0, 0);
                        tb.TextChanged += (s, e) =>
                        {
                            _values[field.Key] = tb.Text;
                            // Chemin changé à la main : le fichier du PC n'est plus celui qui sera lu.
                            if (_pendingFiles.TryGetValue(field.Key, out string local) && tb.Text.Trim() != MxTemplates.Staging + System.IO.Path.GetFileName(local))
                            {
                                _pendingFiles.Remove(field.Key);
                                attached.Text = Loc.T("Fichier à joindre ensuite dans « Fichiers du profil » du Master.");
                            }
                            UpdatePreview();
                        };
                        pick.Click += (s, e) =>
                        {
                            string ext = field.FileFilter.Length > 0 ? field.FileFilter : "*.*";
                            var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T(field.Label), Filter = Loc.F("Fichier ({0})|{0}|Tous les fichiers (*.*)|*.*", ext) };
                            if (dlg.ShowDialog(this) != true) return;
                            string path = MxTemplates.Staging + System.IO.Path.GetFileName(dlg.FileName);
                            _pendingFiles[field.Key] = dlg.FileName;
                            tb.Text = path;
                            _pendingFiles[field.Key] = dlg.FileName;
                            attached.Text = Loc.F("Joint au Master : {0}", System.IO.Path.GetFileName(dlg.FileName));
                        };
                        dock.Children.Add(pick);
                        dock.Children.Add(tb);
                        FormHost.Children.Add(dock);
                        FormHost.Children.Add(attached);
                        break;
                    }
                    default:
                    {
                        var tb = new TextBox { Text = current ?? "", ToolTip = field.Hint.Length > 0 ? Loc.F("ex. : {0}", field.Hint) : null };
                        tb.TextChanged += (s, e) => { _values[field.Key] = tb.Text; UpdatePreview(); };
                        FormHost.Children.Add(tb);
                        if (field.Kind == MxFieldKind.Folder)
                        {
                            // Dossiers déjà utilisés dans les Masters, puis les dossiers usuels des Zebra.
                            var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                            var folders = DevicePathMemory.Recent(AppSettings.Current.MasterDevicePaths, 6)
                                .Concat(new[] { "/enterprise/usr/", "/enterprise/usr/persist/", MxTemplates.Staging })
                                .Distinct(StringComparer.Ordinal).Take(8);
                            foreach (var folder in folders)
                            {
                                var path = folder;
                                var chip = new Button { Content = path, Style = (Style)FindResource("XmlToolButtonStyle"), Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 6), FontSize = 11.5 };
                                chip.Click += (s, e) => tb.Text = path;
                                chips.Children.Add(chip);
                            }
                            FormHost.Children.Add(chips);
                        }
                        else if (field.Hint.Length > 0)
                        {
                            var hint = Muted(Loc.F("ex. : {0}", field.Hint), 11.5);
                            hint.Margin = new Thickness(0, 3, 0, 0);
                            FormHost.Children.Add(hint);
                        }
                        break;
                    }
                }
                FormHost.Children.Add(new Border { Height = 10 });
            }

            if (t.Warning.Length > 0)
            {
                var warn = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10) };
                warn.SetResourceReference(Border.BackgroundProperty, "WarnBg");
                var wt = new TextBlock { Text = Loc.T(t.Warning), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                wt.SetResourceReference(TextBlock.ForegroundProperty, "WarnFg");
                warn.Child = wt;
                FormHost.Children.Add(warn);
            }

            var where = Muted(Loc.T("Où insérer"), 12);
            where.Margin = new Thickness(0, 0, 0, 4);
            FormHost.Children.Add(where);
            if (t.MustBeLast) FormHost.Children.Add(Muted(Loc.T("Toujours en dernier : rien ne s'exécute après un redémarrage."), 12));
            else
            {
                var pos = new WrapPanel();
                var end = new RadioButton { Content = Loc.T("À la fin du profil"), GroupName = "tpl_where", IsChecked = !_insertAtCaret, Style = (Style)FindResource("XmlPillStyle") };
                end.Checked += (s, e) => _insertAtCaret = false;
                var caret = new RadioButton { Content = Loc.F("Après l'étape du curseur (ligne {0})", Editor.TextArea.Caret.Line), GroupName = "tpl_where", IsChecked = _insertAtCaret, Style = (Style)FindResource("XmlPillStyle") };
                caret.Checked += (s, e) => _insertAtCaret = true;
                pos.Children.Add(end);
                pos.Children.Add(caret);
                FormHost.Children.Add(pos);
            }

            var pv = Muted(Loc.T("XML ajouté"), 12);
            pv.Margin = new Thickness(0, 10, 0, 4);
            FormHost.Children.Add(pv);
            _preview = new TextBox { IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11.5, Height = double.NaN, MinHeight = 60, MaxHeight = 260,
                TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(6, 4, 6, 4) };
            FormHost.Children.Add(_preview);
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (_preview == null || _template == null) return;
            try
            {
                string xml = MxTemplates.BuildXml(_template, _values);
                // Mot de passe masqué à l'écran (il est bien écrit dans le XML inséré).
                _preview.Text = System.Text.RegularExpressions.Regex.Replace(xml, "(name=\"Passphrase[^\"]*\" value=\")[^\"]*", m => m.Groups[1].Value + "••••••••");
            }
            catch (Exception ex) { _preview.Text = ex.Message; }
            TxtFormError.Visibility = Visibility.Collapsed;
        }

        private void BtnInsertStep_Click(object sender, RoutedEventArgs e)
        {
            var t = _template;
            if (t == null) return;
            string err = MxTemplates.Validate(t, _values);
            if (err != null) { TxtFormError.Text = err; TxtFormError.Visibility = Visibility.Visible; return; }
            MxTemplates.InsertResult r;
            try { r = MxTemplates.Insert(Editor.Text ?? "", MxTemplates.BuildXml(t, _values), _insertAtCaret ? Editor.TextArea.Caret.Line : 0, t.MustBeLast); }
            catch
            {
                TxtFormError.Text = Loc.T("Le XML actuel est invalide : corrigez-le (barre rouge) avant d'ajouter une étape.");
                TxtFormError.Visibility = Visibility.Visible;
                return;
            }

            // Une seule opération : un Ctrl+Z la retire.
            Editor.Document.Replace(0, Editor.Document.TextLength, r.Xml);
            _timer.Stop();
            Reparse(true);

            foreach (var kv in _pendingFiles)
            {
                if (!_values.TryGetValue(kv.Key, out string devicePath) || devicePath.Trim().Length == 0) continue;
                FileAttached?.Invoke(devicePath.Trim(), kv.Value);
            }
            if (t.Fields.Any(f => f.Kind == MxFieldKind.Folder))
            {
                string folder = t.Fields.Where(f => f.Kind == MxFieldKind.Folder).Select(f => _values.TryGetValue(f.Key, out string v) ? v : "").FirstOrDefault() ?? "";
                string file = _pendingFiles.Values.FirstOrDefault() ?? (_values.TryGetValue("source", out string src) ? src : "");
                if (folder.Trim().Length > 0) { DevicePathMemory.Remember(AppSettings.Current.MasterDevicePaths, file, folder); AppSettings.Save(); }
            }
            _pendingFiles.Clear();

            ShowPanel(StepsPanel);
            var first = Editor.Document.GetLineByNumber(Math.Min(r.FirstLine, Editor.Document.LineCount));
            var last = Editor.Document.GetLineByNumber(Math.Min(r.FirstLine + r.LineCount - 1, Editor.Document.LineCount));
            Editor.TextArea.Caret.Offset = first.Offset;
            Editor.Select(first.Offset, last.EndOffset - first.Offset);
            Editor.ScrollToLine(r.FirstLine);
            Editor.Focus();
        }

        // ------------------------------------------------------------------ Barre d'outils

        private void BtnFind_Click(object sender, RoutedEventArgs e) => _search.Open();

        private void BtnRevert_Click(object sender, RoutedEventArgs e)
        {
            if (!DocumentWindow.Confirm(this, "Revenir à l'original", "Remettre le XML importé, sans vos modifications ?", "Revenir à l'original", "Annuler")) return;
            Editor.Text = _block.OriginalXml ?? "";
            _timer.Stop();
            Reparse(true);
        }

        /// <summary>Lignes retirées (−) et ajoutées (+) par rapport à l'original importé.</summary>
        private void BtnChanges_Click(object sender, RoutedEventArgs e)
        {
            var orig = (_block.OriginalXml ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var now = (Editor.Text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var removed = orig.Where(o => !now.Contains(o)).ToList();
            var added = now.Where(n => !orig.Contains(n)).ToList();
            var panel = new StackPanel();
            void Add(string prefix, string line, string brush)
            {
                var t = new TextBlock { Text = prefix + " " + line, FontFamily = new FontFamily("Consolas"), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
                t.SetResourceReference(TextBlock.ForegroundProperty, brush);
                panel.Children.Add(t);
            }
            foreach (var r in removed) Add("−", r, "BadFg");
            foreach (var a in added) Add("+", a, "OkFg");
            if (removed.Count + added.Count == 0) Add("", Loc.T("Aucun changement."), "Muted");
            DocumentWindow.ShowContent(this, "Changements par rapport à l'original", panel, 840, 560);
        }

        /// <summary>Coins arrondis et ombre en fenêtre normale ; plein cadre, sans marge ni arrondi, une fois agrandie.</summary>
        private void UpdateFrame()
        {
            bool max = WindowState == WindowState.Maximized;
            var margin = new Thickness(max ? 0 : 12);
            var radius = new CornerRadius(max ? 0 : 10);
            RootBorder.Margin = WindowShadow.Margin = margin;
            RootBorder.CornerRadius = WindowShadow.CornerRadius = radius;
            RootBorder.BorderThickness = new Thickness(max ? 0 : 1);
            WindowShadow.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
            UpdateClip();
        }

        private void RootBorder_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

        /// <summary>Découpe le contenu suivant les coins (un Border n'arrondit que son fond, pas ce qu'il contient).</summary>
        private void UpdateClip()
        {
            double r = WindowState == WindowState.Maximized ? 0 : 9;
            double w = ContentGrid.ActualWidth, h = ContentGrid.ActualHeight;
            ContentGrid.Clip = w > 0 && h > 0 ? new RectangleGeometry(new Rect(0, 0, w, h), r, r) : null;
        }

        private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void BtnMax_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Repères dans la marge du texte : ligne modifiée (vert), étape sensible (orange), ligne en erreur (soulignée).</summary>
        private sealed class LineMarks : IBackgroundRenderer
        {
            public HashSet<int> ModifiedLines = new HashSet<int>();
            public HashSet<int> SensitiveLines = new HashSet<int>();
            public int ErrorLine;
            public Brush Modified = Brushes.SeaGreen, Sensitive = Brushes.Orange, Error = Brushes.IndianRed;

            public KnownLayer Layer => KnownLayer.Background;

            public void Draw(TextView textView, DrawingContext dc)
            {
                if (!textView.VisualLinesValid) return;
                foreach (var vl in textView.VisualLines)
                {
                    int n = vl.FirstDocumentLine.LineNumber;
                    double top = vl.VisualTop - textView.VerticalOffset, h = vl.Height;
                    if (SensitiveLines.Contains(n)) dc.DrawRectangle(Sensitive, null, new Rect(0, top, 4, h));
                    else if (ModifiedLines.Contains(n)) dc.DrawRectangle(Modified, null, new Rect(0, top, 4, h));
                    if (n == ErrorLine)
                    {
                        double y = top + h - 1.5, w = Math.Max(40, vl.TextLines.Max(tl => tl.WidthIncludingTrailingWhitespace) - textView.HorizontalOffset);
                        var pen = new Pen(Error, 1.4);
                        var g = new StreamGeometry();
                        using (var ctx = g.Open())
                        {
                            ctx.BeginFigure(new Point(6, y), false, false);
                            for (double x = 6; x < w; x += 4) ctx.LineTo(new Point(x + 2, ((int)(x / 4) % 2 == 0) ? y - 2 : y + 1), true, false);
                        }
                        g.Freeze();
                        dc.DrawGeometry(null, pen, g);
                    }
                }
            }
        }
    }
}

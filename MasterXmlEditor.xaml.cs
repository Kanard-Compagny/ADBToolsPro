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

        public MasterVendorBlock Block => _block;

        public MasterXmlEditor(MasterVendorBlock block)
        {
            InitializeComponent();
            Loc.TranslateTree(this);
            _block = block;
            string title = Loc.F("XML du profil · {0}", block.Name);
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
            var def = HighlightingManager.Instance.GetDefinition("XML");
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
            var p = StageNowReader.Read(text);
            if (!p.Ok)
            {
                _marks.ErrorLine = Math.Max(1, p.ErrorLine);
                DotState.SetResourceReference(Shape_Fill, "BadFg");
                TxtState.SetResourceReference(TextBlock.ForegroundProperty, "BadFg");
                TxtState.Text = Loc.F("XML invalide (ligne {0}) · le Master garde la dernière version correcte", p.ErrorLine);
                TxtError.Text = Loc.F("Ligne {0} : {1}", p.ErrorLine, p.Error);
                ErrorBar.Visibility = Visibility.Visible;
            }
            else
            {
                _marks.ErrorLine = 0;
                ErrorBar.Visibility = Visibility.Collapsed;
                DotState.SetResourceReference(Shape_Fill, "OkFg");
                TxtState.SetResourceReference(TextBlock.ForegroundProperty, "OkFg");
                TxtState.Text = Loc.T("XML valide · synchronisé avec le Master");
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

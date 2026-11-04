using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace ADBToolsNew
{
    /// <summary>
    /// Fenêtre de lecture mise en forme (notes de version, licence, composants tiers, aide).
    /// Les fichiers texte sont transformés en titres, paragraphes et listes à puces (voir RenderText) :
    /// on garde des fichiers .txt simples à maintenir, avec un affichage lisible.
    /// </summary>
    public partial class DocumentWindow : Window
    {
        public DocumentWindow()
        {
            InitializeComponent();
            StateChanged += (s, e) =>
            {
                BtnDocMaximize.Content = WindowState == WindowState.Maximized ? "" : "";
                // Fenêtre agrandie : Windows déborde de quelques pixels sur les bords, on les compense.
                RootBorder.BorderThickness = new Thickness(WindowState == WindowState.Maximized ? 7 : 1);
            };
        }

        private void BtnDocMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void BtnDocClose_Click(object sender, RoutedEventArgs e) => Close();

        // ------------------------------------------------------------------ Ouverture

        private static DocumentWindow Create(Window owner, string windowTitle, string heading, string intro)
        {
            var w = new DocumentWindow { Owner = owner, Title = windowTitle + " — ADB Tools" };
            w.TxtWindowTitle.Text = windowTitle;
            if (!string.IsNullOrEmpty(heading)) w.ContentHost.Children.Add(H1(heading));
            if (!string.IsNullOrEmpty(intro)) w.ContentHost.Children.Add(Paragraph(intro, "Ink"));
            return w;
        }

        /// <summary>Affiche un fichier texte (chemin relatif au dossier du programme), mis en forme.</summary>
        public static void ShowTextFile(Window owner, string windowTitle, string heading, string relativePath)
        {
            string fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            string text;
            try { text = File.Exists(fullPath) ? File.ReadAllText(fullPath) : $"Fichier introuvable : {relativePath}"; }
            catch (Exception ex) { text = $"Impossible de lire {relativePath} : {ex.Message}"; }
            ShowText(owner, windowTitle, heading, text);
        }

        public static void ShowText(Window owner, string windowTitle, string heading, string text)
        {
            var w = Create(owner, windowTitle, heading, null);
            foreach (var el in RenderText(text)) w.ContentHost.Children.Add(el);
            w.Show();
        }

        /// <summary>Tableau des composants tiers (composant, version, usage, licence), comme dans Scanalyse.</summary>
        public static void ShowThirdParty(Window owner)
        {
            var w = Create(owner, "Composants tiers et licences", "Composants tiers",
                "ADB Tools s'appuie sur les composants open source suivants. Les outils externes ne sont pas livrés avec le programme : " +
                "ils sont téléchargés depuis leurs sources officielles (Paramètres ▸ Outils tiers). Chacun reste soumis à sa propre licence.");

            string V(ToolComponent c) => ToolsManager.GetInstalledVersion(c) ?? (ToolsManager.IsInstalled(c) ? "?" : "non installé");
            var rows = new List<string[]>
            {
                new[] { "ADB & Fastboot (Platform-Tools)", "https://developer.android.com/tools/releases/platform-tools", V(ToolComponent.PlatformTools), "Communication avec les terminaux Android", "Apache 2.0 (AOSP), conditions du SDK Android" },
                new[] { "scrcpy", "https://github.com/Genymobile/scrcpy", V(ToolComponent.Scrcpy), "Affichage, contrôle et enregistrement de l'écran", "Apache 2.0" },
                new[] { "SDL2, FFmpeg (bibliothèques), libusb", "https://github.com/Genymobile/scrcpy", "fournies avec scrcpy", "Fenêtre et décodage vidéo de scrcpy", "zlib, LGPL 2.1" },
                new[] { "FFmpeg", "https://ffmpeg.org/", V(ToolComponent.FFmpeg), "Filigrane des vidéos (facultatif)", "GPL v3 (build gyan.dev)" },
                new[] { "DocumentFormat.OpenXml", "https://github.com/dotnet/Open-XML-SDK", "2.20.0", "Génération des rapports Word", "MIT" },
                new[] { "LiveCharts", "https://github.com/Live-Charts/Live-Charts", "0.9.7", "Graphiques de l'Analyse Wi-Fi", "MIT" },
                new[] { "System.Text.Json et bibliothèques .NET", "https://github.com/dotnet/runtime", "10.0.12", "Lecture des données (JSON), compatibilité .NET", "MIT" }
            };
            w.ContentHost.Children.Add(BuildTable(new[] { "Composant", "Version", "Usage", "Licence" }, rows));
            w.ContentHost.Children.Add(Paragraph("Le détail complet des licences est dans le fichier Docs\\THIRD_PARTY_NOTICES.txt.", "Muted", new Thickness(0, 14, 0, 0)));
            w.Show();
        }

        /// <summary>Liste des balises des modèles Word, en tableaux (au lieu du fichier texte du dossier Templates).</summary>
        public static void ShowTemplateTags(Window owner)
        {
            var w = Create(owner, "Balises des modèles Word", "Balises des modèles Word",
                "Placez ces balises dans votre modèle Word, là où vous voulez voir chaque information. Écrivez-les en majuscules, " +
                "avec les doubles accolades, d'un seul tenant (sans changer de mise en forme au milieu). Une balise mal écrite reste " +
                "visible dans le rapport, pour la repérer facilement.");
            var host = w.ContentHost;

            host.Children.Add(H2("Créer votre modèle"));
            host.Children.Add(Bullet("Paramètres ▸ Rapports Word ▸ « Gérer le modèle » ▸ « Créer mon modèle à partir du modèle fourni ».", 0));
            host.Children.Add(Bullet("Modifiez-le dans Word ou LibreOffice (mise en page, couleurs, textes) en gardant le format .docx.", 0));
            host.Children.Add(Bullet("Votre logo : déposez logo.png dans le dossier des modèles, il remplace la balise {{LOGO}}.", 0));
            host.Children.Add(Bullet("Pour revenir au modèle fourni : « Gérer le modèle » ▸ « Revenir au modèle fourni ». Votre modèle est rangé dans Templates\\_anciens, rien n'est supprimé.", 0));

            host.Children.Add(H2("Balises communes (les deux rapports)"));
            host.Children.Add(BuildSimpleTable(new[] { "Balise", "Contenu" }, new[] { 1.0, 2.2 }, new List<string[]>
            {
                new[] { "{{LOGO}}", "Votre logo (fichier logo.png du dossier des modèles)" },
                new[] { "{{SOCIETE}}", "Société / client saisi au moment de l'export" },
                new[] { "{{INCIDENT}}", "Numéro d'incident saisi au moment de l'export" },
                new[] { "{{NUMERO_SERIE}}", "Numéro de série du terminal" },
                new[] { "{{MODELE}}", "Modèle du terminal" },
                new[] { "{{DATE}}", "Date et heure du rapport" },
                new[] { "{{RESUME}}", "Résumé du diagnostic" },
                new[] { "{{PIED_DE_PAGE}}", "Mention « Rapport généré par ADB Tools » et la date" }
            }));

            host.Children.Add(H2("Rapport SAV (Diagnostics SAV)"));
            host.Children.Add(BuildSimpleTable(new[] { "Balise", "Contenu" }, new[] { 1.0, 2.2 }, new List<string[]>
            {
                new[] { "{{VERSION_ANDROID}}", "Version d'Android du terminal" },
                new[] { "{{BUILD_OS}}", "Numéro de build du système" },
                new[] { "{{IMAGE_TACTILE}}", "Capture de la grille du test tactile" }
            }));
            host.Children.Add(Paragraph("Pour chaque test, trois balises : informations relevées (_DETAILS), verdict (_STATUT : Conforme, À surveiller, Non conforme… la case du tableau est colorée automatiquement) et commentaire (_COMMENTAIRE).", "Ink", new Thickness(0, 12, 0, 8)));
            var tests = new[] { new[] { "Wi-Fi", "WIFI" }, new[] { "Ping réseau", "PING" }, new[] { "Batterie", "BATTERIE" }, new[] { "Stockage", "STOCKAGE" },
                                new[] { "Capteurs", "CAPTEURS" }, new[] { "Thermique", "THERMIQUE" }, new[] { "Boutons physiques", "BOUTONS" },
                                new[] { "Dalle tactile", "TACTILE" }, new[] { "Scanner", "SCANNER" }, new[] { "Écran", "ECRAN" } };
            host.Children.Add(BuildSimpleTable(new[] { "Test", "Détails", "Statut", "Commentaire" }, new[] { 0.9, 1.2, 1.2, 1.4 },
                tests.Select(t => new[] { t[0], "{{" + t[1] + "_DETAILS}}", "{{" + t[1] + "_STATUT}}", "{{" + t[1] + "_COMMENTAIRE}}" }).ToList(), monoFrom: 1));

            host.Children.Add(H2("Rapport Wi-Fi (Analyse Wi-Fi)"));
            host.Children.Add(BuildSimpleTable(new[] { "Balise", "Contenu" }, new[] { 1.0, 2.2 }, new List<string[]>
            {
                new[] { "{{SSID}}", "Nom du réseau" },
                new[] { "{{BSSID}}", "Point d'accès" },
                new[] { "{{FREQUENCE}}", "Fréquence, bande et canal" },
                new[] { "{{DEBIT}}", "Vitesse de liaison" },
                new[] { "{{MAC}}", "Adresse MAC du terminal" },
                new[] { "{{IP}}", "Adresse IP" },
                new[] { "{{PASSERELLE}}", "Passerelle (routeur)" },
                new[] { "{{DNS}}", "Serveur DNS" },
                new[] { "{{VERDICT}}", "Excellent / Moyen / Critique" },
                new[] { "{{DUREE}}", "Durée de la session" },
                new[] { "{{GRAPHIQUE_PING}}", "Graphique de latence" },
                new[] { "{{STATS_PING}}", "Statistiques de latence et pertes de paquets" },
                new[] { "{{GRAPHIQUE_RSSI}}", "Graphique du signal" },
                new[] { "{{STATS_RSSI}}", "Statistiques du signal" },
                new[] { "{{JOURNAL}}", "Journal brut des événements (police à chasse fixe)" }
            }));
            w.Show();
        }

        // ------------------------------------------------------------------ Mise en forme du texte

        /// <summary>
        /// Transforme un texte simple en éléments mis en forme :
        /// titres entre lignes "=====" ou commençant par "## ", titres numérotés ("2. NOUVEAUTÉS..."),
        /// sous-titres "[Corrections importantes]" ou "### ", puces "- " / "• " (avec lignes de suite indentées),
        /// lignes « Libellé : valeur », et paragraphes (les retours à la ligne de mise en page sont recollés).
        /// </summary>
        public static List<UIElement> RenderText(string text)
        {
            var result = new List<UIElement>();
            var lines = (text ?? "").Replace("\r", "").Split('\n');
            var para = new StringBuilder();
            StringBuilder bullet = null;
            int bulletIndent = 0;
            bool firstHeading = true;

            void FlushPara()
            {
                if (para.Length > 0) { result.Add(Paragraph(para.ToString().Trim(), "Ink")); para.Clear(); }
            }
            void FlushBullet()
            {
                if (bullet != null) { result.Add(Bullet(bullet.ToString().Trim(), bulletIndent)); bullet = null; }
            }
            void Heading(string t)
            {
                FlushPara(); FlushBullet();
                t = t.Trim();
                if (t.Length == 0) return;
                result.Add(firstHeading ? H1(t) : H2(t));
                firstHeading = false;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string line = raw.TrimEnd();
                string trimmed = line.Trim();

                if (Regex.IsMatch(trimmed, @"^[=\-_*]{10,}$")) { FlushPara(); FlushBullet(); continue; } // séparateur

                // Texte encadré par deux lignes ===== : titre (plusieurs lignes possibles)
                bool prevIsBanner = i > 0 && Regex.IsMatch(lines[i - 1].Trim(), @"^={10,}$");
                if (prevIsBanner && trimmed.Length > 0)
                {
                    var title = new List<string> { trimmed };
                    int k = i + 1;
                    while (k < lines.Length && !Regex.IsMatch(lines[k].Trim(), @"^={10,}$") && lines[k].Trim().Length > 0 && k - i < 3)
                    {
                        title.Add(lines[k].Trim());
                        k++;
                    }
                    if (k < lines.Length && Regex.IsMatch(lines[k].Trim(), @"^={10,}$"))
                    {
                        Heading(string.Join(" — ", title));
                        i = k;
                        continue;
                    }
                }

                if (trimmed.Length == 0) { FlushPara(); FlushBullet(); continue; }

                if (trimmed.StartsWith("## ")) { Heading(trimmed.Substring(3)); continue; }
                if (trimmed.StartsWith("### ") || Regex.IsMatch(trimmed, @"^\[[^\]]{3,60}\]$"))
                {
                    FlushPara(); FlushBullet();
                    result.Add(H3(trimmed.StartsWith("### ") ? trimmed.Substring(4) : trimmed.Trim('[', ']')));
                    continue;
                }
                // Titre numéroté en majuscules (« 2. NOUVEAUTÉS DE LA VERSION ») ou ligne entièrement en majuscules
                if (Regex.IsMatch(trimmed, @"^\d+(\.\d+)*\.?\s+[A-ZÀ-Ý0-9 ’'&()/:,\-]{4,}$") ||
                    (trimmed.Length >= 6 && trimmed.Length <= 60 && trimmed == trimmed.ToUpperInvariant() && Regex.IsMatch(trimmed, @"[A-Z]{4,}") && !trimmed.Contains(":")))
                {
                    Heading(trimmed);
                    continue;
                }

                var bm = Regex.Match(line, @"^(\s*)([-•*]|\d+\))\s+(.*)$");
                if (bm.Success)
                {
                    FlushPara(); FlushBullet();
                    bullet = new StringBuilder(bm.Groups[3].Value);
                    bulletIndent = bm.Groups[1].Value.Length >= 4 ? 1 : 0;
                    continue;
                }
                if (bullet != null && raw.StartsWith("  ")) { bullet.Append(' ').Append(trimmed); continue; } // suite de la puce

                // Intertitre isolé et court (« Preamble », « 0. Definitions. ») : entouré de lignes vides
                bool prevEmpty = i == 0 || lines[i - 1].Trim().Length == 0;
                bool nextEmpty = i + 1 >= lines.Length || lines[i + 1].Trim().Length == 0;
                if (prevEmpty && nextEmpty && trimmed.Length <= 45 && !trimmed.EndsWith(":") && para.Length == 0 && bullet == null)
                {
                    result.Add(H3(trimmed));
                    continue;
                }

                // « Libellé : valeur » (libellé court) : une ligne à part, libellé en gras. Seulement si la ligne
                // suivante n'est pas la suite d'une phrase (sinon c'est un paragraphe qui contient un « : »).
                var kv = Regex.Match(trimmed, @"^([^:]{2,24}?)\s+:\s+(.+)$");
                string nextLine = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
                bool nextIsSeparateLine = nextLine.Length == 0 || Regex.IsMatch(nextLine, @"^([^:]{2,24}?)\s+:\s+(.+)$") || Regex.IsMatch(nextLine, @"^[=\-_*]{10,}$");
                if (kv.Success && para.Length == 0 && nextIsSeparateLine)
                {
                    FlushBullet();
                    result.Add(KeyValue(kv.Groups[1].Value.Trim(), kv.Groups[2].Value.Trim()));
                    continue;
                }

                FlushBullet();
                if (para.Length > 0) para.Append(' ');
                para.Append(trimmed);
            }
            FlushPara(); FlushBullet();
            return result;
        }

        // ------------------------------------------------------------------ Briques d'affichage

        private static TextBlock Themed(string text, string brush, double size, FontWeight weight, Thickness margin)
        {
            var tb = new TextBlock { Text = text, FontSize = size, FontWeight = weight, Margin = margin, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.45 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return tb;
        }

        private static TextBlock H1(string t)
        {
            var tb = Themed(t, "Ink", 24, FontWeights.Bold, new Thickness(0, 0, 0, 14));
            tb.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
            return tb;
        }
        private static TextBlock H2(string t) => Themed(t, "BrandText", 17, FontWeights.SemiBold, new Thickness(0, 18, 0, 8));
        private static TextBlock H3(string t) => Themed(t, "Ink", 14, FontWeights.SemiBold, new Thickness(0, 12, 0, 6));
        private static TextBlock Paragraph(string t, string brush, Thickness? margin = null) => Themed(t, brush, 13.5, FontWeights.Normal, margin ?? new Thickness(0, 0, 0, 10));

        private static UIElement Bullet(string t, int level)
        {
            var grid = new Grid { Margin = new Thickness(8 + level * 22, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = Themed(level == 0 ? "•" : "◦", "Muted", 13.5, FontWeights.Normal, new Thickness(0));
            var body = Themed(t, "Ink", 13.5, FontWeights.Normal, new Thickness(0));
            Grid.SetColumn(body, 1);
            grid.Children.Add(dot);
            grid.Children.Add(body);
            return grid;
        }

        private static UIElement KeyValue(string key, string value)
        {
            var tb = new TextBlock { FontSize = 13.5, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            var k = new Run(key + " : ") { FontWeight = FontWeights.SemiBold };
            k.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            tb.Inlines.Add(k);
            tb.Inlines.Add(new Run(value));
            return tb;
        }

        /// <summary>Tableau générique : en-tête sur fond secondaire, lignes séparées par un trait. Les colonnes à
        /// partir de <paramref name="monoFrom"/> (par défaut la première) sont en police à chasse fixe, en vert (balises).</summary>
        private static UIElement BuildSimpleTable(string[] headers, double[] widths, List<string[]> rows, int monoFrom = 0, int monoTo = -1)
        {
            if (monoTo < 0) monoTo = monoFrom == 0 ? 0 : headers.Length - 1;
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var wdt in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wdt, GridUnitType.Star) });

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var headerBg = new Border();
            headerBg.SetResourceReference(Border.BackgroundProperty, "Surface2");
            Grid.SetColumnSpan(headerBg, headers.Length);
            grid.Children.Add(headerBg);
            for (int c = 0; c < headers.Length; c++)
            {
                var h = Themed(headers[c], "Ink", 13, FontWeights.SemiBold, new Thickness(10, 8, 10, 8));
                Grid.SetColumn(h, c);
                grid.Children.Add(h);
            }

            for (int r = 0; r < rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
                line.SetResourceReference(Border.BorderBrushProperty, "Line");
                Grid.SetRow(line, r + 1);
                Grid.SetColumnSpan(line, headers.Length);
                grid.Children.Add(line);

                for (int c = 0; c < headers.Length && c < rows[r].Length; c++)
                {
                    bool mono = c >= monoFrom && c <= monoTo;
                    var cell = Themed(rows[r][c], mono ? "BrandText" : "Ink", mono ? 12.5 : 13, FontWeights.Normal, new Thickness(10, 7, 10, 7));
                    if (mono) cell.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
                    Grid.SetRow(cell, r + 1);
                    Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }
            return grid;
        }

        /// <summary>Tableau simple : en-tête sur fond secondaire, lignes séparées par un trait. Chaque ligne :
        /// { nom, lien (ou null), puis les autres colonnes }.</summary>
        private static UIElement BuildTable(string[] headers, List<string[]> rows)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var headerBg = new Border();
            headerBg.SetResourceReference(Border.BackgroundProperty, "Surface2");
            Grid.SetColumnSpan(headerBg, 4);
            grid.Children.Add(headerBg);
            for (int c = 0; c < headers.Length; c++)
            {
                var h = Themed(headers[c], "Ink", 13, FontWeights.SemiBold, new Thickness(10, 9, 10, 9));
                Grid.SetColumn(h, c);
                grid.Children.Add(h);
            }

            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int gridRow = r + 1;

                var line = new Border { BorderThickness = new Thickness(0, 0, 0, 1) };
                line.SetResourceReference(Border.BorderBrushProperty, "Line");
                Grid.SetRow(line, gridRow);
                Grid.SetColumnSpan(line, 4);
                grid.Children.Add(line);

                // Colonne 1 : nom, en lien vers la source
                var name = new TextBlock { FontSize = 13, Margin = new Thickness(10, 9, 10, 9), TextWrapping = TextWrapping.Wrap };
                if (!string.IsNullOrEmpty(row[1]))
                {
                    var link = new Hyperlink(new Run(row[0])) { NavigateUri = new Uri(row[1]), TextDecorations = null, Cursor = Cursors.Hand };
                    link.SetResourceReference(TextElement.ForegroundProperty, "BrandText");
                    link.RequestNavigate += (s, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                    };
                    name.Inlines.Add(link);
                }
                else
                {
                    name.Text = row[0];
                    name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                }
                Grid.SetRow(name, gridRow);
                grid.Children.Add(name);

                for (int c = 2; c < row.Length && c - 1 < 4; c++)
                {
                    var cell = Themed(row[c], "Ink", 13, FontWeights.Normal, new Thickness(10, 9, 10, 9));
                    Grid.SetRow(cell, gridRow);
                    Grid.SetColumn(cell, c - 1);
                    grid.Children.Add(cell);
                }
            }
            return grid;
        }
    }
}

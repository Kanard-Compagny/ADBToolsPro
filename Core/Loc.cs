using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Langue de l'interface (français par défaut, anglais en option), comme Scanalyse : le choix s'applique au
    /// redémarrage. Le texte français reste la référence dans le code et le XAML ; la traduction anglaise est
    /// un dictionnaire « français → anglais » (Lang\en.json, intégré au programme).
    /// - Interface XAML : <see cref="TranslateTree"/> parcourt chaque fenêtre au chargement.
    /// - Messages du code : <see cref="T"/> et <see cref="F"/>.
    /// Un texte sans traduction reste en français ; en anglais, il est noté dans
    /// %AppData%\ADBToolsPro\traductions_manquantes.txt pour être complété.
    /// </summary>
    public static class Loc
    {
        private static Dictionary<string, string> _en;
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static readonly Regex LooksFrench = new Regex(@"[a-zà-ÿ]{3,}", RegexOptions.Compiled);

        /// <summary>Langue en cours : "fr" ou "en" (lue dans les réglages au démarrage).</summary>
        public static string Language { get; private set; } = "fr";
        public static bool IsEnglish => Language == "en";

        /// <summary>À appeler une fois au démarrage, avant d'ouvrir la première fenêtre.</summary>
        public static void Init()
        {
            Language = AppSettings.Current.Language == "en" ? "en" : "fr";
            if (!IsEnglish) return;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ADBToolsNew.Lang.en.json"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                    _en = JsonSerializer.Deserialize<Dictionary<string, string>>(r.ReadToEnd());
            }
            catch
            {
                _en = null;      // dictionnaire illisible : on reste en français plutôt que de planter
                Language = "fr";
            }
        }

        /// <summary>Traduit un texte français (inchangé en français ou s'il n'a pas de traduction).</summary>
        public static string T(string fr)
        {
            if (!IsEnglish || string.IsNullOrEmpty(fr) || _en == null) return fr;
            if (_en.TryGetValue(fr, out var en)) return en;
            // Espaces en début / fin conservés : « ⏭️ Erreur suivante » est la même clé que « ⏭️ Erreur suivante ».
            string trimmed = fr.Trim();
            if (trimmed.Length != fr.Length && _en.TryGetValue(trimmed, out en))
                return fr.Substring(0, fr.IndexOf(trimmed, StringComparison.Ordinal)) + en + fr.Substring(fr.IndexOf(trimmed, StringComparison.Ordinal) + trimmed.Length);
            NoteMissing(fr);
            return fr;
        }

        /// <summary>Traduit un modèle français puis le remplit : F("Rapport généré : {0}", chemin).</summary>
        public static string F(string frFormat, params object[] args)
        {
            string format = T(frFormat);
            try { return string.Format(format, args); }
            catch (FormatException) { return string.Format(frFormat, args); }
        }

        private static HashSet<string> _englishValues;

        private static void NoteMissing(string fr)
        {
            if (!LooksFrench.IsMatch(fr)) return;
            // Texte déjà traduit (traduit une seconde fois en passant par une boîte de dialogue, par exemple).
            if (_englishValues == null && _en != null) _englishValues = new HashSet<string>(_en.Values);
            if (_englishValues != null && _englishValues.Contains(fr)) return;
            lock (Missing)
            {
                if (!Missing.Add(fr)) return;
                try { File.AppendAllText(Path.Combine(AppSettings.DataFolder, "traductions_manquantes.txt"), fr.Replace("\n", "\\n") + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
        }

        // ------------------------------------------------------------------ Interface

        /// <summary>
        /// Traduit tous les textes d'une fenêtre (TextBlock, contenus et en-têtes, info-bulles, textes fantômes
        /// des champs de saisie, titre). Les textes liés par Binding ne sont pas touchés.
        /// </summary>
        public static void TranslateTree(DependencyObject root)
        {
            if (!IsEnglish || root == null) return;
            var seen = new HashSet<DependencyObject>();
            Walk(root, seen);
        }

        private static void Walk(DependencyObject node, HashSet<DependencyObject> seen)
        {
            if (node == null || !seen.Add(node)) return;

            switch (node)
            {
                case Window w:
                    if (!string.IsNullOrEmpty(w.Title)) w.Title = T(w.Title);
                    break;
                case TextBlock tb when !BindingOperations.IsDataBound(tb, TextBlock.TextProperty):
                    TranslateInlines(tb.Inlines);
                    break;
                case TextBox box:
                    if (box.Tag is string tag) box.Tag = T(tag);
                    if (!BindingOperations.IsDataBound(box, TextBox.TextProperty) && box.IsReadOnly && !string.IsNullOrEmpty(box.Text))
                        box.Text = T(box.Text);
                    break;
            }

            if (node is HeaderedContentControl hcc && hcc.Header is string h && !BindingOperations.IsDataBound(hcc, HeaderedContentControl.HeaderProperty))
                hcc.Header = T(h);
            if (node is ContentControl cc && !(node is Window) && cc.Content is string c && !BindingOperations.IsDataBound(cc, ContentControl.ContentProperty))
            {
                // Élément de liste : la valeur française d'origine reste lisible dans Tag (filtres qui comparent
                // le texte choisi aux données, ex. catégories de la bibliothèque).
                if (node is ComboBoxItem cbi && cbi.Tag == null) cbi.Tag = c;
                cc.Content = T(c);
            }
            if (node is FrameworkElement fe && fe.ToolTip is string tip && !BindingOperations.IsDataBound(fe, FrameworkElement.ToolTipProperty))
                fe.ToolTip = T(tip);

            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().ToList())
                Walk(child, seen);
        }

        private static void TranslateInlines(InlineCollection inlines)
        {
            foreach (var inline in inlines.ToList())
            {
                switch (inline)
                {
                    case Run run when !BindingOperations.IsDataBound(run, Run.TextProperty):
                        run.Text = T(run.Text);
                        break;
                    case Span span:
                        TranslateInlines(span.Inlines);
                        break;
                }
            }
        }
    }
}

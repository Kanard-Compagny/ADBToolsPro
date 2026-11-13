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
        // OVERLAY DIALOGUE PERSONNALISÉ (CUSTOM MSGBOX)
        // ==========================================
        private TaskCompletionSource<bool> _dialogTcs;
        /// <summary>Propositions pour le prochain dialogue à champ (consommées par ShowCustomDialogAsync), et leur oubli.</summary>
        private List<string> _nextDialogSuggestions;
        private Action<string> _nextDialogForget;

        /// <summary>Propose des valeurs sous le champ du prochain dialogue ; forget (facultatif) ajoute un ✕ pour en oublier une.</summary>
        private void SetNextDialogSuggestions(IEnumerable<string> values, Action<string> forget = null)
        {
            _nextDialogSuggestions = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
            _nextDialogForget = forget;
        }

        private void RenderDialogSuggestions(List<string> values, Action<string> forget)
        {
            DialogSuggestions.Children.Clear();
            DialogSuggestionsPanel.Visibility = values != null && values.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (values == null) return;
            foreach (var v in values)
            {
                string value = v;
                var chip = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 6) };
                chip.SetResourceReference(Border.BackgroundProperty, "Surface2");
                chip.SetResourceReference(Border.BorderBrushProperty, "Line");
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var pick = new Button { Content = value, Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(9, 4, forget != null ? 4 : 9, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 12, ToolTip = Loc.T("Utiliser ce dossier") };
                pick.SetResourceReference(Control.ForegroundProperty, "Ink");
                pick.Template = FlatButtonTemplate();
                pick.Click += (s, e) => { TxtDialogInput.Text = value; TxtDialogInput.Focus(); TxtDialogInput.CaretIndex = value.Length; };
                row.Children.Add(pick);
                if (forget != null)
                {
                    var x = new Button { Content = "✕", Cursor = System.Windows.Input.Cursors.Hand, Padding = new Thickness(4, 4, 8, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 10, ToolTip = Loc.T("Oublier ce dossier") };
                    x.SetResourceReference(Control.ForegroundProperty, "Muted");
                    x.Template = FlatButtonTemplate();
                    x.Click += (s, e) =>
                    {
                        forget(value);
                        values.Remove(value);
                        RenderDialogSuggestions(values, forget);
                    };
                    row.Children.Add(x);
                }
                chip.Child = row;
                DialogSuggestions.Children.Add(chip);
            }
        }

        private static ControlTemplate FlatButtonTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;
            return t;
        }

        private Task<bool> ShowCustomDialogAsync(string title, string message, string confirmText = "Confirmer", string cancelText = "Annuler", bool isDanger = true, bool requiresInput = false, string defaultInput = "")
        {
            DialogTitle.Text = Loc.T(title);
            DialogMessage.Text = Loc.T(message);
            BtnDialogConfirm.Content = Loc.T(confirmText);

            // --- NOUVELLE GESTION DU BOUTON ANNULER ---
            if (string.IsNullOrEmpty(cancelText))
            {
                BtnDialogCancel.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnDialogCancel.Visibility = Visibility.Visible;
                BtnDialogCancel.Content = Loc.T(cancelText);
            }
            // ------------------------------------------

            // Applique le bouton Rouge ou Bleu en fonction de l'action
            BtnDialogConfirm.Style = (Style)FindResource(isDanger ? "DangerButtonStyle" : "PrimaryButtonStyle");

            // Propositions sous le champ : seulement pour ce dialogue-ci.
            RenderDialogSuggestions(requiresInput ? _nextDialogSuggestions : null, _nextDialogForget);
            _nextDialogSuggestions = null;
            _nextDialogForget = null;

            // Gère l'affichage du champ de texte
            if (requiresInput)
            {
                TxtDialogInput.Visibility = Visibility.Visible;
                TxtDialogInput.Text = defaultInput;
                TxtDialogInput.Focus();
                TxtDialogInput.SelectAll(); // Sélectionne le texte pour le remplacer direct
            }
            else
            {
                TxtDialogInput.Visibility = Visibility.Collapsed;
            }

            OverlayDialog.Visibility = Visibility.Visible;

            // Un seul overlay pour tous les dialogues : si un dialogue précédent attendait encore une réponse,
            // il est considéré comme annulé. Sans ça, son TaskCompletionSource était écrasé et le code qui
            // l'attendait ("await ShowCustomDialogAsync(...)") restait bloqué indéfiniment.
            // RunContinuationsAsynchronously : la suite du code appelant ne s'exécute pas au milieu de ce
            // dialogue-ci, mais après, via le Dispatcher.
            var previous = _dialogTcs;
            _dialogTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            previous?.TrySetResult(false);
            return _dialogTcs.Task;
        }
        private void BtnDialogConfirm_Click(object sender, RoutedEventArgs e)
        {
            OverlayDialog.Visibility = Visibility.Collapsed;
            _dialogTcs?.TrySetResult(true);
        }

        private void BtnDialogCancel_Click(object sender, RoutedEventArgs e)
        {
            OverlayDialog.Visibility = Visibility.Collapsed;
            _dialogTcs?.TrySetResult(false);
        }

        // --- COPIE DU PACKAGE / ACTIVITÉ DANS LE PRESSE-PAPIER ---
        private void BtnCopySniffer_Click(object sender, RoutedEventArgs e)
        {
            string textToCopy = TxtForegroundPackage?.Text?.Trim() ?? "";

            if (!string.IsNullOrEmpty(textToCopy) && textToCopy != "En attente de connexion..." && textToCopy != "Sniffer désactivé.")
            {
                Clipboard.SetText(textToCopy);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📋 Copié dans le presse-papier : {1}", DateTime.Now, textToCopy));
                if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();
            }
        }
    }
}

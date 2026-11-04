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

        private Task<bool> ShowCustomDialogAsync(string title, string message, string confirmText = "Confirmer", string cancelText = "Annuler", bool isDanger = true, bool requiresInput = false, string defaultInput = "")
        {
            DialogTitle.Text = title;
            DialogMessage.Text = message;
            BtnDialogConfirm.Content = confirmText;

            // --- NOUVELLE GESTION DU BOUTON ANNULER ---
            if (string.IsNullOrEmpty(cancelText))
            {
                BtnDialogCancel.Visibility = Visibility.Collapsed;
            }
            else
            {
                BtnDialogCancel.Visibility = Visibility.Visible;
                BtnDialogCancel.Content = cancelText;
            }
            // ------------------------------------------

            // Applique le bouton Rouge ou Bleu en fonction de l'action
            BtnDialogConfirm.Style = (Style)FindResource(isDanger ? "DangerButtonStyle" : "PrimaryButtonStyle");

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
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📋 Copié dans le presse-papier : {textToCopy}");
                if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();
            }
        }
    }
}

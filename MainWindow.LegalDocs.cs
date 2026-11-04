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
        // === VISIONNEUSE DE DOCUMENT (CGU / Notes de version) ===
        // ==========================================

        private TaskCompletionSource<bool> _documentViewerTcs;

        // Suivi en mémoire de la date de CGU acceptée pour la session en cours, réutilisé par
        // Window_Closing lors de la sauvegarde des préférences (qui reconstruit l'objet UserPrefs en
        // partant de zéro à chaque fermeture, donc ne doit jamais oublier ce champ).
        private string _acceptedCguDate = "";

        private const string CguRelativePath = "Docs\\CGU.txt";
        private const string ReleaseNotesRelativePath = "Docs\\Release_Note_V1.txt";

        /// <summary>
        /// Affiche le contenu d'un fichier texte dans l'overlay DocumentViewerOverlay.
        /// En mode <paramref name="requireAcceptance"/>, propose "J'accepte"/"Refuser" et retourne le choix
        /// de l'utilisateur ; sinon un simple bouton "Fermer" est proposé et la méthode retourne toujours true.
        /// </summary>
        private async Task<bool> ShowDocumentViewerAsync(string title, string relativeFilePath, bool requireAcceptance)
        {
            string fullPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativeFilePath);
            string content;
            try
            {
                content = File.Exists(fullPath) ? File.ReadAllText(fullPath) : $"Fichier introuvable : {relativeFilePath}";
            }
            catch (Exception ex)
            {
                content = $"Impossible de lire le fichier {relativeFilePath} : {ex.Message}";
            }

            TxtDocumentViewerTitle.Text = title;
            TxtDocumentViewerContent.Text = content;

            DocumentViewerAcceptButtons.Visibility = requireAcceptance ? Visibility.Visible : Visibility.Collapsed;
            DocumentViewerCloseButtons.Visibility = requireAcceptance ? Visibility.Collapsed : Visibility.Visible;

            DocumentViewerOverlay.Visibility = Visibility.Visible;

            _documentViewerTcs = new TaskCompletionSource<bool>();
            return await _documentViewerTcs.Task;
        }

        private void BtnDocumentViewerAccept_Click(object sender, RoutedEventArgs e)
        {
            DocumentViewerOverlay.Visibility = Visibility.Collapsed;
            _documentViewerTcs?.TrySetResult(true);
        }

        private void BtnDocumentViewerDecline_Click(object sender, RoutedEventArgs e)
        {
            DocumentViewerOverlay.Visibility = Visibility.Collapsed;
            _documentViewerTcs?.TrySetResult(false);
        }

        private void BtnDocumentViewerClose_Click(object sender, RoutedEventArgs e)
        {
            DocumentViewerOverlay.Visibility = Visibility.Collapsed;
            _documentViewerTcs?.TrySetResult(true);
        }

        // --- ACCÈS À LA DEMANDE (pied de la sidebar) ---
        private void BtnShowReleaseNotes_Click(object sender, RoutedEventArgs e)
            => DocumentWindow.ShowTextFile(this, "Quoi de neuf", null, ReleaseNotesRelativePath);


        // --- VALIDATION OBLIGATOIRE AU PREMIER LANCEMENT ---

        /// <summary>Extrait la ligne "Dernière mise à jour : JJ/MM/AAAA" du CGU, pour savoir si la version
        /// actuellement acceptée par l'utilisateur (stockée dans prefs.xml) correspond bien à ce fichier.</summary>
        private string GetCguVersionDate()
        {
            string fullPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CguRelativePath);
            if (!File.Exists(fullPath)) return null;

            try
            {
                string content = File.ReadAllText(fullPath);
                var match = Regex.Match(content, @"Dernière mise à jour\s*:\s*(\d{2}/\d{2}/\d{4})");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Affiche le CGU en mode acceptation obligatoire si nécessaire (jamais accepté, ou CGU mis à jour
        /// depuis la dernière acceptation). Ferme l'application si l'utilisateur refuse. Sauvegarde
        /// l'acceptation immédiatement (pas seulement à la fermeture normale de l'app) pour qu'elle survive
        /// même si l'application est fermée brutalement avant sa prochaine fermeture propre.
        /// </summary>
        /// <returns>false si l'utilisateur a refusé (l'application est alors en cours de fermeture).</returns>
        private async Task<bool> EnsureCguAcceptedAsync(string currentAcceptedDate)
        {
            string cguVersionDate = GetCguVersionDate();
            if (cguVersionDate == null) return true; // Fichier CGU absent : rien à bloquer, on ne pénalise pas l'utilisateur pour un fichier manquant.

            if (currentAcceptedDate == cguVersionDate)
            {
                _acceptedCguDate = cguVersionDate; // Déjà accepté pour cette version précise.
                return true;
            }

            bool accepted = await ShowDocumentViewerAsync("📜 Conditions Générales d'Utilisation — Merci de valider avant de continuer", CguRelativePath, requireAcceptance: true);

            if (!accepted)
            {
                Application.Current.Shutdown();
                return false;
            }

            _acceptedCguDate = cguVersionDate;
            SaveCguAcceptance(cguVersionDate);
            return true;
        }

        /// <summary>Sauvegarde uniquement le champ AcceptedCguDate dans prefs.xml, sans toucher aux autres
        /// préférences (taille de fenêtre, historique...), qui ne sont finalisées qu'à la fermeture.</summary>
        private void SaveCguAcceptance(string cguVersionDate)
        {
            try
            {
                string prefsPath = GetPrefsPath();
                UserPrefs prefs = new UserPrefs();

                if (File.Exists(prefsPath))
                {
                    try
                    {
                        var xs = new XmlSerializer(typeof(UserPrefs));
                        using (var fs = new FileStream(prefsPath, FileMode.Open))
                        {
                            prefs = (UserPrefs)xs.Deserialize(fs) ?? new UserPrefs();
                        }
                    }
                    catch { prefs = new UserPrefs(); }
                }

                prefs.AcceptedCguDate = cguVersionDate;

                var xsOut = new XmlSerializer(typeof(UserPrefs));
                using (var fs = new FileStream(prefsPath, FileMode.Create))
                {
                    xsOut.Serialize(fs, prefs);
                }
            }
            catch { /* Non bloquant : au pire, le CGU sera redemandé au prochain lancement. */ }
        }
    }
}

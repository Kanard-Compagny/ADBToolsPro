using ADBToolsNew.Core;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ADBToolsNew
{
    /// <summary>
    /// Bouton « Modèle » à côté des exports Word (Diagnostic SAV et Analyse Wi-Fi) : chacun peut utiliser le
    /// modèle neutre fourni, en modifier une copie, ou importer le sien. Voir Core\ReportTemplates.cs.
    /// </summary>
    public partial class MainWindow
    {
        private void BtnReportTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button)) return;
            string tag = button.Tag as string;
            var kind = tag == "Wifi" ? ReportKind.Wifi : tag == "Logs" ? ReportKind.Logs : ReportKind.Sav;
            ShowReportTemplateMenu(button, kind);
        }

        /// <summary>Analyse de fichiers ▸ Exporter ▸ « Modèle Word » : même menu que dans les Paramètres.</summary>
        private void MenuFaReportTemplate_Click(object sender, RoutedEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() => ShowReportTemplateMenu(BtnFaExport, ReportKind.Logs)), System.Windows.Threading.DispatcherPriority.Background);

        private void ShowReportTemplateMenu(FrameworkElement target, ReportKind kind)
        {
            bool hasUser = ReportTemplates.HasUserTemplate(kind);

            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };

            menu.Items.Add(new MenuItem
            {
                Header = hasUser ? Loc.T("Modèle utilisé : le vôtre (dossier Templates)") : Loc.T("Modèle utilisé : modèle fourni (neutre)"),
                IsEnabled = false
            });
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem(Loc.T("Ouvrir le modèle utilisé dans Word"), () => OpenWithShell(ReportTemplates.GetTemplatePath(kind))));
            menu.Items.Add(MakeItem(hasUser ? Loc.T("Modifier mon modèle dans Word") : Loc.T("Créer mon modèle à partir du modèle fourni"), () =>
            {
                string path = ReportTemplates.CopyDefaultForEditing(kind);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📝 Modèle {1} prêt à être modifié : {2}", DateTime.Now, ReportTemplates.DisplayName(kind), path));
                UpdateReportTemplateStates();
                OpenWithShell(path);
            }));
            menu.Items.Add(MakeItem(Loc.T("Importer mon propre modèle (.docx)…"), () => ImportReportTemplate(kind)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem(Loc.T("Ouvrir le dossier des modèles (logo, balises…)"), () =>
            {
                ReportTemplates.EnsureUserFolder();
                OpenFolder(ReportTemplates.UserFolder);
            }));
            menu.Items.Add(MakeItem(Loc.T("Liste des balises disponibles"), () =>
            {
                ReportTemplates.EnsureUserFolder();
                DocumentWindow.ShowTemplateTags(this);
            }));
            var reset = MakeItem(Loc.T("Revenir au modèle fourni"), async () =>
            {
                bool ok = await ShowCustomDialogAsync(Loc.T("🔄 Modèle par défaut"),
                    Loc.T("Votre modèle sera rangé dans Templates\\_anciens (rien n'est supprimé) et le modèle fourni sera de nouveau utilisé."),
                    Loc.T("Revenir au modèle fourni"), Loc.T("Annuler"), false);
                if (!ok) return;
                ReportTemplates.ResetToDefault(kind);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔄 {1} : retour au modèle fourni.", DateTime.Now, ReportTemplates.DisplayName(kind)));
                UpdateReportTemplateStates();
            });
            reset.IsEnabled = hasUser;
            menu.Items.Add(reset);

            menu.IsOpen = true;
        }

        private static MenuItem MakeItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            return item;
        }

        private async void ImportReportTemplate(ReportKind kind)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.F("Choisir votre modèle Word — {0}", ReportTemplates.DisplayName(kind)),
                Filter = Loc.T("Document Word (*.docx)|*.docx"),
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                string dest = ReportTemplates.Import(kind, dlg.FileName);
                bool hasTags = false;
                try
                {
                    using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(dest, false))
                        hasTags = ReportTemplateEngine.UsesTags(doc);
                }
                catch { /* fichier illisible : signalé ci-dessous */ }

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Modèle {1} importé : {2}", DateTime.Now, ReportTemplates.DisplayName(kind), dest));
                UpdateReportTemplateStates();
                if (!hasTags)
                {
                    await ShowCustomDialogAsync(Loc.T("⚠️ Aucune balise trouvée"),
                        Loc.T("Ce modèle ne contient aucune balise comme {{SOCIETE}} ou {{NUMERO_SERIE}} : les informations ne pourront pas y être placées.\n\n") +
                        Loc.T("Ouvrez la « Liste des balises disponibles » depuis le bouton Modèle, ajoutez-les dans votre document, puis réimportez-le."),
                        "OK", null, false);
                }
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("❌ Import impossible"), ex.Message, "OK", null, false);
            }
        }
    }
}

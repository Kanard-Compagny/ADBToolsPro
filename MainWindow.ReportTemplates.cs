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
            var kind = (button.Tag as string) == "Wifi" ? ReportKind.Wifi : ReportKind.Sav;
            bool hasUser = ReportTemplates.HasUserTemplate(kind);

            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };

            menu.Items.Add(new MenuItem
            {
                Header = hasUser ? "Modèle utilisé : le vôtre (dossier Templates)" : "Modèle utilisé : modèle fourni (neutre)",
                IsEnabled = false
            });
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("Ouvrir le modèle utilisé dans Word", () => OpenWithShell(ReportTemplates.GetTemplatePath(kind))));
            menu.Items.Add(MakeItem(hasUser ? "Modifier mon modèle dans Word" : "Créer mon modèle à partir du modèle fourni", () =>
            {
                string path = ReportTemplates.CopyDefaultForEditing(kind);
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📝 Modèle {ReportTemplates.DisplayName(kind)} prêt à être modifié : {path}");
                UpdateReportTemplateStates();
                OpenWithShell(path);
            }));
            menu.Items.Add(MakeItem("Importer mon propre modèle (.docx)…", () => ImportReportTemplate(kind)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("Ouvrir le dossier des modèles (logo, balises…)", () =>
            {
                ReportTemplates.EnsureUserFolder();
                OpenFolder(ReportTemplates.UserFolder);
            }));
            menu.Items.Add(MakeItem("Liste des balises disponibles", () =>
            {
                ReportTemplates.EnsureUserFolder();
                DocumentWindow.ShowTemplateTags(this);
            }));
            var reset = MakeItem("Revenir au modèle fourni", async () =>
            {
                bool ok = await ShowCustomDialogAsync("🔄 Modèle par défaut",
                    "Votre modèle sera rangé dans Templates\\_anciens (rien n'est supprimé) et le modèle fourni sera de nouveau utilisé.",
                    "Revenir au modèle fourni", "Annuler", false);
                if (!ok) return;
                ReportTemplates.ResetToDefault(kind);
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🔄 {ReportTemplates.DisplayName(kind)} : retour au modèle fourni.");
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
                Title = $"Choisir votre modèle Word — {ReportTemplates.DisplayName(kind)}",
                Filter = "Document Word (*.docx)|*.docx",
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

                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ✅ Modèle {ReportTemplates.DisplayName(kind)} importé : {dest}");
                UpdateReportTemplateStates();
                if (!hasTags)
                {
                    await ShowCustomDialogAsync("⚠️ Aucune balise trouvée",
                        "Ce modèle ne contient aucune balise comme {{SOCIETE}} ou {{NUMERO_SERIE}} : les informations ne pourront pas y être placées.\n\n" +
                        "Ouvrez la « Liste des balises disponibles » depuis le bouton Modèle, ajoutez-les dans votre document, puis réimportez-le.",
                        "OK", null, false);
                }
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync("❌ Import impossible", ex.Message, "OK", null, false);
            }
        }
    }
}

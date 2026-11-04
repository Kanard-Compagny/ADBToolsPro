using System.Windows;

namespace ADBToolsNew
{
    public partial class MainWindow : Window
    {
        // Notes de version : affichées dans une fenêtre mise en forme (DocumentWindow).
        // (L'ancienne visionneuse et la validation obligatoire des CGU ont été retirées : ADB Tools est un
        //  logiciel libre sous licence GPL-3.0, il n'y a rien à accepter pour l'utiliser.)
        private const string ReleaseNotesRelativePath = "Docs\\Release_Note_V1.txt";

        private void BtnShowReleaseNotes_Click(object sender, RoutedEventArgs e)
            => DocumentWindow.ShowTextFile(this, "Quoi de neuf", null, ReleaseNotesRelativePath);
    }
}

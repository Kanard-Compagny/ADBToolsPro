using System;
using System.IO;
using System.Text;

namespace ADBToolsNew.Core
{
    public enum ReportKind { Sav, Wifi }

    /// <summary>
    /// Où trouver les modèles de rapport Word.
    /// - Modèles fournis (neutres, sans logo) : Docs\Modeles\Rapport_SAV.docx et Rapport_Wifi.docx.
    /// - Modèles personnels : dossier Templates\ à côté de l'exe (programme portable). Un fichier présent
    ///   ici remplace le modèle fourni ; le supprimer (ou « Revenir au modèle par défaut ») le désactive.
    /// - Logo : Templates\logo.png (ou .jpg) est inséré à la place de la balise {{LOGO}}.
    /// </summary>
    public static class ReportTemplates
    {
        public static string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        public static string DefaultFolder => Path.Combine(BaseDir, "Docs", "Modeles");
        public static string UserFolder => Path.Combine(BaseDir, "Templates");

        public static string FileName(ReportKind kind) => kind == ReportKind.Sav ? "Rapport_SAV.docx" : "Rapport_Wifi.docx";
        public static string DisplayName(ReportKind kind) => kind == ReportKind.Sav ? "Rapport SAV" : "Rapport Wi-Fi";

        public static string UserTemplatePath(ReportKind kind) => Path.Combine(UserFolder, FileName(kind));
        public static string DefaultTemplatePath(ReportKind kind) => Path.Combine(DefaultFolder, FileName(kind));

        public static bool HasUserTemplate(ReportKind kind) => File.Exists(UserTemplatePath(kind));

        /// <summary>Modèle à utiliser : le modèle personnel s'il existe, sinon le modèle fourni.</summary>
        public static string GetTemplatePath(ReportKind kind)
        {
            string user = UserTemplatePath(kind);
            return File.Exists(user) ? user : DefaultTemplatePath(kind);
        }

        /// <summary>Logo personnel (Templates\logo.png / logo.jpg), ou null s'il n'y en a pas.</summary>
        public static string GetLogoPath()
        {
            foreach (var name in LogoNames)
            {
                string p = Path.Combine(UserFolder, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static readonly string[] LogoNames = { "logo.png", "logo.jpg", "logo.jpeg" };

        /// <summary>Installe l'image choisie comme logo des rapports (copiée dans Templates\logo.png/.jpg).</summary>
        public static string SetLogo(string sourcePath)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Image introuvable.", sourcePath);
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                throw new InvalidOperationException("Le logo doit être une image PNG ou JPEG.");
            EnsureUserFolder();
            string dest = Path.Combine(UserFolder, "logo" + (ext == ".jpeg" ? ".jpg" : ext));
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return dest;
            string tmp = dest + ".nouveau";
            File.Copy(sourcePath, tmp, true);
            ClearLogo();
            File.Move(tmp, dest);
            return dest;
        }

        /// <summary>Retire le logo des rapports.</summary>
        public static void ClearLogo()
        {
            foreach (var name in LogoNames)
            {
                string p = Path.Combine(UserFolder, name);
                if (File.Exists(p)) File.Delete(p);
            }
        }

        /// <summary>Copie un .docx choisi par l'utilisateur comme modèle personnel. L'ancien modèle personnel
        /// éventuel est mis de côté (jamais supprimé).</summary>
        public static string Import(ReportKind kind, string sourcePath)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Fichier introuvable.", sourcePath);
            if (!string.Equals(Path.GetExtension(sourcePath), ".docx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le modèle doit être un document Word au format .docx.");

            EnsureUserFolder();
            string dest = UserTemplatePath(kind);
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return dest;
            ArchiveUserTemplate(kind);
            File.Copy(sourcePath, dest, false);
            return dest;
        }

        /// <summary>Désactive le modèle personnel : il est déplacé dans Templates\_anciens (récupérable).</summary>
        public static bool ResetToDefault(ReportKind kind)
        {
            return ArchiveUserTemplate(kind);
        }

        /// <summary>Copie le modèle fourni dans Templates\ pour que l'utilisateur le modifie dans Word.</summary>
        public static string CopyDefaultForEditing(ReportKind kind)
        {
            EnsureUserFolder();
            string dest = UserTemplatePath(kind);
            if (!File.Exists(dest)) File.Copy(DefaultTemplatePath(kind), dest, false);
            return dest;
        }

        private static bool ArchiveUserTemplate(ReportKind kind)
        {
            string current = UserTemplatePath(kind);
            if (!File.Exists(current)) return false;
            string archive = Path.Combine(UserFolder, "_anciens");
            Directory.CreateDirectory(archive);
            string name = Path.GetFileNameWithoutExtension(current) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".docx";
            File.Move(current, Path.Combine(archive, name));
            return true;
        }

        /// <summary>Crée le dossier Templates\ avec un fichier d'explication des balises.</summary>
        public static void EnsureUserFolder()
        {
            Directory.CreateDirectory(UserFolder);
            string help = Path.Combine(UserFolder, "LISEZ-MOI - Balises des modeles.txt");
            File.WriteAllText(help, TagsHelpText, new UTF8Encoding(true));
        }

        public const string TagsHelpText =
@"MODÈLES DE RAPPORT WORD — ADB TOOLS
===================================

Ce dossier contient VOS modèles de rapport. S'il est vide, ADB Tools utilise ses
modèles neutres fournis (dossier Docs\Modeles).

  Rapport_SAV.docx   → rapport du Diagnostic SAV
  Rapport_Wifi.docx  → rapport de l'Analyse Wi-Fi
  logo.png           → (facultatif) votre logo, inséré à la place de {{LOGO}}

Pour créer votre modèle : dans ADB Tools, bouton « Modèle » à côté de l'export
Word → « Modifier une copie du modèle fourni », puis modifiez-le dans Word.
Vous pouvez tout changer (mise en page, couleurs, textes) : il suffit de placer
les balises ci-dessous là où vous voulez voir les informations. Écrivez-les en
MAJUSCULES, avec les doubles accolades, d'un seul tenant (sans mise en forme au
milieu). Une balise mal écrite reste visible dans le rapport, pour la repérer.
Une ligne qui ne contient qu'une balise sans valeur disparaît du rapport.

BALISES COMMUNES (les deux rapports)
  {{LOGO}}            votre logo (Paramètres ▸ Rapports Word ▸ En-tête)
  {{MA_SOCIETE}}      votre société (Paramètres ▸ Rapports Word ▸ En-tête)
  {{COORDONNEES}}     vos coordonnées (plusieurs lignes possibles)
  {{TECHNICIEN}}      votre nom
  {{SOCIETE}}         société / client saisi à l'export
  {{INCIDENT}}        numéro d'incident saisi à l'export
  {{NUMERO_SERIE}}    numéro de série du terminal
  {{MODELE}}          modèle du terminal
  {{DATE}}            date et heure du rapport
  {{RESUME}}          résumé du diagnostic
  {{PIED_DE_PAGE}}    mention « Rapport généré par ADB Tools »

RAPPORT SAV
  {{VERSION_ANDROID}}  {{BUILD_OS}}
  {{IMAGE_TACTILE}}    capture de la grille du test tactile

  Pour chaque test, trois balises : _DETAILS (informations relevées),
  _STATUT (Conforme / À surveiller / Non conforme..., la case du tableau
  est colorée automatiquement) et _COMMENTAIRE.
  Tests automatiques : WIFI, PING, BATTERIE, STOCKAGE, CAPTEURS, THERMIQUE
  Tests manuels      : BOUTONS, TACTILE, SCANNER, ECRAN
  Exemple : {{BATTERIE_DETAILS}}  {{BATTERIE_STATUT}}  {{BATTERIE_COMMENTAIRE}}

RAPPORT WI-FI
  {{SSID}}  {{BSSID}}  {{FREQUENCE}}  {{DEBIT}}  {{MAC}}  {{IP}}
  {{PASSERELLE}}  {{DNS}}
  {{VERDICT}}          Excellent / Moyen / Critique
  {{DUREE}}            durée de la session
  {{GRAPHIQUE_PING}}   graphique de latence     {{STATS_PING}}
  {{GRAPHIQUE_RSSI}}   graphique du signal      {{STATS_RSSI}}
  {{JOURNAL}}          journal brut des événements (police à chasse fixe)

Revenir au modèle fourni : bouton « Modèle » → « Revenir au modèle par défaut ».
Votre modèle est alors rangé dans le sous-dossier _anciens (rien n'est supprimé).
";
    }
}

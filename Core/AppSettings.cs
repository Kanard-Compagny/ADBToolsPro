using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Réglages de ce poste (page Paramètres), enregistrés dans %AppData%\ADBToolsPro\reglages.xml.
    /// Lecture tolérante : un fichier absent ou abîmé donne simplement les valeurs par défaut.
    /// </summary>
    public sealed class AppSettings
    {
        // --- Général : langue de l'interface ("fr" ou "en"), appliquée au redémarrage
        public string Language { get; set; } = "fr";

        // --- Master : dossiers du terminal déjà choisis pour déposer des fichiers (« fichier|dossier », voir DevicePathMemory)
        public List<string> MasterDevicePaths { get; set; } = new List<string>();

        // --- Outils avancés : « écran allumé quand branché » activé ici, valeur d'origine à remettre (« série|valeur »)
        public List<string> StayOnRestore { get; set; } = new List<string>();

        // --- Général : démarrage et affichage
        /// <summary>Page ouverte au démarrage : Dashboard, Mirroring, Packages, Explorer, Tools, Diag, Radar, Library, Logs.</summary>
        public string StartPage { get; set; } = "Dashboard";
        /// <summary>Taille de l'interface : 0.8, 0.9, 1.0, 1.15 ou 1.3.</summary>
        public double UiScale { get; set; } = 1.0;
        /// <summary>Console dépliée au démarrage.</summary>
        public bool ConsoleExpandedAtStartup { get; set; } = true;

        /// <summary>Écran d'accueil déjà vu (il ne s'affiche qu'au premier lancement).</summary>
        public bool WelcomeShown { get; set; }

        // --- Mises à jour
        public bool CheckUpdatesAtStartup { get; set; } = true;
        /// <summary>Dossier de diffusion de l'équipe (partage réseau où l'on dépose ADBToolsPro-X.Y.Z.zip). Vide : GitHub seulement.</summary>
        public string UpdateShareFolder { get; set; } = "";

        // --- Rapports : en-tête (balises {{MA_SOCIETE}}, {{COORDONNEES}}, {{TECHNICIEN}}) et dossier d'enregistrement
        public string ReportCompany { get; set; } = "";
        public string ReportContact { get; set; } = "";
        public string ReportTechnician { get; set; } = "";
        /// <summary>Dossier des rapports Word / RMA. Vide : dossier des captures.</summary>
        public string ReportsFolder { get; set; } = "";

        // --- Historique des diagnostics
        public bool HistoryEnabled { get; set; } = true;
        /// <summary>Durée de conservation en jours (0 = jamais supprimé).</summary>
        public int HistoryRetentionDays { get; set; }
        /// <summary>Dossier de l'historique. Vide : dossier « Historique » à côté du programme.</summary>
        public string HistoryFolder { get; set; } = "";

        // --- Profil d'équipe appliqué sur ce poste (affichage seulement)
        public string TeamProfileName { get; set; } = "";
        public DateTime? TeamProfileImported { get; set; }

        // --- Mise à jour OS : dossier des paquets (bibliothèque) et nombre d'envois simultanés (0 = tous)
        public string OsPackageLibraryFolder { get; set; } = "";
        public int OsMaxParallel { get; set; }

        // ------------------------------------------------------------------ Lecture / écriture

        public static string DataFolder
        {
            get
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ADBToolsPro");
                try { Directory.CreateDirectory(folder); } catch { }
                return folder;
            }
        }

        private static string FilePath => Path.Combine(DataFolder, "reglages.xml");

        private static AppSettings _current;

        /// <summary>Aucun réglage enregistré sur ce poste : nouvelle installation (premier lancement).</summary>
        [XmlIgnore]
        public static bool IsNewInstall { get; private set; }

        /// <summary>Réglages en cours (chargés au premier accès).</summary>
        public static AppSettings Current => _current ?? (_current = Load());

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var s = (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(fs);
                        if (s != null) { s.Normalize(); return s; }
                    }
                }
            }
            catch
            {
                // Fichier abîmé : on repart des valeurs par défaut (il sera réécrit au prochain changement).
            }
            var d = new AppSettings();
            try { IsNewInstall = !File.Exists(FilePath); } catch { }
            MigrateOldFiles(d);
            return d;
        }

        /// <summary>Reprend le choix « vérifier au démarrage » de la 1.1.1 (ancien fichier maj_auto.txt).</summary>
        private static void MigrateOldFiles(AppSettings s)
        {
            try
            {
                string old = Path.Combine(DataFolder, "maj_auto.txt");
                if (File.Exists(old)) s.CheckUpdatesAtStartup = File.ReadAllText(old).Trim() != "non";
            }
            catch { }
        }

        private void Normalize()
        {
            if (UiScale < 0.75 || UiScale > 1.5) UiScale = 1.0;
            if (string.IsNullOrWhiteSpace(StartPage)) StartPage = "Dashboard";
            if (UpdateShareFolder == null) UpdateShareFolder = "";
            ReportCompany = ReportCompany ?? "";
            ReportContact = ReportContact ?? "";
            ReportTechnician = ReportTechnician ?? "";
            ReportsFolder = ReportsFolder ?? "";
            HistoryFolder = HistoryFolder ?? "";
            if (HistoryRetentionDays < 0) HistoryRetentionDays = 0;
        }

        public static void Save()
        {
            try
            {
                string tmp = FilePath + ".tmp";
                using (var fs = File.Create(tmp))
                    new XmlSerializer(typeof(AppSettings)).Serialize(fs, Current);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch
            {
                // Dossier en lecture seule... : le réglage reste valable pour cette session.
            }
        }

        /// <summary>Supprime les réglages de ce poste (désinstallation).</summary>
        public static void ResetForUninstall() => _current = new AppSettings();
    }

    /// <summary>
    /// Taille d'interface conseillée selon la place disponible à l'écran (en points Windows, zoom de Windows déjà
    /// compris). L'interface est pensée pour 1400 × 800 à 100 % ; au-delà de 100 %, on garde de la marge.
    /// </summary>
    public static class UiScaleAdvisor
    {
        public const double DesignWidth = 1400;
        public const double DesignHeight = 800;
        public static readonly double[] Choices = { 0.8, 0.9, 1.0, 1.15, 1.3 };

        /// <summary>Place occupée par l'interface complète à cette taille, en part de l'écran (1 = tout l'écran).</summary>
        public static (double W, double H) Footprint(double scale, double areaWidth, double areaHeight) =>
            (areaWidth <= 0 ? 1 : DesignWidth * scale / areaWidth, areaHeight <= 0 ? 1 : DesignHeight * scale / areaHeight);

        public static bool Fits(double scale, double areaWidth, double areaHeight)
        {
            var f = Footprint(scale, areaWidth, areaHeight);
            return f.W <= 1.0001 && f.H <= 1.0001;
        }

        public static double Recommend(double areaWidth, double areaHeight)
        {
            if (areaWidth <= 0 || areaHeight <= 0) return 1.0;
            double best = Choices[0];
            foreach (var s in Choices)
            {
                double margin = s > 1.0 ? 1.25 : 1.0;   // agrandir seulement si l'écran est nettement plus grand
                if (DesignWidth * s * margin <= areaWidth + 0.5 && DesignHeight * s * margin <= areaHeight + 0.5) best = s;
            }
            return best;
        }
    }
}

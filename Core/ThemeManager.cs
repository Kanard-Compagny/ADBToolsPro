using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace ADBToolsNew.Core
{
    /// <summary>Thème réellement affiché.</summary>
    public enum AppTheme { Dark, Light }

    /// <summary>Choix de l'utilisateur : « Système » suit le réglage clair / sombre de Windows.</summary>
    public enum AppThemeMode { System, Light, Dark }

    /// <summary>
    /// Gestion des thèmes clair / sombre.
    /// Les couleurs vivent dans Themes\Theme.Dark.xaml et Themes\Theme.Light.xaml (mêmes clés dans les deux).
    /// Changer de thème = remplacer à chaud le dictionnaire fusionné dans App.xaml : tout ce qui est lié en
    /// DynamicResource se met à jour immédiatement. Le choix est mémorisé dans theme.txt à côté de l'exe
    /// (programme portable : rien dans AppData). En mode Système, le thème suit Windows en direct.
    /// </summary>
    public static class ThemeManager
    {
        private const string SettingsFileName = "theme.txt";
        private const string ThemeFolderMarker = "/Themes/Theme.";
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>Choix de l'utilisateur (Système par défaut).</summary>
        public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;

        /// <summary>Thème affiché actuellement.</summary>
        public static AppTheme Current { get; private set; } = AppTheme.Dark;

        /// <summary>Déclenché après chaque changement de thème (pour rafraîchir ce qui est coloré en code).</summary>
        public static event EventHandler ThemeChanged;

        private static bool _listeningToWindows;

        private static string SettingsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);

        public static string ModeLabel(AppThemeMode mode)
        {
            switch (mode)
            {
                case AppThemeMode.Light: return "Clair";
                case AppThemeMode.Dark: return "Sombre";
                default: return "Système";
            }
        }

        /// <summary>Applique le choix mémorisé. À appeler au démarrage.</summary>
        public static void ApplySaved()
        {
            SetMode(LoadSaved(), save: false);
            if (!_listeningToWindows)
            {
                // Windows passe en clair / sombre : on suit, si l'utilisateur a choisi « Système ».
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (Mode != AppThemeMode.System || e.Category != UserPreferenceCategory.General) return;
                    Application.Current?.Dispatcher.BeginInvoke(new Action(() => ApplyTheme(ResolveSystemTheme())));
                };
                _listeningToWindows = true;
            }
        }

        /// <summary>Passe au choix suivant : Système → Clair → Sombre → Système.</summary>
        public static void CycleMode()
        {
            SetMode(Mode == AppThemeMode.System ? AppThemeMode.Light : Mode == AppThemeMode.Light ? AppThemeMode.Dark : AppThemeMode.System);
        }

        public static void SetMode(AppThemeMode mode, bool save = true)
        {
            Mode = mode;
            if (save) Save(mode);
            ApplyTheme(mode == AppThemeMode.System ? ResolveSystemTheme() : mode == AppThemeMode.Light ? AppTheme.Light : AppTheme.Dark);
        }

        /// <summary>Thème de Windows pour les applications (clair ou sombre). Sombre si illisible.</summary>
        public static AppTheme ResolveSystemTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                {
                    if (key?.GetValue("AppsUseLightTheme") is int light) return light != 0 ? AppTheme.Light : AppTheme.Dark;
                }
            }
            catch { }
            return AppTheme.Dark;
        }

        private static void ApplyTheme(AppTheme theme)
        {
            var app = Application.Current;
            if (app == null) return;

            var newDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/Theme.{theme}.xaml", UriKind.Absolute)
            };

            var merged = app.Resources.MergedDictionaries;
            var old = merged.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains(ThemeFolderMarker));
            if (old != null)
            {
                if (Current == theme && old.Source.OriginalString.EndsWith($"Theme.{theme}.xaml")) { ThemeChanged?.Invoke(null, EventArgs.Empty); return; }
                merged[merged.IndexOf(old)] = newDict;
            }
            else
            {
                merged.Insert(0, newDict);
            }

            Current = theme;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static AppThemeMode LoadSaved()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string text = File.ReadAllText(SettingsPath).Trim();
                    if (Enum.TryParse(text, true, out AppThemeMode saved)) return saved;
                }
            }
            catch
            {
                // Fichier illisible : on garde le mode par défaut.
            }
            return AppThemeMode.System;
        }

        private static void Save(AppThemeMode mode)
        {
            try
            {
                File.WriteAllText(SettingsPath, mode.ToString());
            }
            catch
            {
                // Dossier en lecture seule (ex. clé protégée) : le thème s'applique quand même, sans être mémorisé.
            }
        }
    }
}

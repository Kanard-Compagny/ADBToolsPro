using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ADBToolsNew
{
    /// <summary>
    /// Logique d'interaction pour App.xaml
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Version affichée (splash, "À propos"), lue dans l'assembly : on ne la modifie plus qu'à un
        /// seul endroit, Properties\AssemblyInfo.cs (ex. "1.0.1").</summary>
        public static string VersionText
        {
            get
            {
                var v = typeof(App).Assembly.GetName().Version;
                return v == null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        private static readonly string CrashLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

        public App()
        {
            // Filet de sécurité global : une exception non prévue ne doit plus tuer l'application entière.
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Thème clair / sombre mémorisé : appliqué avant l'ouverture du splash.
            Core.ThemeManager.ApplySaved();
            base.OnStartup(e);
        }

        // --- Anti-avalanche des fenêtres d'erreur ---
        // Une erreur dans l'affichage (layout, rendu) se reproduit à chaque image : sans garde-fou, chaque
        // répétition ouvrait une nouvelle fenêtre, jusqu'à rendre l'application impossible à fermer.
        // Règles : une seule fenêtre d'erreur à la fois ; la même erreur répétée dans un court délai est
        // seulement journalisée ; la fenêtre propose de quitter proprement.
        private static bool _isShowingErrorDialog;
        private static string _lastErrorSignature;
        private static DateTime _lastErrorShownAt = DateTime.MinValue;
        private static int _suppressedErrorCount;
        private static readonly TimeSpan SameErrorQuietPeriod = TimeSpan.FromSeconds(30);

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Empêche la fermeture brutale du processus : l'app reste utilisable.
            e.Handled = true;

            var ex = e.Exception;
            string signature = ex.GetType().FullName + "|" + ex.Message;
            bool sameAsLast = signature == _lastErrorSignature && DateTime.Now - _lastErrorShownAt < SameErrorQuietPeriod;

            // Déjà une fenêtre ouverte, ou même erreur que celle qui vient d'être affichée : on note sans rien afficher.
            if (_isShowingErrorDialog || sameAsLast)
            {
                _suppressedErrorCount++;
                if (_suppressedErrorCount == 1 || _suppressedErrorCount % 50 == 0)
                    LogException($"UI Thread (Dispatcher) — répétition n°{_suppressedErrorCount}, fenêtre non réaffichée", ex);
                return;
            }

            LogException("UI Thread (Dispatcher)", ex);
            _lastErrorSignature = signature;
            _suppressedErrorCount = 0;
            _isShowingErrorDialog = true;
            try
            {
                var answer = MessageBox.Show(
                    $"Une erreur inattendue est survenue :\n\n{ex.Message}\n\n" +
                    "Les détails sont enregistrés dans crash.log.\n\n" +
                    "Voulez-vous fermer l'application ?\n" +
                    "• Oui : quitter maintenant\n" +
                    "• Non : continuer (si l'erreur se répète, elle ne sera plus affichée pendant 30 secondes)",
                    "Erreur inattendue",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Error,
                    MessageBoxResult.No);

                if (answer == MessageBoxResult.Yes)
                {
                    Shutdown(1);
                }
            }
            finally
            {
                _isShowingErrorDialog = false;
                _lastErrorShownAt = DateTime.Now;
            }
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // Ce type d'exception (hors thread UI) ne peut pas être "annulé" : on journalise avant l'arrêt.
            LogException("AppDomain (thread secondaire)", e.ExceptionObject as Exception);
        }

        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogException("Task non observée", e.Exception);

            // Empêche le crash du processus lors de la finalisation de la Task fautive.
            e.SetObserved();
        }

        // Taille max avant rotation : évite qu'un crash.log grossisse sans limite sur une install longue durée.
        private const long MaxCrashLogBytes = 2 * 1024 * 1024; // 2 Mo

        private static void LogException(string source, Exception ex)
        {
            try
            {
                RotateCrashLogIfTooLarge();
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
                File.AppendAllText(CrashLogPath, entry);
            }
            catch
            {
                // On ne doit jamais planter à l'intérieur d'un gestionnaire de crash.
            }
        }

        private static void RotateCrashLogIfTooLarge()
        {
            try
            {
                var info = new FileInfo(CrashLogPath);
                if (info.Exists && info.Length > MaxCrashLogBytes)
                {
                    string oldPath = CrashLogPath + ".old";
                    File.Delete(oldPath); // Pas d'erreur si absent : on garde un seul historique roulant
                    File.Move(CrashLogPath, oldPath);
                }
            }
            catch
            {
                // Idem : jamais d'exception ici, la rotation est un bonus, pas une garantie.
            }
        }
    }
}

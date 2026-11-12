using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ADBToolsNew
{
    /// <summary>
    /// Prise en main (comme Scanalyse) : écran d'accueil au premier lancement et visite guidée qui montre où se
    /// trouve chaque outil (voile sombre percé autour de l'élément présenté + bulle d'explication).
    /// Les deux se relancent depuis Paramètres ▸ Prise en main.
    /// </summary>
    public partial class MainWindow
    {
        private sealed class TourStep
        {
            public Func<FrameworkElement> Target;
            public string Title;
            public string Body;
        }

        private List<TourStep> _tourSteps;
        private int _tourIndex;

        // --------------------------------------------------------------- Accueil

        /// <summary>Au démarrage : accueil affiché une seule fois (premier lancement).</summary>
        private void ShowWelcomeIfFirstLaunch()
        {
            if (!AppSettings.Current.WelcomeShown) ShowWelcome();
        }

        private void ShowWelcome()
        {
            WelcomeOverlay.Visibility = Visibility.Visible;
        }

        private void CloseWelcome()
        {
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            if (!AppSettings.Current.WelcomeShown)
            {
                AppSettings.Current.WelcomeShown = true;
                AppSettings.Save();
            }
        }

        private void BtnWelcomeClose_Click(object sender, RoutedEventArgs e) => CloseWelcome();

        private void BtnWelcomeTour_Click(object sender, RoutedEventArgs e)
        {
            CloseWelcome();
            StartTour();
        }

        private void BtnShowWelcome_Click(object sender, RoutedEventArgs e) => ShowWelcome();

        private void BtnStartTour_Click(object sender, RoutedEventArgs e) => StartTour();

        // --------------------------------------------------------------- Visite guidée

        private List<TourStep> BuildTourSteps() => new List<TourStep>
        {
            new TourStep { Target = () => ConnectionBar, Title = Loc.T("Barre de connexion"),
                Body = Loc.T("Choisissez le terminal dans la liste, puis « Connecter ». Le voyant passe au vert une fois connecté. À droite : désactiver le mode développeur et les différents redémarrages (classique, recovery, bootloader).") },
            new TourStep { Target = () => SideMenuScroll, Title = Loc.T("Le menu"),
                Body = Loc.T("Chaque outil a sa page : informations du terminal, mirroring, applications, fichiers, outils avancés, diagnostics, Wi-Fi, bibliothèque de commandes, logs, comparateur, analyse de fichiers et mise à jour OS. La flèche en haut du menu le replie pour gagner de la place.") },
            new TourStep { Target = () => BtnNavDashboard, Title = Loc.T("Device Info"),
                Body = Loc.T("Le tableau de bord du terminal connecté : modèle, version d'Android, batterie, stockage, réseau. Les cartes changent de couleur selon l'état.") },
            new TourStep { Target = () => BtnNavMirroring, Title = Loc.T("Mirroring"),
                Body = Loc.T("Affiche et pilote l'écran du terminal sur le PC (scrcpy), avec captures d'écran et enregistrement vidéo.") },
            new TourStep { Target = () => BtnNavDiag, Title = Loc.T("Diagnostics SAV"),
                Body = Loc.T("Tests automatiques (Wi-Fi, batterie, stockage, capteurs…) et manuels (boutons, tactile, écran). « Tests du terminal » (caméras, son, capteurs, NFC, GPS…) et « Test du lecteur » passent par une petite appli installée le temps du test. Puis rapport Word à remettre au client.") },
            new TourStep { Target = () => BtnNavRadar, Title = Loc.T("Analyse Wi-Fi"),
                Body = Loc.T("Suivi en direct du signal et de la latence, avec graphiques et rapport Word. « Mesure sans câble » : le terminal mesure seul, débranché, pendant le parcours du site.") },
            new TourStep { Target = () => BtnNavLogs, Title = Loc.T("Logs"),
                Body = Loc.T("Journal Android en direct, avec filtres. « Analyser maintenant » repère plantages, ANR, mémoire, Wi-Fi, batterie et lecteur, avec un conseil pour chaque problème.") },
            new TourStep { Target = () => BtnNavCompare, Title = Loc.T("Comparateur"),
                Body = Loc.T("Compare deux terminaux, un terminal et sa référence validée, ou un terminal et un état précédent : seules les vraies différences ressortent.") },
            new TourStep { Target = () => BtnNavFileAnalysis, Title = Loc.T("Analyse de fichiers"),
                Body = Loc.T("Ouvre un relevé reçu du terrain (RxLogger, bugreport, logcat) : frise par sujet, « Aller à l'incident », constats et rapport. Le « Suivi terrain » enregistre des heures sur le terminal débranché.") },
            new TourStep { Target = () => BtnNavOsUpdate, Title = Loc.T("Mise à jour OS"),
                Body = Loc.T("Installe le paquet OS du fabricant sur un ou plusieurs terminaux en même temps, par câble ou par copie dans la mémoire du terminal : vérifications avant l'envoi, contrôle de la version au retour, bilan Excel.") },
            new TourStep { Target = () => BtnNavMaster, Title = Loc.T("Master"),
                Body = Loc.T("Remet des terminaux en état après un wipe. Capturez un terminal de référence (ou créez le Master à la main), vérifiez et complétez son contenu par onglets (ce qui manque est en orange), puis cochez les terminaux : chacun est analysé avant l'envoi, et le bilan dit ce qui a été remis en place ou non.") },
            new TourStep { Target = () => BtnNavDepannage, Title = Loc.T("Dépannage"),
                Body = Loc.T("Terminal non détecté ou non autorisé, écran cassé, tactile mort, débogage USB désactivé : choisissez le symptôme, la page explique la cause et propose les solutions (prise en main au clavier et à la souris par câble, récupération des données, recovery, combinaisons de touches).") },
            new TourStep { Target = () => ConsoleBorder, Title = Loc.T("La console"),
                Body = Loc.T("Chaque action y est notée, avec son résultat. « Copier Logs » pour transmettre un souci ; la flèche la replie.") },
            new TourStep { Target = () => BtnCapturesMenu, Title = Loc.T("Captures"),
                Body = Loc.T("Ouvre ou vide le dossier où sont rangés captures d'écran, vidéos, logs et rapports.") },
            new TourStep { Target = () => BtnThemeMode, Title = Loc.T("Thème"),
                Body = Loc.T("Passe de « Système » (comme Windows) à Clair puis Sombre.") },
            new TourStep { Target = () => BtnNavSettings, Title = Loc.T("Paramètres"),
                Body = Loc.T("Réglages de ce poste : démarrage, mises à jour, outils tiers, en-tête et modèles des rapports… La visite se relance aussi d'ici (Prise en main).") },
        };

        private void StartTour()
        {
            // Revenir sur une page « normale » pour que les éléments présentés soient visibles.
            if (MainTabControl != null && MainTabControl.SelectedIndex == SettingsTabIndex) NavDashboard_Click(null, null);
            _tourSteps = BuildTourSteps();
            _tourIndex = 0;
            TourOverlay.Visibility = Visibility.Visible;
            TourOverlay.SizeChanged -= TourOverlay_SizeChanged;
            TourOverlay.SizeChanged += TourOverlay_SizeChanged;
            PreviewKeyDown -= Tour_PreviewKeyDown;
            PreviewKeyDown += Tour_PreviewKeyDown;
            // Attendre la mise en page (la page Device Info vient peut-être d'être affichée).
            Dispatcher.BeginInvoke(new Action(ShowTourStep), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void EndTour()
        {
            TourOverlay.Visibility = Visibility.Collapsed;
            TourOverlay.SizeChanged -= TourOverlay_SizeChanged;
            PreviewKeyDown -= Tour_PreviewKeyDown;
        }

        private void TourOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (TourOverlay.Visibility == Visibility.Visible) ShowTourStep();
        }

        private void Tour_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (TourOverlay.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Escape) { EndTour(); e.Handled = true; }
            else if (e.Key == Key.Right || e.Key == Key.Enter) { BtnTourNext_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Left) { BtnTourPrev_Click(null, null); e.Handled = true; }
        }

        private void BtnTourNext_Click(object sender, RoutedEventArgs e)
        {
            if (_tourSteps == null) return;
            if (_tourIndex >= _tourSteps.Count - 1) { EndTour(); return; }
            _tourIndex++;
            ShowTourStep();
        }

        private void BtnTourPrev_Click(object sender, RoutedEventArgs e)
        {
            if (_tourSteps == null || _tourIndex == 0) return;
            _tourIndex--;
            ShowTourStep();
        }

        private void BtnTourSkip_Click(object sender, RoutedEventArgs e) => EndTour();

        /// <summary>Place le voile, le cadre et la bulle autour de l'élément de l'étape en cours.</summary>
        private void ShowTourStep()
        {
            if (_tourSteps == null || _tourSteps.Count == 0) return;
            var step = _tourSteps[_tourIndex];
            double w = TourOverlay.ActualWidth, h = TourOverlay.ActualHeight;
            if (w <= 0 || h <= 0) return;

            TxtTourStep.Text = $"{_tourIndex + 1} / {_tourSteps.Count}";
            TxtTourTitle.Text = step.Title;
            TxtTourBody.Text = step.Body;
            BtnTourPrev.IsEnabled = _tourIndex > 0;
            BtnTourNext.Content = _tourIndex == _tourSteps.Count - 1 ? Loc.T("Terminer") : Loc.T("Suivant");

            Rect target = Rect.Empty;
            var el = step.Target?.Invoke();
            if (el != null && el.IsVisible && el.ActualWidth > 0)
            {
                try
                {
                    target = el.TransformToVisual(TourOverlay).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
                    target.Inflate(6, 6);
                    target.Intersect(new Rect(0, 0, w, h));
                }
                catch { target = Rect.Empty; }
            }

            var full = new RectangleGeometry(new Rect(0, 0, w, h));
            if (target.IsEmpty)
            {
                TourShade.Data = full;
                TourHighlight.Visibility = Visibility.Collapsed;
            }
            else
            {
                TourShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(target, 8, 8));
                TourHighlight.Visibility = Visibility.Visible;
                TourHighlight.Width = target.Width;
                TourHighlight.Height = target.Height;
                Canvas.SetLeft(TourHighlight, target.Left);
                Canvas.SetTop(TourHighlight, target.Top);
            }

            // Bulle : à droite de l'élément si la place le permet, sinon à gauche, en dessous ou au-dessus.
            TourCallout.Measure(new Size(TourCallout.Width, double.PositiveInfinity));
            double cw = TourCallout.Width, ch = TourCallout.DesiredSize.Height, gap = 14, margin = 12;
            double x, y;
            if (target.IsEmpty) { x = (w - cw) / 2; y = (h - ch) / 2; }
            else if (target.Right + gap + cw + margin <= w) { x = target.Right + gap; y = target.Top; }
            else if (target.Left - gap - cw - margin >= 0) { x = target.Left - gap - cw; y = target.Top; }
            else if (target.Bottom + gap + ch + margin <= h) { x = target.Left; y = target.Bottom + gap; }
            else { x = target.Left; y = target.Top - gap - ch; }
            x = Math.Max(margin, Math.Min(x, w - cw - margin));
            y = Math.Max(margin, Math.Min(y, h - ch - margin));
            Canvas.SetLeft(TourCallout, x);
            Canvas.SetTop(TourCallout, y);
        }
    }
}

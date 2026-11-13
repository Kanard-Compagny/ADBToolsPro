## Nouveautés 1.7.0

- **Diagnostics SAV : une seule fiche**, une ligne par élément du terminal, rangée par catégorie (système ; écran, tactile et boutons ; lecteur ; caméras ; audio ; capteurs ; connexions).
  - La mention à droite dit qui teste : le PC, l'appli Diag sur le terminal, ou les deux au choix (écran, tactile, boutons, lecteur, capteurs).
  - « Check-up auto (PC) », « Tester sur le terminal » (toute la batterie dans l'appli) et « Récupérer du terminal ».
  - Pour chaque ligne : la consigne, le lancement sur le terminal ou sur le PC, l'écoute en direct, l'aperçu de ce qui a été fait sur le terminal (grille tactile balayée, touches pressées), photos et enregistrements.
  - Résultat retenu et commentaire **modifiables après récupération**, avec l'historique de la ligne. Les résultats reçus passent par une fenêtre où l'on coche ce qui entre dans la fiche : rien n'est écrasé sans le vouloir.
  - Tests absents du modèle grisés et exclus du rapport. Le rapport Word reprend la fiche, avec la grille tactile du terminal en image.
- **Appli ADB Tools Diag, refaite** aux couleurs d'ADB Tools, en français ou en anglais, thème clair ou sombre.
  - Menu burger : diagnostic complet, lecteur, Wi-Fi, touches, suivi terrain, résultats.
  - Tests : écran, tactile, touches (marche comprise), caméras, flash, haut-parleur, écouteur, micro avec niveau, vibreur, capteurs avec réaction, lecteur (DataWedge ou clavier), **RFID**, NFC, GPS, Bluetooth, vitesse de la mémoire. Seuls les tests possibles sur le modèle sont proposés.
  - Fonctionne **branché ou sans PC** : les résultats restent sur le terminal ; écran Résultats pour refaire un test, changer un résultat et **« Envoyer au PC »**.
  - **Mesure Wi-Fi** (cible du ping au choix, ex. 8.8.8.8) et **suivi terrain** (anciennement « enregistreur terrain ») : bilan gardé à la fin, puis « Envoyer au PC » ouvre directement l'analyse Wi-Fi ou l'analyse de fichiers.
  - L'appli **reste installée**. Bouton **« Appli Diag »** dans la barre du haut : installer, ouvrir, retirer ; il passe en orange quand une mesure ou un suivi attend d'être récupéré.
  - Désinstallation possible depuis le terminal (menu « Retirer l'appli »), sans laisser de réglage DataWedge derrière.
- **Master : réglages constructeur** (onglet « Constructeur »), Zebra pour commencer, prévu pour d'autres marques.
  - Import d'un ou plusieurs **profils StageNow** (Export to MDM, XML), **fusion** en un seul profil ; étapes en clair par thème (Wi-Fi, EHS, Velocity, DataWedge, applis, fichiers, écran et énergie…).
  - **Éditeur XML** en fenêtre séparée : vérifié à chaque frappe, le Master suit en direct ; original gardé, changements visibles.
  - Étapes sensibles (remise à zéro, mise à jour d'OS, débogage USB…) décochées, confirmées en tapant APPLIQUER, appliquées en dernier.
  - Fichiers du profil : à joindre depuis le PC ou depuis l'apk d'une appli du Master ; un fichier téléchargé par un lien est repéré et peut être fourni à la place.
  - Application par la mini appli ADB Tools Helper (EMDK), seulement sur les terminaux Zebra ; la réponse du terminal figure au bilan.
  - Onglet Fichiers : dépôt dans les dossiers protégés de Zebra (`/enterprise`, ex. configuration EHS) par MX, sans root ; **mémoire des dossiers** déjà utilisés.
- **Dépannage** (nouvelle page du menu) : par symptôme (non détecté, non autorisé, écran cassé, tactile mort, pas de débogage USB), prise en main au clavier et à la souris par câble, récupération des données, recovery, et **combinaisons de touches par marque**, consultables hors connexion et à compléter.
- **Premier lancement** : langue, taille de l'interface conseillée selon l'écran (avec aperçu) et thème.
- **Divers** : toutes les fenêtres séparées à la charte (coins arrondis, barres de défilement) ; visite guidée complétée ; nouveau composant libre AvalonEdit (MIT) pour l'éditeur XML.

## Installation
1. Déjà en 1.1.1 ou plus : **Paramètres ▸ Mises à jour ▸ Installer** (ou dossier de diffusion de l'équipe).
2. Sinon : téléchargez `ADBToolsPro-1.7.0.zip` ci-dessous et décompressez-le où vous voulez (aucune installation), puis lancez `ADBToolsNew.exe`.

Prérequis : Windows 10 ou 11 avec .NET Framework 4.7.2 ou plus récent (déjà présent sur les Windows à jour).

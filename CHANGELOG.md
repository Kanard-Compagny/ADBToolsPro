## Nouveautés de la version 1.2.0

Une page **Paramètres** entièrement revue, sur le modèle de Scanalyse.

### Général
- Page ouverte au démarrage, **taille de l'interface** (80 / 90 / 100 / 115 / 130 %, les petites tailles pour les écrans de PC portable), console dépliée ou non au démarrage.
- Sur un petit écran, la fenêtre ne dépasse plus de l'écran au premier lancement.

### Mises à jour
- **Dossier de diffusion de l'équipe** : ADB Tools y cherche aussi les nouvelles versions (partage réseau où l'on dépose `ADBToolsPro-X.Y.Z.zip` et son `.sha256`), en plus de GitHub.
- État de chaque source affiché clairement (dépôt privé, dossier introuvable…).

### Prise en main
- **Écran d'accueil** au premier lancement : présentation, comment brancher un terminal en 3 étapes, choix du thème.
- **Visite guidée** qui montre où se trouve chaque outil ; les deux se relancent depuis Paramètres ▸ Prise en main.

### Rapports Word
- **En-tête des rapports** : votre société, vos coordonnées, votre nom et votre logo remplissent `{{MA_SOCIETE}}`, `{{COORDONNEES}}`, `{{TECHNICIEN}}` et `{{LOGO}}`. Les modèles fournis les affichent façon papier à lettres.
- **Dossier des rapports** au choix (dossier des captures par défaut).
- Une ligne de modèle qui ne contient qu'une balise vide disparaît du rapport.
- Rapport Wi-Fi : graphiques clairs pour l'impression, sur toute la session, avec une échelle adaptée aux mesures.

### Historique
- Chaque rapport exporté (Diagnostic SAV, RMA, Analyse Wi-Fi) est rangé dans l'**historique du terminal**, par numéro de série, avec les résultats de chaque test et une copie du rapport.
- Bouton **Historique** dans les Diagnostics SAV, recherche, durée de conservation, dossier partageable avec l'équipe.

### Profil d'équipe
- Exporter / importer un fichier **`.adbprofil`** : en-tête et logo, modèles Word, bibliothèque de commandes ADB, dossier des mises à jour, dossier des rapports. Le contenu est affiché avant d'être appliqué ; rien n'est supprimé.

### À propos et Désinstaller
- Encart **Soutenir ADB Tools** (don libre, sans engagement).
- Bloc **Diagnostic** à copier pour une demande d'aide, lien vers le dossier des données.
- **Désinstaller** : efface les réglages (et vos données si vous le cochez), puis ouvre l'Explorateur sur le programme.

## Installation
1. Déjà en 1.1.1 ou plus : **Paramètres ▸ Mises à jour ▸ Installer**.
2. Sinon : téléchargez `ADBToolsPro-1.2.0.zip` ci-dessous et décompressez-le où vous voulez (aucune installation), puis lancez `ADBToolsNew.exe`.

Prérequis : Windows 10 ou 11 avec .NET Framework 4.7.2 ou plus récent (déjà présent sur les Windows à jour).

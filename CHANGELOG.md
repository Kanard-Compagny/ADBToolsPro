## Correctifs 1.8.1

- **Profils Scan2Deploy (Datalogic)** lus d'après le schéma le plus récent de Datalogic (1.13), comme ceux exportés par Scan2Deploy Studio :
  - reconnus : lanceur par défaut, fuseau horaire, réseaux Wi-Fi supplémentaires, mise à jour de l'agent, option OEMConfig ;
  - action de fin « ouvrir l'appli … » au lieu de l'intent brut ; script résumé par commandes, mise à jour système (UPDATE) en étape sensible ;
  - déploiement d'une archive déjà sur le terminal : l'archive devient un fichier du profil à joindre, déposée avant l'envoi à l'agent.
- Master : « Rien pour l'instant » n'apparaît plus sous des profils constructeur existants.

## Nouveautés 1.8.0

- **Master, réglages constructeur : trois marques.** Le bouton « Importer un profil constructeur » reconnaît le format au contenu du fichier.
  - **Zebra** : profils StageNow, comme en 1.7.
  - **Datalogic** : profils **Scan2Deploy** (`.json`) lus en étapes claires (Wi-Fi, heure, barre d'état, ADB, script, archive à déployer, fichiers) ; envoyés en une fois à l'agent Scan2Deploy du terminal avec les seules étapes cochées, compte rendu de l'agent au bilan.
  - **Honeywell** : **DeviceConfig.xml** (EZConfig) lu par section ; déposé dans `honeywell/persist` (mode Provisioning activé) avec les seules sections cochées, puis le terminal redémarre en fin de Master pour l'appliquer.
  - Étapes sensibles des trois formats (remise à zéro, ADB coupé…) toujours confirmées en tapant APPLIQUER.
  - *Datalogic et Honeywell sont faits d'après la documentation des marques : vos remontées terrain sont les bienvenues.*
- **Modèles d'étapes MX (Zebra)** : bouton **« Ajouter une étape »** dans l'éditeur XML et sur chaque profil Zebra. Catalogue de 15 modèles (Wi-Fi, installer/désinstaller/activer une appli, lanceur, déposer ou télécharger un fichier, configuration EHS, veille, date et fuseau, langue, Bluetooth/NFC/GPS, redémarrage final), avec ce que le profil contient déjà. Quelques champs vérifiés, le XML ajouté visible, insertion propre (Ctrl+Z la retire). Fichier choisi sur le PC joint au Master automatiquement.
- **« Créer un profil Zebra vide »** dans le Master, pour partir de zéro avec le catalogue.
- **Outils avancés, refait** :
  - tout l'état du terminal est relu à l'ouverture et au changement de terminal (NFC, localisation, données mobiles, veille, volume, mode sonnerie compris) ; ce qui n'existe pas sur le terminal est grisé avec la raison ;
  - chaque interrupteur vérifie l'état réel ; si le réglage ne prend pas, l'écran Android concerné s'ouvre sur le terminal ;
  - plus de touches (applis récentes, volume, réveil, veille) et gâchette de scan sur Zebra (DataWedge) ;
  - **« Ouvrir sur le terminal »** : Paramètres, Wi-Fi, Bluetooth, Options développeur, Date et heure, Langue… (toutes marques) ;
  - **date et heure** : écart avec le PC signalé, heure et fuseau automatiques ;
  - **écran allumé quand branché**, avec garde-fou : remis comme avant au changement de terminal ou à la fermeture, rappel orange dans la barre du haut.

## Installation
1. Déjà en 1.1.1 ou plus : **Paramètres ▸ Mises à jour ▸ Installer** (ou dossier de diffusion de l'équipe).
2. Sinon : téléchargez `ADBToolsPro-1.8.1.zip` ci-dessous et décompressez-le où vous voulez (aucune installation), puis lancez `ADBToolsNew.exe`.

Prérequis : Windows 10 ou 11 avec .NET Framework 4.7.2 ou plus récent (déjà présent sur les Windows à jour).

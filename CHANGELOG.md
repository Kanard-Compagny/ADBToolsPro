## Nouveautés 1.5.0

- **Mise à jour OS** (nouvelle page du menu) : un ou plusieurs terminaux branchés en USB mis à jour en même temps, chacun avec sa progression, toutes marques compatibles ADB.
  - **Trois méthodes** : **par câble** (ADB sideload, passage automatique en mode mise à jour), **copie dans la mémoire du terminal** puis Recovery pour les modèles qui refusent le sideload, et **fastboot** (image d'usine) pour les terminaux au bootloader déverrouillé, avec mise en garde détaillée : rien n'est flashé si le bootloader est verrouillé ou si le modèle ne correspond pas.
  - **Vérifications avant l'envoi** : modèle visé par le paquet (plateforme Zebra et DeviceManifest.xml compris), terminal déjà à jour, mise à jour différentielle, batterie sous 30 %. Un retour à une version antérieure est signalé ; avec un paquet de « downgrade » du fabricant, le terminal passe par « Downgrade from ADB » (il est effacé, la confirmation le rappelle).
  - **Bibliothèque** : un dossier de paquets (le vôtre ou celui de l'équipe) où ADB Tools choisit le paquet le plus récent prévu pour chaque modèle ; un parc mélangé se met à jour en une fois.
  - **Au retour** : contrôle de la nouvelle version, puis installation d'applications (APK un par un ou tout un dossier) et commandes ADB au choix.
  - **Bilan Excel** (.xlsx) : versions avant / après, méthode, paquet, résultat coloré, détail, heures. Envois simultanés limitables à partir de 5 terminaux.
  - Un envoi commencé n'est jamais interrompu ; ADB Tools refuse de se fermer tant qu'un terminal est en cours.
- **Barre des tâches Windows** : progression verte sur l'icône tant qu'une tâche tourne (logs en direct, Wi-Fi, mise à jour OS…), clignotement à la fin d'une mise à jour si ADB Tools est au second plan.
- **Divers** : Outils avancés réorganisé ; boutons orange estompés quand ils sont indisponibles ; visite guidée complétée (Logs, Comparateur, Analyse de fichiers, Mise à jour OS).

## Installation
1. Déjà en 1.1.1 ou plus : **Paramètres ▸ Mises à jour ▸ Installer** (ou dossier de diffusion de l'équipe).
2. Sinon : téléchargez `ADBToolsPro-1.5.0.zip` ci-dessous et décompressez-le où vous voulez (aucune installation), puis lancez `ADBToolsNew.exe`.

Prérequis : Windows 10 ou 11 avec .NET Framework 4.7.2 ou plus récent (déjà présent sur les Windows à jour).

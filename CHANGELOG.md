## Nouveautés 1.6.0

- **Master** (nouvelle page du menu) : remettre des terminaux en état après un wipe, toutes marques compatibles ADB.
  - **Capture d'un terminal de référence**, sans rien y modifier : applications (morceaux compris) et leurs autorisations, applis désactivées, réglages Android, langue et fuseau horaire, réseaux Wi-Fi, lanceur, propriétaire du terminal (MDM). Ou **Master créé à la main**, sans terminal de référence.
  - **Tout se vérifie et se complète avant l'envoi**, par onglets :
    - applications avec leur origine (MDM, Play Store d'entreprise, livrée avec le terminal…) ;
    - réglages en **formulaire par thème** avec des valeurs en clair : « Ne pas modifier » ou valeur imposée ;
    - réglages techniques expliqués et rangés par catégorie ;
    - Wi-Fi avec type de sécurité et mot de passe ;
    - étapes à faire à la main, guidées : compte Google, Wi-Fi d'entreprise, certificat, enrôlement MDM… ;
    - fichiers (ex. configuration DataWedge) et commandes ADB.

    Ce qui reste à compléter est signalé en orange.
  - **Analyse de chaque terminal coché** : applis absentes, plus anciennes, identiques ou plus récentes, modèle ou Android différents. Choix par appli : installer, mettre à jour, garder la version du terminal ou rétrograder.
  - **Application en parallèle** et **bilan détaillé** par terminal : chaque élément appliqué, déjà en place, ignoré ou refusé avec la raison en clair ; chaque réglage modifié avec son ancienne et sa nouvelle valeur ; export Excel.
  - **Mini appli « ADB Tools Helper »** (sans icône, elle reste sur le terminal) : enregistre les réseaux Wi-Fi et change la langue, ce qu'Android n'autorise plus par câble. En dernier recours, un **QR code Wi-Fi** à scanner depuis le terminal.
  - Fichier **.adbmaster** à partager dans l'équipe. Les mots de passe Wi-Fi y sont enregistrés en clair : gardez-le en lieu sûr.
- **Divers** : visite guidée complétée (Master).

## Installation
1. Déjà en 1.1.1 ou plus : **Paramètres ▸ Mises à jour ▸ Installer** (ou dossier de diffusion de l'équipe).
2. Sinon : téléchargez `ADBToolsPro-1.6.0.zip` ci-dessous et décompressez-le où vous voulez (aucune installation), puis lancez `ADBToolsNew.exe`.

Prérequis : Windows 10 ou 11 avec .NET Framework 4.7.2 ou plus récent (déjà présent sur les Windows à jour).

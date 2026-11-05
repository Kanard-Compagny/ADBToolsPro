## Nouveautés 1.3.0

- **Analyse automatique des logs** (page Logs) : sur la capture en direct, sur le journal actuel du terminal (« Analyse rapide », logcat -d) ou sur un fichier reçu (logcat .txt / .log, bugreport .zip). Repère les plantages (Java, natifs), les applications figées (ANR), la mémoire saturée, le stockage plein, la batterie et la chauffe, les redémarrages, les problèmes Wi-Fi (déconnexions et leur raison, mot de passe refusé, DHCP, itinérance), DNS, serveurs injoignables et certificats, ainsi que les erreurs du lecteur code-barres (DataWedge, Honeywell, Datalogic, Newland, Urovo…). Résultats triés par gravité, avec un conseil pour chacun, clic vers la ligne concernée, copie et export .txt.
- **Comparateur de terminaux** (nouvelle page du menu) : deux terminaux branchés, un terminal et une référence (.adbref, terminal modèle validé) ou un terminal et l'un de ses états précédents. Version Android, correctif de sécurité, build, applications et versions, applications désactivées, réglages et propriétés ; les valeurs propres à chaque appareil sont ignorées. Chaque relevé est mémorisé dans l'historique du terminal. Export .csv.
- **Test du lecteur code-barres** (Diagnostics SAV ▸ Laboratoire) : une petite appli de test est installée sur le terminal le temps du test, puis désinstallée. Lectures en direct (données avec caractères invisibles, longueur, symbologie transmise sur Zebra et Honeywell, estimée sur les autres marques), gâchette détectée (délai jusqu'à la lecture, lectures ratées, taux de réussite), cadence, contrôle d'une liste de codes attendus. Résultat reporté dans « Optique Scanner » du diagnostic SAV et du rapport Word, export .csv.
- **Mises à jour, comme dans Scanalyse** : la pastille « Mise à jour X » s'affiche dans la barre de titre, à côté de la version ; un clic ouvre la fenêtre d'installation avec les nouveautés de la version, puis « Installer et redémarrer ».

## Installation
1. Déjà en 1.1.1 ou plus : **Paramètres ▸ Mises à jour ▸ Installer** (ou dossier de diffusion de l'équipe).
2. Sinon : téléchargez `ADBToolsPro-1.3.0.zip` ci-dessous et décompressez-le où vous voulez (aucune installation), puis lancez `ADBToolsNew.exe`.

Prérequis : Windows 10 ou 11 avec .NET Framework 4.7.2 ou plus récent (déjà présent sur les Windows à jour).

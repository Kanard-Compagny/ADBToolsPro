# Sécurité / Security

## Versions suivies / Supported versions

Seule la **dernière version publiée** reçoit des correctifs. Le programme propose lui-même la mise à jour (vérifiée par SHA-256).
Only the **latest release** receives fixes. The program offers the update itself (SHA-256 verified).

## Signaler une faille / Reporting a vulnerability

Merci de **ne pas ouvrir de ticket public** pour une faille de sécurité.
Utilisez le signalement privé de GitHub : onglet **Security → Report a vulnerability**, ou directement
<https://github.com/Kanard-Compagny/ADBToolsPro/security/advisories/new>.

Please **do not open a public issue** for a security vulnerability. Use GitHub private reporting:
**Security → Report a vulnerability**, or the link above.

Indiquez si possible / Please include if possible:
- la version d'ADB Tools Pro et de Windows / ADB Tools Pro and Windows versions;
- les étapes pour reproduire et l'impact constaté / steps to reproduce and observed impact;
- une preuve de concept minimale, **sans données réelles de clients** / a minimal proof of concept, **without real customer data**.

Un premier retour est donné sous 7 jours. Le correctif est publié dans une nouvelle version, et l'auteur du signalement est remercié dans les notes de version s'il le souhaite.
You will get a first answer within 7 days. The fix ships in a new release, and the reporter is credited in the release notes if they wish.

## Ce qui est dans le périmètre / In scope

- Le programme Windows ADB Tools Pro et l'appli de diagnostic Android installée temporairement sur le terminal.
- Le mécanisme de mise à jour (téléchargement, vérification SHA-256, installation).
- Les fichiers que le programme ouvre : profils d'équipe `.adbprofil`, modèles Word, archives de journaux.

Les outils tiers téléchargés par le programme (ADB / Fastboot, scrcpy, FFmpeg) relèvent de leurs propres projets.
Third-party tools downloaded by the program (ADB / Fastboot, scrcpy, FFmpeg) belong to their own projects.

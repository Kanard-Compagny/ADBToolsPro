# Contribuer à ADB Tools Pro

Merci de votre intérêt ! ADB Tools Pro est né sur le terrain, au service des techniciens. Chaque retour compte : un bug signalé, une idée, une traduction corrigée ou du code.

*English speakers are welcome: issues, discussions and pull requests can be written in English.*

## Avant de commencer

- **Une question d'utilisation ?** Posez-la dans les [Discussions](https://github.com/Kanard-Compagny/ADBToolsPro/discussions).
- **Un bug ou une idée ?** Ouvrez un [ticket](https://github.com/Kanard-Compagny/ADBToolsPro/issues/new/choose) avec le modèle proposé.
- **Une faille de sécurité ?** Jamais en public : voir [SECURITY.md](SECURITY.md).
- **Un changement important ?** Ouvrez d'abord un ticket pour en parler : cela évite de travailler pour rien.

## Ne jamais publier de données confidentielles

Les terminaux suivis par ADB Tools appartiennent souvent à des clients. Dans les tickets, les captures, les journaux et les fichiers d'exemple des tests, **retirez** les noms de réseau Wi-Fi, les noms de société, les numéros de série, les adresses IP internes et les identifiants.

## Compiler

- Windows 10 ou 11, **Visual Studio 2022 ou plus récent** avec la charge de travail « Développement .NET Desktop » et le pack de ciblage **.NET Framework 4.7.2**.
- Ouvrez `ADBToolsNew.slnx`, restaurez les packages NuGet, compilez.
- Les tests (MSTest) sont dans `Tests/` ; l'onglet **Actions** les exécute à chaque envoi et produit une archive de test téléchargeable.
- L'appli Android de diagnostic (`DiagApp/`) se compile avec `DiagApp/build.ps1` (SDK Android et JDK 11 ou plus récent, sans Gradle), ou laissez faire l'onglet Actions.

## Règles de la maison

1. **Ne rien casser, seulement améliorer.** Un changement ne doit pas retirer une fonction existante ni changer un comportement attendu sans raison discutée.
2. **Le français est la langue de référence.** Les textes de l'interface sont écrits en français dans le XAML et via `Loc.T("…")` dans le code ; ajoutez la traduction anglaise correspondante dans `Lang/en.json` (clé = texte français exact).
3. **Thèmes clair et sombre.** Utilisez les ressources de couleur (`{DynamicResource …}`) plutôt que des couleurs en dur, et vérifiez la lisibilité dans les deux thèmes.
4. **Identité visuelle.** Le vert est la couleur de la marque ; l'orange est réservé à l'action principale d'un écran.
5. **Pour les techniciens, pas pour les développeurs.** Les messages expliquent ce qui se passe et quoi faire, sans jargon inutile.
6. **Tout reste sur le PC.** Aucune donnée de terminal ni aucun rapport ne doit être envoyé sur Internet.
7. **Fichiers** : `.cs` en UTF-8 avec BOM et fins de ligne CRLF ; le projet principal est au format « ancien » : ajoutez chaque nouveau fichier `.cs` dans `ADBToolsNew.csproj` (`<Compile Include="…"/>`).
8. **Tests** : toute logique d'analyse (journaux, Wi-Fi, diagnostics) s'accompagne d'un test dans `Tests/`.

## Proposer une modification

1. Créez une branche depuis `main` dans votre copie (fork).
2. Faites des envois courts et clairs, en français ou en anglais.
3. Ouvrez une demande de fusion (pull request) en remplissant le modèle : ce que ça change, comment vous l'avez testé (terminal, version d'Android).
4. Les tests de l'onglet Actions doivent passer.

## Licence

En contribuant, vous acceptez que votre contribution soit publiée sous la licence du projet, la [GNU GPL v3](LICENSE).

Merci de respecter le [code de conduite](CODE_OF_CONDUCT.md) dans tous les échanges.

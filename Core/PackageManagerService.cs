using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Encapsule les opérations du Package Manager : listage/parsing des paquets installés et actions
    /// (lancer, forcer l'arrêt, vider le cache, désinstaller, extraire l'APK, installer). Aucune dépendance UI.
    /// </summary>
    public class PackageManagerService
    {
        private readonly AdbManager _adbManager;

        public PackageManagerService(AdbManager adbManager)
        {
            _adbManager = adbManager;
        }

        /// <summary>Récupère et parse la liste complète des paquets (type Utilisateur/Système, statut Actif/Désactivé).</summary>
        public async Task<List<AndroidPackageItem>> ListPackagesAsync(string serial)
        {
            // On utilise 'pm list packages -f' (avec le chemin) et 'pm list packages -3' (pour cibler les apps tiers)
            // Les trois listes sont demandées en parallèle (auparavant l'une après l'autre).
            var allTask = _adbManager.ExecuteAsync($"-s {serial} shell pm list packages -f");
            var thirdPartyTask = _adbManager.ExecuteAsync($"-s {serial} shell pm list packages -3");
            var disabledTask = _adbManager.ExecuteAsync($"-s {serial} shell pm list packages -d");
            await Task.WhenAll(allTask, thirdPartyTask, disabledTask);

            var resultAll = allTask.Result;
            var resultThirdParty = thirdPartyTask.Result;
            var resultDisabled = disabledTask.Result;

            if (!resultAll.IsSuccess) return new List<AndroidPackageItem>();

            return ParsePackageList(
                resultAll.StandardOutput,
                resultThirdParty.IsSuccess ? resultThirdParty.StandardOutput : "",
                resultDisabled.IsSuccess ? resultDisabled.StandardOutput : "");
        }

        /// <summary>Parse la sortie combinée de "pm list packages -f/-3/-d" en liste triée. Fonction pure, testable.</summary>
        public static List<AndroidPackageItem> ParsePackageList(string allOutput, string thirdPartyOutput, string disabledOutput)
        {
            var list = new List<AndroidPackageItem>();
            var allLines = (allOutput ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var thirdPartyNames = SplitPackageNames(thirdPartyOutput);
            var disabledNames = SplitPackageNames(disabledOutput);

            foreach (var line in allLines)
            {
                if (line.StartsWith("package:"))
                {
                    int eqIdx = line.LastIndexOf('=');
                    if (eqIdx != -1)
                    {
                        string pkgName = line.Substring(eqIdx + 1).Trim();

                        // Détermination robuste du type
                        bool isThirdParty = thirdPartyNames.Contains(pkgName);
                        bool isDisabled = disabledNames.Contains(pkgName);

                        list.Add(new AndroidPackageItem
                        {
                            PackageName = pkgName,
                            Type = isThirdParty ? "Utilisateur" : "Système",
                            Status = isDisabled ? "Désactivé" : "Actif"
                        });
                    }
                }
            }

            return list.OrderBy(p => p.PackageName).ToList(); // Tri alphabétique par défaut
        }

        // HashSet : recherche en temps constant (auparavant List.Contains dans une boucle sur 300-600 paquets).
        private static HashSet<string> SplitPackageNames(string output)
        {
            if (string.IsNullOrEmpty(output)) return new HashSet<string>();
            return new HashSet<string>(output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Replace("package:", "").Trim()));
        }

        public Task<AdbCommandResult> LaunchAsync(string serial, string packageName) =>
            _adbManager.ExecuteAsync($"-s {serial} shell monkey -p {AdbManager.QuoteRemoteArg(packageName)} -c android.intent.category.LAUNCHER 1");

        public Task<AdbCommandResult> ForceStopAsync(string serial, string packageName) =>
            _adbManager.ExecuteAsync($"-s {serial} shell am force-stop {AdbManager.QuoteRemoteArg(packageName)}");

        public Task<AdbCommandResult> ClearDataAsync(string serial, string packageName) =>
            _adbManager.ExecuteAsync($"-s {serial} shell pm clear {AdbManager.QuoteRemoteArg(packageName)}");

        public Task<AdbCommandResult> UninstallAsync(string serial, string packageName) =>
            _adbManager.ExecuteAsync($"-s {serial} uninstall {AdbManager.QuoteLocalArg(packageName)}");

        /// <summary>
        /// Résout le(s) chemin(s) distant(s) des APK d'un paquet. Une app "split APK" (cas courant des apps du
        /// Play Store) en renvoie plusieurs : base.apk + split_config.*.apk. Liste vide si introuvable.
        /// base.apk est toujours placé en premier.
        /// </summary>
        public async Task<List<string>> GetApkPathsAsync(string serial, string packageName)
        {
            var pathRes = await _adbManager.ExecuteAsync($"-s {serial} shell pm path {AdbManager.QuoteRemoteArg(packageName)}");
            if (!pathRes.IsSuccess) return new List<string>();
            return ParseApkPaths(pathRes.StandardOutput);
        }

        /// <summary>Fonction pure, testable : extrait les chemins de la sortie de "pm path", base.apk en premier.</summary>
        public static List<string> ParseApkPaths(string pmPathOutput)
        {
            return (pmPathOutput ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("package:"))
                .Select(l => l.Substring("package:".Length).Trim())
                .Where(p => p.Length > 0)
                .OrderBy(p => p.EndsWith("/base.apk") ? 0 : 1)
                .ToList();
        }

        public Task<AdbCommandResult> PullApkAsync(string serial, string remotePath, string localPath) =>
            _adbManager.ExecuteAsync($"-s {serial} pull {AdbManager.QuoteLocalArg(remotePath)} {AdbManager.QuoteLocalArg(localPath)}", TimeSpan.FromMinutes(5));

        public Task<AdbCommandResult> InstallApkAsync(string serial, string localApkPath) =>
            InstallApkSetAsync(serial, new[] { localApkPath });

        /// <summary>
        /// Installe un APK simple ("adb install") ou une app en plusieurs parties ("adb install-multiple" :
        /// base.apk + split_config.*.apk, obligatoire pour la plupart des apps du Play Store — installer
        /// base.apk seul échoue).
        /// </summary>
        public Task<AdbCommandResult> InstallApkSetAsync(string serial, IList<string> localApkPaths)
        {
            if (localApkPaths == null || localApkPaths.Count == 0) throw new ArgumentException("Aucun APK à installer.", nameof(localApkPaths));

            string files = string.Join(" ", localApkPaths.Select(AdbManager.QuoteLocalArg));
            string verb = localApkPaths.Count > 1 ? "install-multiple" : "install";
            return _adbManager.ExecuteAsync($"-s {serial} {verb} -r -d -t {files}", TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Résout l'ensemble des fichiers à installer à partir d'un élément déposé ou choisi :
        /// - un dossier → tous les .apk qu'il contient (base.apk en premier) ;
        /// - un .apk accompagné d'un dossier voisin "&lt;nom&gt;_splits" (format produit par l'extraction APK
        ///   de l'app) → l'APK + toutes les parties du dossier ;
        /// - sinon → l'APK seul.
        /// Liste vide si rien d'installable.
        /// </summary>
        public static List<string> ResolveApkSet(string path)
        {
            if (string.IsNullOrEmpty(path)) return new List<string>();

            if (System.IO.Directory.Exists(path))
            {
                var inFolder = System.IO.Directory.GetFiles(path, "*.apk")
                    .OrderBy(f => string.Equals(System.IO.Path.GetFileName(f), "base.apk", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Dossier "<nom>_splits" déposé seul : l'APK principal "<nom>.apk" est juste à côté, on l'ajoute
                // (les parties complémentaires seules ne s'installent pas).
                string folder = path.TrimEnd('\\', '/');
                string folderName = System.IO.Path.GetFileName(folder);
                if (folderName.EndsWith("_splits", StringComparison.OrdinalIgnoreCase))
                {
                    string siblingBase = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(folder) ?? "",
                        folderName.Substring(0, folderName.Length - "_splits".Length) + ".apk");
                    if (System.IO.File.Exists(siblingBase)) inFolder.Insert(0, siblingBase);
                }
                return inFolder;
            }

            if (!System.IO.File.Exists(path) || !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                return new List<string>();

            var set = new List<string> { path };
            string splitFolder = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(path) ?? "",
                System.IO.Path.GetFileNameWithoutExtension(path) + "_splits");

            if (System.IO.Directory.Exists(splitFolder))
            {
                set.AddRange(System.IO.Directory.GetFiles(splitFolder, "*.apk").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            return set;
        }

        /// <summary>Fonction pure, testable : explication en français des codes d'échec d'installation les plus fréquents (null si inconnu).</summary>
        public static string DescribeInstallFailure(string adbOutput)
        {
            string o = adbOutput ?? "";
            if (o.Contains("INSTALL_FAILED_VERIFICATION_FAILURE"))
                return "Installation refusée par la vérification d'apps du terminal (Play Protect / « Vérifier les applis via USB » dans les Options développeur, ou politique MDM). Désactivez cette vérification sur le terminal ou installez via le MDM.";
            if (o.Contains("INSTALL_FAILED_MISSING_SPLIT"))
                return "Cette app est découpée en plusieurs APK : installez l'APK avec son dossier « _splits » (ou déposez le dossier contenant tous les APK).";
            if (o.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE"))
                return "Une version signée avec un autre certificat est déjà installée : désinstallez-la d'abord.";
            if (o.Contains("INSTALL_FAILED_VERSION_DOWNGRADE"))
                return "Une version plus récente est déjà installée et le terminal refuse la rétrogradation.";
            if (o.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE"))
                return "Espace de stockage insuffisant sur le terminal.";
            if (o.Contains("INSTALL_FAILED_USER_RESTRICTED"))
                return "Installation bloquée par une restriction utilisateur ou MDM sur le terminal.";
            if (o.Contains("INSTALL_FAILED_NO_MATCHING_ABIS"))
                return "APK incompatible avec le processeur du terminal (architecture différente).";
            if (o.Contains("INSTALL_FAILED_OLDER_SDK"))
                return "APK prévu pour une version d'Android plus récente que celle du terminal.";
            return null;
        }

        // Réversible (contrairement à Vider/Désinstaller) : on peut toujours réactiver un paquet désactivé.
        public Task<AdbCommandResult> EnableAsync(string serial, string packageName) =>
            _adbManager.ExecuteAsync($"-s {serial} shell pm enable {AdbManager.QuoteRemoteArg(packageName)}");

        public Task<AdbCommandResult> DisableAsync(string serial, string packageName) =>
            _adbManager.ExecuteAsync($"-s {serial} shell pm disable-user --user 0 {AdbManager.QuoteRemoteArg(packageName)}");

        /// <summary>Résout version/SDK cible/source d'installation d'un paquet, à la demande (pas pour toute la liste : trop lent en masse).</summary>
        public async Task<PackageDetails> GetPackageDetailsAsync(string serial, string packageName)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys package {AdbManager.QuoteRemoteArg(packageName)}");
            return res.IsSuccess ? ParsePackageDetails(res.StandardOutput) : null;
        }

        private static readonly Regex VersionNameRegex = new Regex(@"versionName=(\S+)");
        private static readonly Regex VersionCodeRegex = new Regex(@"versionCode=(\d+)");
        private static readonly Regex TargetSdkRegex = new Regex(@"targetSdk=(\d+)");
        private static readonly Regex InstallerRegex = new Regex(@"installerPackageName=(\S+)");

        /// <summary>Fonction pure, testable : extrait les champs utiles d'une sortie "dumpsys package &lt;pkg&gt;".</summary>
        public static PackageDetails ParsePackageDetails(string dumpsysOutput)
        {
            string output = dumpsysOutput ?? "";
            var mVersionName = VersionNameRegex.Match(output);
            var mVersionCode = VersionCodeRegex.Match(output);
            var mTargetSdk = TargetSdkRegex.Match(output);
            var mInstaller = InstallerRegex.Match(output);

            return new PackageDetails
            {
                VersionName = mVersionName.Success ? mVersionName.Groups[1].Value : null,
                VersionCode = mVersionCode.Success ? mVersionCode.Groups[1].Value : null,
                TargetSdk = mTargetSdk.Success ? mTargetSdk.Groups[1].Value : null,
                InstallerPackage = mInstaller.Success ? mInstaller.Groups[1].Value : null
            };
        }
    }

    /// <summary>Détails d'un paquet résolus à la demande via "dumpsys package". Tout champ peut être null si absent de la sortie.</summary>
    public class PackageDetails
    {
        public string VersionName { get; set; }
        public string VersionCode { get; set; }
        public string TargetSdk { get; set; }
        public string InstallerPackage { get; set; }
    }

    /// <summary>Critères de filtrage de la liste des paquets, capturés une fois depuis l'UI.</summary>
    public struct PackageFilterCriteria
    {
        /// <summary>"Utilisateur" ou "Système". Null/vide = tous types.</summary>
        public string TypeFilter;

        /// <summary>Ne garder que les paquets désactivés.</summary>
        public bool OnlyDisabled;

        /// <summary>Texte recherché (insensible à la casse) dans le nom du paquet.</summary>
        public string SearchText;
    }

    /// <summary>Logique de filtrage d'une liste de paquets. Fonction pure, testable isolément.</summary>
    public static class PackageFilter
    {
        public static List<AndroidPackageItem> Apply(IEnumerable<AndroidPackageItem> packages, PackageFilterCriteria criteria)
        {
            var filtered = packages ?? Enumerable.Empty<AndroidPackageItem>();

            if (!string.IsNullOrEmpty(criteria.TypeFilter))
                filtered = filtered.Where(p => p.Type == criteria.TypeFilter);

            if (criteria.OnlyDisabled)
                filtered = filtered.Where(p => p.Status == "Désactivé");

            if (!string.IsNullOrEmpty(criteria.SearchText))
            {
                string search = criteria.SearchText.ToLower();
                filtered = filtered.Where(p => p.PackageName.ToLower().Contains(search));
            }

            return filtered.ToList();
        }
    }
}

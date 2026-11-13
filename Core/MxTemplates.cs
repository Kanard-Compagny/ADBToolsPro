using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ADBToolsNew.Core
{
    public enum MxFieldKind { Text, Password, Choice, DeviceFile, Folder }

    /// <summary>Un champ du formulaire d'un modèle.</summary>
    public sealed class MxField
    {
        public string Key = "";
        public string Label = "";
        public MxFieldKind Kind = MxFieldKind.Text;
        public string Default = "";
        public string Hint = "";
        /// <summary>Choix (valeur, libellé) pour Kind = Choice.</summary>
        public (string Value, string Label)[] Options = new (string, string)[0];
        /// <summary>Champ affiché seulement si cette condition sur les autres valeurs est vraie.</summary>
        public Func<IDictionary<string, string>, bool> VisibleWhen;
        /// <summary>Contrôle de la valeur : message d'erreur, ou null si elle est bonne.</summary>
        public Func<string, IDictionary<string, string>, string> Check;
        /// <summary>DeviceFile : extensions proposées à l'ouverture (« *.apk »).</summary>
        public string FileFilter = "";

        public bool IsVisible(IDictionary<string, string> v) => VisibleWhen == null || VisibleWhen(v);
    }

    /// <summary>Un modèle d'étape MX : formulaire, XML produit, et reconnaissance des étapes équivalentes déjà présentes.</summary>
    public sealed class MxTemplate
    {
        public string Id = "";
        public string Group = "";
        public string Label = "";
        public string Description = "";
        public string MinMx = "4.2";
        public string Warning = "";
        public List<MxField> Fields = new List<MxField>();
        public Func<IDictionary<string, string>, XElement> Build;
        public Func<MxStep, bool> Matches;
        /// <summary>À placer en dernier : rien ne s'exécute après (redémarrage).</summary>
        public bool MustBeLast;

        public Dictionary<string, string> Defaults() => Fields.ToDictionary(f => f.Key, f => f.Default);
    }

    /// <summary>
    /// Catalogue des modèles d'étapes MX (Zebra) de l'éditeur XML. Noms et valeurs des paramètres repris de la
    /// documentation MX de Zebra (techdocs.zebra.com) et, pour le Wi-Fi, d'un profil StageNow 13.5 appliqué avec
    /// succès sur TC53. Aucun modèle n'efface ni ne réinitialise le terminal : ces étapes restent possibles à la main.
    /// </summary>
    public static class MxTemplates
    {
        public const string GroupWifi = "Wi-Fi";
        public const string GroupApps = "Applications";
        public const string GroupFiles = "Fichiers et EHS";
        public const string GroupSystem = "Écran, heure et système";
        public static readonly string[] Groups = { GroupWifi, GroupApps, GroupFiles, GroupSystem };

        public const string Staging = "/sdcard/Download/";
        public const string EhsPackage = "com.symbol.enterprisehomescreen";
        public const string EhsConfig = "/enterprise/usr/enterprisehomescreen.xml";

        // ------------------------------------------------------------------ Aides

        private static XElement Parm(string name, string value) => new XElement("parm", new XAttribute("name", name), new XAttribute("value", value ?? ""));
        private static XElement Char(string type, string version, params object[] content) =>
            version == null ? new XElement("characteristic", new XAttribute("type", type), content)
                            : new XElement("characteristic", new XAttribute("type", type), new XAttribute("version", version), content);

        private static string V(IDictionary<string, string> v, string key) => v != null && v.TryGetValue(key, out string s) ? (s ?? "").Trim() : "";

        /// <summary>Valeur d'un paramètre dans le XML d'une étape (premier trouvé, insensible à la casse).</summary>
        public static string StepParm(MxStep s, string name)
        {
            try
            {
                return XElement.Parse(s.Xml).Descendants("parm")
                    .FirstOrDefault(x => string.Equals((string)x.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value ?? "";
            }
            catch { return ""; }
        }

        private static bool Is(MxStep s, string type) => string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase);
        private static bool ParmIs(MxStep s, string name, params string[] values) => values.Any(v => string.Equals(StepParm(s, name), v, StringComparison.OrdinalIgnoreCase));

        private static string Required(string value, string what) => string.IsNullOrWhiteSpace(value) ? Loc.F("{0} : à remplir", what) : null;

        private static readonly Regex PackageRx = new Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$");
        private static string CheckPackage(string v, IDictionary<string, string> all) =>
            Required(v, Loc.T("Nom du paquet")) ?? (PackageRx.IsMatch(v.Trim()) ? null : Loc.T("Nom du paquet : de la forme com.societe.appli"));
        private static string CheckDevicePath(string v, string what) =>
            Required(v, what) ?? (v.Trim().StartsWith("/") ? null : Loc.F("{0} : chemin du terminal, commençant par /", what));

        private static MxField Package(string label = "Nom du paquet") => new MxField
        {
            Key = "package", Label = label, Hint = "com.societe.appli", Check = CheckPackage,
        };

        // ------------------------------------------------------------------ Catalogue

        public static readonly List<MxTemplate> All = new List<MxTemplate>
        {
            // ---------------- Wi-Fi
            new MxTemplate
            {
                Id = "wifi-add", Group = GroupWifi, Label = "Ajouter un réseau", MinMx = "10.2",
                Description = "Le réseau est ajouté et le terminal s'y connecte s'il est à portée.",
                Warning = "Le mot de passe est écrit en clair dans le XML, comme les réseaux Wi-Fi du Master.",
                Fields =
                {
                    new MxField { Key = "ssid", Label = "Nom du réseau (SSID)", Check = (v, a) => Required(v, Loc.T("Nom du réseau"))
                        ?? (v.Length > 32 ? Loc.T("Nom du réseau : 32 caractères au plus") : Regex.IsMatch(v, "[&\"<>]") ? Loc.T("Nom du réseau : & \" < > ne sont pas acceptés par MX") : null) },
                    new MxField { Key = "security", Label = "Sécurité", Kind = MxFieldKind.Choice, Default = "wpa2",
                        Options = new[] { ("wpa2", "WPA2 personnel"), ("wpa3", "WPA3 personnel"), ("open", "Ouvert") } },
                    new MxField { Key = "password", Label = "Mot de passe", Kind = MxFieldKind.Password, VisibleWhen = v => V(v, "security") != "open",
                        Check = (v, a) => V(a, "security") == "open" ? null : (v ?? "").Length < 8 || v.Length > 63 ? Loc.T("Mot de passe : de 8 à 63 caractères") : null },
                },
                Build = v =>
                {
                    string sec = V(v, "security");
                    var profile = Char("network-profile", null, Parm("SSID", V(v, "ssid")));
                    if (sec == "open") profile.Add(Parm("SecurityMode", "0"));
                    else
                    {
                        profile.Add(Parm("SecurityMode", "1"));
                        profile.Add(Parm("WPAModePersonal", sec == "wpa3" ? "5" : "3"));
                        if (sec == "wpa2") profile.Add(Char("auth-details", null, Char("encryption-details", null, Parm("EncryptionWPA2Personal", "3"))));
                        profile.Add(Char("key-details", null, Parm("KeyType", "Passphrase"), Parm("ProtectKey", "0"), Parm("PassphraseWPAClear", v.TryGetValue("password", out var pw) ? pw : "")));
                    }
                    profile.Add(Parm("UseDHCP", "1"));
                    profile.Add(Parm("UseProxy", "0"));
                    return Char("Wi-Fi", "10.2", Parm("NetworkAction", "Add"), profile);
                },
                Matches = s => (Is(s, "Wi-Fi") || Is(s, "WifiMgr")) && ParmIs(s, "NetworkAction", "Add", "1"),
            },
            new MxTemplate
            {
                Id = "wifi-remove", Group = GroupWifi, Label = "Retirer un réseau", MinMx = "4.2",
                Description = "Le réseau enregistré sous ce nom est oublié par le terminal.",
                Fields = { new MxField { Key = "ssid", Label = "Nom du réseau (SSID)", Check = (v, a) => Required(v, Loc.T("Nom du réseau")) } },
                Build = v => Char("Wi-Fi", "4.2", Parm("NetworkAction", "Remove"), Char("network-profile", null, Parm("SSID", V(v, "ssid")))),
                Matches = s => (Is(s, "Wi-Fi") || Is(s, "WifiMgr")) && ParmIs(s, "NetworkAction", "Remove", "2"),
            },

            // ---------------- Applications
            new MxTemplate
            {
                Id = "app-install", Group = GroupApps, Label = "Installer une appli", MinMx = "4.2",
                Description = "Installe ou met à jour l'appli depuis un fichier .apk. Choisi sur le PC, il est joint au Master et déposé sur le terminal avant l'étape.",
                Fields =
                {
                    new MxField { Key = "apk", Label = "Fichier .apk", Kind = MxFieldKind.DeviceFile, FileFilter = "*.apk", Hint = Staging + "appli.apk",
                        Check = (v, a) => CheckDevicePath(v, Loc.T("Fichier .apk")) },
                    new MxField { Key = "action", Label = "Si l'appli est déjà là", Kind = MxFieldKind.Choice, Default = "Upgrade",
                        Options = new[] { ("Upgrade", "Mettre à jour"), ("Install", "Installer seulement") } },
                },
                Build = v => Char("AppMgr", "4.2", Parm("Action", V(v, "action") == "Install" ? "Install" : "Upgrade"), Parm("APK", V(v, "apk"))),
                Matches = s => Is(s, "AppMgr") && ParmIs(s, "Action", "Install", "Upgrade"),
            },
            new MxTemplate
            {
                Id = "app-uninstall", Group = GroupApps, Label = "Désinstaller une appli", MinMx = "4.2",
                Description = "Retire une appli installée par l'utilisateur.",
                Fields = { Package() },
                Build = v => Char("AppMgr", "4.2", Parm("Action", "Uninstall"), Parm("Package", V(v, "package"))),
                Matches = s => Is(s, "AppMgr") && ParmIs(s, "Action", "Uninstall"),
            },
            new MxTemplate
            {
                Id = "app-enable", Group = GroupApps, Label = "Activer ou désactiver une appli", MinMx = "4.2",
                Description = "Pour les applis système : une appli désactivée disparaît du terminal sans être désinstallée.",
                Fields =
                {
                    Package(),
                    new MxField { Key = "action", Label = "Action", Kind = MxFieldKind.Choice, Default = "DisableApplication",
                        Options = new[] { ("DisableApplication", "Désactiver"), ("EnableApplication", "Activer") } },
                },
                Build = v => Char("AppMgr", "4.2", Parm("Action", V(v, "action") == "EnableApplication" ? "EnableApplication" : "DisableApplication"), Parm("Package", V(v, "package"))),
                Matches = s => Is(s, "AppMgr") && ParmIs(s, "Action", "EnableApplication", "DisableApplication"),
            },
            new MxTemplate
            {
                Id = "app-launcher", Group = GroupApps, Label = "Lanceur par défaut", MinMx = "4.2",
                Description = "L'appli qui s'ouvre avec la touche Accueil.",
                Fields = { Package("Paquet du lanceur") },
                Build = v => Char("AppMgr", "4.2", Parm("Action", "SetDefaultLauncher"), Parm("Package", V(v, "package"))),
                Matches = s => Is(s, "AppMgr") && ParmIs(s, "Action", "SetDefaultLauncher") && !StepParm(s, "Package").Equals(EhsPackage, StringComparison.OrdinalIgnoreCase),
            },

            // ---------------- Fichiers et EHS
            new MxTemplate
            {
                Id = "file-copy", Group = GroupFiles, Label = "Déposer un fichier", MinMx = "4.2",
                Description = "Le fichier choisi sur le PC est joint au Master, déposé sur le terminal puis placé par MX dans le dossier voulu, y compris les dossiers protégés (/enterprise…).",
                Fields =
                {
                    new MxField { Key = "source", Label = "Fichier", Kind = MxFieldKind.DeviceFile, Hint = Staging + "fichier",
                        Check = (v, a) => CheckDevicePath(v, Loc.T("Fichier")) },
                    new MxField { Key = "folder", Label = "Dossier du terminal", Kind = MxFieldKind.Folder, Default = "/enterprise/usr/",
                        Check = (v, a) => CheckDevicePath(v, Loc.T("Dossier du terminal")) },
                },
                Build = v => FileCopy(V(v, "source"), DevicePathMemory.Normalize(V(v, "folder")) + FileName(V(v, "source"))),
                Matches = s => Is(s, "FileMgr") && ParmIs(s, "SourceAccessMethod", "2") && !StepParm(s, "TargetPathAndFileName").Equals(EhsConfig, StringComparison.OrdinalIgnoreCase),
            },
            new MxTemplate
            {
                Id = "file-download", Group = GroupFiles, Label = "Télécharger un fichier (adresse web)", MinMx = "4.2",
                Description = "Le terminal télécharge lui-même le fichier : il doit joindre le serveur au moment d'appliquer.",
                Fields =
                {
                    new MxField { Key = "url", Label = "Adresse du fichier", Hint = "https://serveur/fichier.apk",
                        Check = (v, a) => Required(v, Loc.T("Adresse du fichier")) ?? (Regex.IsMatch(v.Trim(), "^(https?|ftp)://", RegexOptions.IgnoreCase) ? null : Loc.T("Adresse du fichier : commence par http://, https:// ou ftp://")) },
                    new MxField { Key = "target", Label = "Enregistré sur le terminal sous", Hint = Staging + "fichier.apk",
                        Check = (v, a) => CheckDevicePath(v, Loc.T("Chemin sur le terminal")) },
                },
                Build = v => Char("FileMgr", "4.2", Parm("FileAction", "1"), Char("file-details", null,
                    Parm("TargetAccessMethod", "2"), Parm("TargetPathAndFileName", V(v, "target")), Parm("IfDuplicate", "1"),
                    Parm("SourceAccessMethod", "1"), Parm("SourceURI", V(v, "url")))),
                Matches = s => Is(s, "FileMgr") && ParmIs(s, "SourceAccessMethod", "1"),
            },
            new MxTemplate
            {
                Id = "ehs-config", Group = GroupFiles, Label = "Déposer la configuration EHS", MinMx = "4.2",
                Description = "Le fichier enterprisehomescreen.xml choisi sur le PC est placé dans /enterprise/usr/, où Enterprise Home Screen le lit.",
                Fields =
                {
                    new MxField { Key = "source", Label = "Fichier enterprisehomescreen.xml", Kind = MxFieldKind.DeviceFile, FileFilter = "*.xml", Hint = Staging + "enterprisehomescreen.xml",
                        Check = (v, a) => CheckDevicePath(v, Loc.T("Fichier")) },
                },
                Build = v => FileCopy(V(v, "source"), EhsConfig),
                Matches = s => Is(s, "FileMgr") && StepParm(s, "TargetPathAndFileName").Equals(EhsConfig, StringComparison.OrdinalIgnoreCase),
            },
            new MxTemplate
            {
                Id = "ehs-launcher", Group = GroupFiles, Label = "EHS comme lanceur", MinMx = "4.2",
                Description = "Enterprise Home Screen devient l'écran d'accueil (l'appli EHS doit être installée).",
                Build = v => Char("AppMgr", "4.2", Parm("Action", "SetDefaultLauncher"), Parm("Package", EhsPackage)),
                Matches = s => Is(s, "AppMgr") && ParmIs(s, "Action", "SetDefaultLauncher") && StepParm(s, "Package").Equals(EhsPackage, StringComparison.OrdinalIgnoreCase),
            },

            // ---------------- Écran, heure et système
            new MxTemplate
            {
                Id = "display-timeout", Group = GroupSystem, Label = "Délai de mise en veille", MinMx = "4.2",
                Fields =
                {
                    new MxField { Key = "seconds", Label = "Écran éteint après", Kind = MxFieldKind.Choice, Default = "300",
                        Options = new[] { ("15", "15 s"), ("30", "30 s"), ("60", "1 min"), ("120", "2 min"), ("300", "5 min"), ("600", "10 min"), ("1800", "30 min") } },
                },
                Build = v => Char("DisplayMgr", "4.2", Parm("TimeoutInterval", V(v, "seconds"))),
                Matches = s => Is(s, "DisplayMgr") && StepParm(s, "TimeoutInterval").Length > 0,
            },
            new MxTemplate
            {
                Id = "clock", Group = GroupSystem, Label = "Date, heure et fuseau", MinMx = "4.2",
                Description = "Heure prise sur le réseau (NTP) et fuseau horaire.",
                Fields =
                {
                    new MxField { Key = "auto", Label = "Heure automatique", Kind = MxFieldKind.Choice, Default = "true", Options = new[] { ("true", "Oui"), ("false", "Non") } },
                    new MxField { Key = "ntp", Label = "Serveur de temps (facultatif)", Hint = "pool.ntp.org", VisibleWhen = v => V(v, "auto") == "true" },
                    new MxField { Key = "zone", Label = "Fuseau horaire", Default = "Europe/Paris", Hint = "Europe/Paris, GMT+01:00…",
                        Check = (v, a) => Required(v, Loc.T("Fuseau horaire")) },
                },
                Build = v =>
                {
                    var c = Char("Clock", "4.2", Parm("AutoTime", V(v, "auto") == "false" ? "false" : "true"));
                    if (V(v, "auto") != "false" && V(v, "ntp").Length > 0) c.Add(Char("AutoTimeDetails", null, Parm("NTPServer", V(v, "ntp"))));
                    c.Add(Parm("TimeZone", V(v, "zone")));
                    return c;
                },
                Matches = s => Is(s, "Clock"),
            },
            new MxTemplate
            {
                Id = "locale", Group = GroupSystem, Label = "Langue du terminal", MinMx = "5.0",
                Description = "Ce qu'ADB seul ne peut pas faire sans root.",
                Fields =
                {
                    new MxField { Key = "locale", Label = "Langue", Kind = MxFieldKind.Choice, Default = "fr_FR",
                        Options = new[] { ("fr_FR", "Français (France)"), ("fr_CA", "Français (Canada)"), ("en_US", "Anglais (États-Unis)"), ("en_GB", "Anglais (Royaume-Uni)"),
                                          ("de_DE", "Allemand"), ("es_ES", "Espagnol"), ("it_IT", "Italien"), ("pt_PT", "Portugais") } },
                },
                Build = v => Char("UiMgr", "5.0", Parm("CurrentLocale", V(v, "locale"))),
                Matches = s => Is(s, "UiMgr") && StepParm(s, "CurrentLocale").Length > 0,
            },
            new MxTemplate
            {
                Id = "radios", Group = GroupSystem, Label = "Bluetooth, NFC et GPS", MinMx = "4.3",
                Description = "Allume ou éteint les radios. « Ne pas changer » laisse le réglage du terminal.",
                Fields =
                {
                    new MxField { Key = "bt", Label = "Bluetooth", Kind = MxFieldKind.Choice, Default = "0", Options = Radio() },
                    new MxField { Key = "nfc", Label = "NFC (Android 10 et plus)", Kind = MxFieldKind.Choice, Default = "0", Options = Radio() },
                    new MxField { Key = "gps", Label = "GPS", Kind = MxFieldKind.Choice, Default = "0", Options = Radio(),
                        Check = (v, a) => V(a, "bt") == "0" && V(a, "nfc") == "0" && V(a, "gps") == "0" ? Loc.T("Choisissez au moins une radio à allumer ou éteindre") : null },
                },
                Build = v =>
                {
                    var c = Char("WirelessMgr", "4.3");
                    if (V(v, "bt") != "0") c.Add(Parm("BluetoothState", V(v, "bt")));
                    if (V(v, "nfc") != "0") c.Add(Parm("NFCState", V(v, "nfc")));
                    if (V(v, "gps") != "0") c.Add(Parm("GPSState", V(v, "gps")));
                    return c;
                },
                Matches = s => Is(s, "WirelessMgr"),
            },
            new MxTemplate
            {
                Id = "reboot", Group = GroupSystem, Label = "Redémarrer le terminal à la fin", MinMx = "4.2", MustBeLast = true,
                Description = "Toujours placé en dernier : rien ne s'exécute après un redémarrage. Le bilan ne reçoit pas de réponse pour cette étape.",
                Build = v => Char("PowerMgr", "4.2", Parm("ResetAction", "4")),
                Matches = IsReboot,
            },
        };

        private static (string, string)[] Radio() => new[] { ("0", "Ne pas changer"), ("1", "Allumer"), ("2", "Éteindre") };

        public static bool IsReboot(MxStep s) => Is(s, "PowerMgr") && ParmIs(s, "ResetAction", "4");

        private static string FileName(string path) => (path ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";

        /// <summary>Copie MX d'un fichier déjà sur le terminal vers son dossier final (protégé ou non).</summary>
        private static XElement FileCopy(string source, string target) =>
            Char("FileMgr", "4.2", Parm("FileAction", "1"), Char("file-details", null,
                Parm("TargetAccessMethod", "2"), Parm("TargetPathAndFileName", target), Parm("IfDuplicate", "1"),
                Parm("SourceAccessMethod", "2"), Parm("SourcePathAndFileName", source)));

        public static MxTemplate Find(string id) => All.FirstOrDefault(t => t.Id == id);

        // ------------------------------------------------------------------ Formulaire → XML

        /// <summary>Premier problème des valeurs saisies (champs visibles seulement), ou null.</summary>
        public static string Validate(MxTemplate t, IDictionary<string, string> values)
        {
            foreach (var f in t.Fields.Where(f => f.IsVisible(values)))
            {
                string v = values != null && values.TryGetValue(f.Key, out string s) ? s ?? "" : "";
                string err = f.Check?.Invoke(f.Kind == MxFieldKind.Password ? v : v.Trim(), values);
                if (err != null) return err;
            }
            return null;
        }

        /// <summary>XML de l'étape, indenté de deux espaces par niveau, sans indentation de départ.</summary>
        public static string BuildXml(MxTemplate t, IDictionary<string, string> values)
        {
            var e = t.Build(values ?? t.Defaults());
            return e.ToString().Replace("\r\n", "\n");
        }

        /// <summary>Combien d'étapes du profil font déjà ce que fait le modèle.</summary>
        public static int CountIn(MxTemplate t, MxProfile p) => p == null ? 0 : p.Steps.Count(s => { try { return t.Matches(s); } catch { return false; } });

        // ------------------------------------------------------------------ Insertion dans le texte

        /// <summary>Profil vide prêt à recevoir des étapes.</summary>
        public static string EmptyProfile() => "<wap-provisioningdoc>\r\n</wap-provisioningdoc>\r\n";

        public sealed class InsertResult
        {
            public string Xml = "";
            /// <summary>Première ligne insérée (1 = première ligne du texte) et nombre de lignes.</summary>
            public int FirstLine;
            public int LineCount;
        }

        /// <summary>
        /// Insère l'étape dans le texte du profil sans toucher au reste (mise en forme et commentaires gardés).
        /// <paramref name="afterLine"/> = 0 : à la fin du profil, mais avant un redémarrage final ; sinon après l'étape
        /// qui contient cette ligne (ou avant la première étape si la ligne la précède). Un redémarrage va toujours à la fin.
        /// </summary>
        public static InsertResult Insert(string xml, string fragment, int afterLine, bool mustBeLast = false)
        {
            string nl = (xml ?? "").Contains("\r\n") ? "\r\n" : "\n";
            var lines = (xml ?? "").Replace("\r\n", "\n").Split('\n').ToList();
            var p = StageNowReader.Read(xml, true);
            if (!p.Ok) throw new InvalidOperationException(p.Error);
            var steps = p.Steps;

            string root = XDocument.Parse(xml).Root.Name.LocalName;
            int closing = ClosingLine(lines, root);
            // Fermeture de la racine sur la même ligne que du contenu (« …</characteristic></wap-provisioningdoc> ») : coupée en deux.
            int cutAt = lines[closing].LastIndexOf("</" + root, StringComparison.Ordinal);
            if (cutAt > 0 && lines[closing].Substring(0, cutAt).Trim().Length > 0)
            {
                string l = lines[closing];
                lines[closing] = l.Substring(0, cutAt).TrimEnd();
                lines.Insert(closing + 1, LeadingWhitespace(l) + l.Substring(cutAt));
                closing++;
            }

            // Ligne (index 0) avant laquelle insérer : à la fin, ou avant l'étape qui suit le curseur.
            var next = afterLine > 0 && !mustBeLast ? steps.FirstOrDefault(s => s.Line > afterLine) : null;
            int at = next != null ? LeadingComments(lines, next.Line - 1) : closing;
            // Rien après un redémarrage final (sauf un autre redémarrage).
            if (!mustBeLast && steps.Count > 0 && IsReboot(steps.Last()))
                at = Math.Min(at, LeadingComments(lines, steps.Last().Line - 1));

            string indent = steps.Count > 0 ? LeadingWhitespace(lines[steps[0].Line - 1]) : LeadingWhitespace(lines[closing]) + "  ";
            var add = fragment.Replace("\r\n", "\n").Split('\n').Select(l => indent + l).ToList();
            lines.InsertRange(at, add);
            return new InsertResult { Xml = string.Join(nl, lines), FirstLine = at + 1, LineCount = add.Count };
        }

        /// <summary>Index de la ligne qui porte la balise fermante de la racine.</summary>
        private static int ClosingLine(List<string> lines, string root)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
                if (lines[i].Contains("</" + root)) return i;
            return lines.Count - 1;
        }

        /// <summary>Remonte au-dessus des commentaires collés à une étape (« SettingID=… » de StageNow).</summary>
        private static int LeadingComments(List<string> lines, int index)
        {
            while (index > 0 && Regex.IsMatch(lines[index - 1], @"^\s*<!--.*-->\s*$")) index--;
            return index;
        }

        private static string LeadingWhitespace(string s) => Regex.Match(s ?? "", @"^\s*").Value;
    }
}

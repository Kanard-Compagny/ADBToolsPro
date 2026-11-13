using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Bloc « réglages constructeur » d'un Master : un fichier propre à une marque (aujourd'hui un profil StageNow Zebra),
    /// appliqué seulement sur les terminaux de cette marque. Le moteur est commun ; chaque marque a son module
    /// (<see cref="VendorModules"/>) qui sait reconnaître ses terminaux et lire son format.
    /// </summary>
    public sealed class MasterVendorBlock
    {
        public string Brand { get; set; } = VendorModules.Zebra;
        /// <summary>Type de contenu : « stagenow » (profil MX exporté pour MDM).</summary>
        public string Kind { get; set; } = "stagenow";
        public string Name { get; set; } = "";
        public string SourceFile { get; set; } = "";
        public DateTime ImportedAt { get; set; } = DateTime.Now;
        /// <summary>XML actuel (modifiable dans l'éditeur) et XML importé, gardé pour « Revenir à l'original ».</summary>
        public string Xml { get; set; } = "";
        public string OriginalXml { get; set; } = "";
        public bool Include { get; set; } = true;
        /// <summary>Étapes décochées (étapes ordinaires) et étapes sensibles cochées, par clé d'étape (voir MxStep.Key).</summary>
        public List<string> Unchecked { get; set; } = new List<string>();
        public List<string> CheckedSensitive { get; set; } = new List<string>();
        /// <summary>Fichiers joints : déposés sur le terminal au chemin que le profil lit, avant d'appliquer.</summary>
        public List<MasterVendorFile> Files { get; set; } = new List<MasterVendorFile>();

        public bool IsChecked(MxStep s) => s.Sensitive ? CheckedSensitive.Contains(s.Key) : !Unchecked.Contains(s.Key);

        public void SetChecked(MxStep s, bool on)
        {
            Unchecked.Remove(s.Key);
            CheckedSensitive.Remove(s.Key);
            if (s.Sensitive && on) CheckedSensitive.Add(s.Key);
            if (!s.Sensitive && !on) Unchecked.Add(s.Key);
        }
    }

    public sealed class MasterVendorFile
    {
        /// <summary>Chemin que le profil lit sur le terminal (ex. /sdcard/EHS.apk).</summary>
        public string DevicePath { get; set; } = "";
        /// <summary>Fichier dans le Master (chemin relatif au dossier du Master), vide tant qu'il n'est pas joint.</summary>
        public string Local { get; set; } = "";
        /// <summary>Fichier que le profil fait télécharger au terminal depuis un lien (l'étape est sautée s'il est fourni).</summary>
        public bool Download { get; set; }
        /// <summary>Fourni par l'apk d'une appli du Master (onglet Applications) plutôt que par un fichier joint.</summary>
        public string AppPackage { get; set; } = "";
        /// <summary>Téléchargement laissé au terminal, en connaissance de cause (plus d'avertissement).</summary>
        public bool KeepDownload { get; set; }

        public bool Provided => !string.IsNullOrEmpty(Local) || !string.IsNullOrEmpty(AppPackage);
    }

    /// <summary>Une étape d'un profil MX : un élément « characteristic » de premier niveau (un gestionnaire MX).</summary>
    public sealed class MxStep
    {
        public string Type = "";
        public string Version = "";
        public int Line;
        public string Xml = "";
        public string Theme = "";
        public string Label = "";
        public bool Sensitive;
        public string SensitiveReason = "";
        public bool Recognized = true;
        public string Warning = "";
        public string Key = "";
        public List<string> DeviceFiles = new List<string>();
        /// <summary>Fichier que l'étape fait télécharger au terminal depuis un lien (FileMgr + SourceURI) : chemin d'arrivée.</summary>
        public string DownloadTarget = "";
        /// <summary>Lien chiffré par StageNow (illisible ici) : on ne sait pas où le terminal va le chercher.</summary>
        public bool DownloadEncrypted;
    }

    /// <summary>Résultat d'une fusion de profils.</summary>
    public sealed class MxMergeResult
    {
        public string Xml = "";
        public int Added;
        public int Duplicates;
        /// <summary>Gestionnaires de réglages présents dans les deux profils (la dernière valeur l'emporte).</summary>
        public List<string> SameTypes = new List<string>();
    }

    /// <summary>Résultat de la lecture d'un profil : étapes, ou erreur avec sa ligne.</summary>
    public sealed class MxProfile
    {
        public List<MxStep> Steps = new List<MxStep>();
        public string Name = "";
        public string Error = "";
        public int ErrorLine;
        public string MinVersion = "";
        public bool Ok => Error.Length == 0;
    }

    public static class VendorModules
    {
        public const string Zebra = "Zebra";

        /// <summary>Marque d'un terminal d'après ro.product.manufacturer / ro.product.brand.</summary>
        public static string BrandOf(string manufacturer, string brand = "")
        {
            string s = ((manufacturer ?? "") + " " + (brand ?? "")).ToLowerInvariant();
            if (s.Contains("zebra") || s.Contains("symbol") || s.Contains("motorola solutions")) return Zebra;
            if (s.Contains("honeywell") || s.Contains("intermec")) return "Honeywell";
            if (s.Contains("datalogic")) return "Datalogic";
            if (s.Contains("samsung")) return "Samsung";
            return (manufacturer ?? "").Trim();
        }

        /// <summary>« 13.4.0.10 » ≥ « 11.3 » ? Comparaison champ par champ (absent = 0).</summary>
        public static int CompareVersions(string a, string b)
        {
            var x = Regex.Matches(a ?? "", @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
            var y = Regex.Matches(b ?? "", @"\d+").Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
            for (int i = 0; i < Math.Max(x.Count, y.Count); i++)
            {
                int p = i < x.Count ? x[i] : 0, q = i < y.Count ? y[i] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return 0;
        }

        /// <summary>versionName de « dumpsys package com.symbol.mxmf » (version MX du terminal), vide sinon.</summary>
        public static string ParseMxVersion(string dumpsys)
        {
            var m = Regex.Match(dumpsys ?? "", @"versionName=([0-9][0-9.]*)");
            return m.Success ? m.Groups[1].Value : "";
        }
    }

    /// <summary>
    /// Lecture d'un profil StageNow exporté pour MDM (« wap-provisioningdoc » + un « characteristic » par gestionnaire MX) :
    /// chaque gestionnaire devient une étape, avec un libellé en clair, un thème, et un repère « sensible » pour ce qui
    /// efface ou coupe l'accès (remise à zéro, chiffrement, débogage USB…).
    /// </summary>
    public static class StageNowReader
    {
        public const string ThemeWifi = "Wi-Fi";
        public const string ThemeEhs = "Enterprise Home Screen";
        public const string ThemeVelocity = "Velocity";
        public const string ThemeDataWedge = "DataWedge";
        /// <summary>Lecture des codes-barres des autres marques (Honeywell, Datalogic).</summary>
        public const string ThemeScanner = "Lecture des codes";
        public const string ThemeApps = "Applications";
        public const string ThemeFiles = "Fichiers";
        public const string ThemeScreen = "Écran et énergie";
        public const string ThemeSystem = "Système";
        public const string ThemeOther = "Autres étapes";

        public static readonly string[] ThemeOrder = { ThemeWifi, ThemeEhs, ThemeVelocity, ThemeDataWedge, ThemeScanner, ThemeApps, ThemeFiles, ThemeScreen, ThemeSystem, ThemeOther };

        private static readonly Dictionary<string, string> KnownTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Clock", "Date, heure et fuseau" }, { "UiMgr", "Interface utilisateur" }, { "AudioVolUIMgr", "Volume" },
            { "KeyMappingMgr", "Réaffectation des touches" }, { "BatteryMgr", "Batterie" }, { "BluetoothMgr", "Bluetooth" },
            { "GprsMgr", "Données mobiles (APN)" }, { "CertMgr", "Certificats" }, { "AccessMgr", "Accès aux applis et réglages" },
            { "DevAdmin", "Administrateurs du terminal" }, { "UsbMgr", "USB" }, { "EncryptMgr", "Chiffrement" },
            { "SettingsMgr", "Paramètres" }, { "PersistMgr", "Conservation après remise à zéro" }, { "Intent", "Commande Android (intent)" },
            { "WirelessMgr", "Sans-fil" }, { "ThreatMgr", "Détection de menaces" }, { "LicenseMgr", "Licences" }, { "HostsMgr", "Fichier hosts" },
            { "WorkStationMgr", "Mode station" }, { "DisplayMgr", "Écran" }, { "PowerMgr", "Alimentation" }, { "AppMgr", "Applications" },
            { "FileMgr", "Fichiers" }, { "Wi-Fi", "Wi-Fi" }, { "WifiMgr", "Wi-Fi" }, { "BrowserMgr", "Navigateur" }, { "CameraMgr", "Caméras" },
            { "PowerKeyMgr", "Menu du bouton marche" }, { "TouchMgr", "Écran tactile" }, { "ComponentMgr", "Composants" }, { "SamMgr", "Carte SAM" },
            { "AnalyticsMgr", "Statistiques" }, { "DhcpOptionMgr", "Options DHCP" }, { "ZebraHudMgr", "Affichage tête haute" }, { "GmsMgr", "Services Google" },
            { "EthernetMgr", "Ethernet" }, { "SerialPortMgr", "Port série" }, { "NotificationMgr", "Notifications" },
        };

        /// <summary>Lit un profil ; en cas de XML invalide, Error et ErrorLine disent où.</summary>
        public static MxProfile Read(string xml) => Read(xml, false);

        /// <summary>Lit un profil ; <paramref name="allowEmpty"/> : un profil sans étape (créé vide) n'est pas une erreur.</summary>
        public static MxProfile Read(string xml, bool allowEmpty)
        {
            var p = new MxProfile();
            XDocument doc;
            try { doc = XDocument.Parse(xml ?? "", LoadOptions.SetLineInfo); }
            catch (XmlException ex)
            {
                p.Error = ex.Message;
                p.ErrorLine = ex.LineNumber;
                return p;
            }
            var root = doc.Root;
            IEnumerable<XElement> items;
            if (root.Name.LocalName == "wap-provisioningdoc") items = root.Elements("characteristic");
            else if (root.Name.LocalName == "characteristic" && (string)root.Attribute("type") == "Profile")
            {
                p.Name = root.Elements("parm").FirstOrDefault(x => (string)x.Attribute("name") == "ProfileName")?.Attribute("value")?.Value ?? "";
                items = root.Elements("characteristic");
            }
            else if (root.Name.LocalName == "characteristic") items = new[] { root };
            else
            {
                p.Error = Loc.T("Ce n'est pas un profil MX : la racine doit être <wap-provisioningdoc> ou <characteristic>.");
                p.ErrorLine = ((IXmlLineInfo)root).LineNumber;
                return p;
            }
            var seen = new Dictionary<string, int>();
            // Fichiers que le profil dépose lui-même (FileMgr, téléchargés depuis une URL ou copiés) : pas à joindre
            // pour les étapes suivantes qui les lisent (ex. l'apk téléchargée puis installée).
            var produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in items)
            {
                var s = Describe(e);
                s.DeviceFiles.RemoveAll(f => produced.Contains(f));
                if (s.Type == "FileMgr")
                {
                    string target = Parm(e, "TargetPathAndFileName");
                    if (target.Length > 0) produced.Add(target);
                }
                // Clé stable : type + empreinte du contenu (+ rang si deux étapes identiques).
                string baseKey = s.Type + ":" + Hash(Normalize(s.Xml));
                seen.TryGetValue(baseKey, out int n);
                seen[baseKey] = n + 1;
                s.Key = n == 0 ? baseKey : baseKey + "#" + n;
                p.Steps.Add(s);
                if (s.Version.Length > 0 && (p.MinVersion.Length == 0 || VendorModules.CompareVersions(s.Version, p.MinVersion) > 0)) p.MinVersion = s.Version;
            }
            if (p.Steps.Count == 0 && !allowEmpty) { p.Error = Loc.T("Aucune étape MX (<characteristic>) dans ce fichier."); p.ErrorLine = 1; }
            return p;
        }

        private static string Parm(XElement e, string name) =>
            e.Descendants("parm").FirstOrDefault(x => string.Equals((string)x.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value ?? "";

        private static string FileName(string path) => (path ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";

        private static MxStep Describe(XElement e)
        {
            var s = new MxStep
            {
                Type = (string)e.Attribute("type") ?? "",
                Version = (string)e.Attribute("version") ?? "",
                Line = ((IXmlLineInfo)e).LineNumber,
                Xml = e.ToString(SaveOptions.DisableFormatting),
            };
            string text = s.Xml.ToLowerInvariant();
            string type = s.Type;
            bool ehs = text.Contains("enterprisehomescreen") || text.Contains("com.zebra.mdna.els") || Regex.IsMatch(text, @"[/""]ehs[^a-z]");
            bool velocity = text.Contains("velocity") || text.Contains("wavelink") || text.Contains("ivanti");
            bool dw = text.Contains("datawedge") || text.Contains("dwprofile");

            switch (type)
            {
                case "Wi-Fi":
                case "WifiMgr":
                    {
                        string ssid = Parm(e, "SSID");
                        s.Theme = ThemeWifi;
                        s.Label = ssid.Length > 0 ? Loc.F("Ajouter le réseau « {0} »", ssid) : Loc.T("Réglages Wi-Fi");
                        var pass = e.Descendants("parm").Where(x => ((string)x.Attribute("name") ?? "").IndexOf("passphrase", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                        if (pass.Count > 0 && pass.All(x => string.IsNullOrEmpty((string)x.Attribute("value"))))
                            s.Warning = Loc.T("mot de passe vide : à compléter dans le XML");
                        break;
                    }
                case "AppMgr":
                    {
                        string action = Parm(e, "Action"), apk = Parm(e, "APK"), pkg = Parm(e, "Package");
                        string target = apk.Length > 0 ? FileName(apk) : pkg;
                        switch (action.ToLowerInvariant())
                        {
                            case "install": s.Label = Loc.F("Installer {0}", target); break;
                            case "upgrade": s.Label = Loc.F("Mettre à jour {0}", target); break;
                            case "uninstall": s.Label = Loc.F("Désinstaller {0}", target); break;
                            case "enableapplication": s.Label = Loc.F("Activer l'appli {0}", target); break;
                            case "disableapplication": s.Label = Loc.F("Désactiver l'appli {0}", target); break;
                            case "setdefaultlauncher": s.Label = Loc.F("Lanceur par défaut : {0}", target); break;
                            case "clearapplicationuserdata":
                                s.Label = Loc.F("Effacer les données de {0}", target);
                                s.Sensitive = true; s.SensitiveReason = Loc.T("efface les données de l'appli");
                                break;
                            default: s.Label = action.Length > 0 ? Loc.F("Applications : {0} {1}", action, target).Trim() : Loc.T("Gestion des applications"); break;
                        }
                        s.Theme = ehs ? ThemeEhs : velocity ? ThemeVelocity : dw ? ThemeDataWedge : ThemeApps;
                        break;
                    }
                case "FileMgr":
                    {
                        string targetPath = Parm(e, "TargetPathAndFileName");
                        string src = Parm(e, "SourcePathAndFileName");
                        string name = FileName(targetPath.Length > 0 ? targetPath : src);
                        string dir = targetPath.Length > name.Length ? targetPath.Substring(0, targetPath.Length - name.Length) : "";
                        s.Label = name.Length > 0 ? (dir.Length > 0 ? Loc.F("Déposer {0} → {1}", name, dir) : Loc.F("Déposer {0}", name)) : Loc.T("Gestion des fichiers");
                        string uri = Parm(e, "SourceURI");
                        if (uri.Length > 0 && targetPath.Length > 0)
                        {
                            s.DownloadTarget = targetPath;
                            s.DownloadEncrypted = !uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("ftp", StringComparison.OrdinalIgnoreCase);
                            s.Label = dir.Length > 0 ? Loc.F("Télécharger {0} → {1}", name, dir) : Loc.F("Télécharger {0}", name);
                        }
                        s.Theme = ehs ? ThemeEhs : velocity ? ThemeVelocity : dw ? ThemeDataWedge : ThemeFiles;
                        if (ehs && name.Equals("enterprisehomescreen.xml", StringComparison.OrdinalIgnoreCase)) s.Label = Loc.T("Déposer la configuration EHS");
                        break;
                    }
                case "DisplayMgr":
                    {
                        string t = Parm(e, "TimeoutInterval");
                        s.Theme = ThemeScreen;
                        s.Label = int.TryParse(t, out int sec) && sec > 0
                            ? (sec % 60 == 0 ? Loc.F("Mise en veille après {0} min", sec / 60) : Loc.F("Mise en veille après {0} s", sec))
                            : Loc.T("Réglages d'écran");
                        break;
                    }
                case "PowerMgr":
                    {
                        string r = Parm(e, "ResetAction");
                        s.Theme = ThemeScreen;
                        switch (r)
                        {
                            case "4": s.Label = Loc.T("Redémarrer le terminal"); break;
                            case "5": s.Label = Loc.T("Remise à zéro entreprise"); s.Sensitive = true; s.SensitiveReason = Loc.T("efface les données et les applis (le dossier /enterprise est gardé)"); break;
                            case "6": s.Label = Loc.T("Remise à zéro usine"); s.Sensitive = true; s.SensitiveReason = Loc.T("efface tout le terminal"); break;
                            case "7": s.Label = Loc.T("Effacement complet du terminal"); s.Sensitive = true; s.SensitiveReason = Loc.T("efface tout le terminal, /enterprise compris"); break;
                            case "8": s.Label = Loc.T("Mise à jour de l'OS"); s.Sensitive = true; s.SensitiveReason = Loc.T("installe un autre système et redémarre"); break;
                            default:
                                s.Label = Loc.T("Gestion de l'alimentation");
                                if (int.TryParse(r, out int ra) && ra > 8) { s.Sensitive = true; s.SensitiveReason = Loc.T("action d'alimentation inconnue (remise à zéro ou mise à jour possible)"); }
                                break;
                        }
                        break;
                    }
                default:
                    {
                        if (KnownTypes.TryGetValue(type, out string label)) s.Label = Loc.T(label);
                        else { s.Label = type.Length > 0 ? type : Loc.T("Étape sans type"); s.Recognized = false; }
                        s.Theme = !s.Recognized ? ThemeOther : ehs ? ThemeEhs : velocity ? ThemeVelocity : dw ? ThemeDataWedge : ThemeSystem;
                        break;
                    }
            }

            // Ce qui efface ou fait perdre la main, quel que soit le gestionnaire.
            if (!s.Sensitive)
            {
                if (type.Equals("EncryptMgr", StringComparison.OrdinalIgnoreCase)) { s.Sensitive = true; s.SensitiveReason = Loc.T("chiffrement du stockage"); }
                else if (e.Descendants("parm").Any(x => Regex.IsMatch((string)x.Attribute("name") ?? "", "ADB|(?i:usbdebug|developeroption)")))
                { s.Sensitive = true; s.SensitiveReason = Loc.T("touche au débogage USB : ADB Tools peut perdre la main sur le terminal"); }
                else if (Regex.IsMatch(s.Xml, "(?i)factoryreset|enterprisereset|wipe"))
                { s.Sensitive = true; s.SensitiveReason = Loc.T("peut effacer des données"); }
            }

            // Fichiers que le profil lit sur le terminal : à joindre pour qu'ADB Tools les y dépose.
            foreach (var parm in e.Descendants("parm"))
            {
                string n = (string)parm.Attribute("name") ?? "", v = (string)parm.Attribute("value") ?? "";
                if (!v.StartsWith("/")) continue;
                if (n.Equals("APK", StringComparison.OrdinalIgnoreCase) || (n.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("Access", StringComparison.OrdinalIgnoreCase) < 0))
                    if (!s.DeviceFiles.Contains(v)) s.DeviceFiles.Add(v);
            }
            return s;
        }

        private static string Normalize(string xml) => Regex.Replace(xml ?? "", @"\s+", " ").Trim();

        private static string Hash(string s)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)), 0, 6).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Gestionnaires qu'on trouve normalement plusieurs fois (une étape par appli, fichier, réseau…).</summary>
        private static readonly HashSet<string> MultiTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "AppMgr", "FileMgr", "Wi-Fi", "WifiMgr", "CertMgr", "Intent", "GprsMgr", "PersistMgr", "KeyMappingMgr", "AccessMgr" };

        /// <summary>
        /// Fusionne deux profils en un seul : les étapes du second sont ajoutées à la suite du premier (avec leurs
        /// commentaires), les étapes identiques ne sont pas recopiées. Un même gestionnaire de réglages présent des deux
        /// côtés avec des valeurs différentes est signalé : MX applique les étapes dans l'ordre, la dernière l'emporte.
        /// </summary>
        public static MxMergeResult Merge(string baseXml, string addXml)
        {
            var r = new MxMergeResult();
            var a = XDocument.Parse(baseXml ?? "");
            var b = XDocument.Parse(addXml ?? "");
            XElement container = Container(a);
            var existing = new HashSet<string>(container.Elements("characteristic").Select(e => Normalize(e.ToString(SaveOptions.DisableFormatting))));
            var types = new HashSet<string>(container.Elements("characteristic").Select(e => (string)e.Attribute("type") ?? ""), StringComparer.OrdinalIgnoreCase);
            foreach (var e in Container(b).Elements("characteristic").ToList())
            {
                string norm = Normalize(e.ToString(SaveOptions.DisableFormatting));
                if (existing.Contains(norm)) { r.Duplicates++; continue; }
                string type = (string)e.Attribute("type") ?? "";
                if (types.Contains(type) && !MultiTypes.Contains(type) && !r.SameTypes.Contains(type)) r.SameTypes.Add(type);
                // Commentaires juste avant l'étape (ex. « SettingID=… » de StageNow) : gardés avec elle.
                var comments = new List<XComment>();
                for (var n = e.PreviousNode; n is XComment c; n = n.PreviousNode) comments.Insert(0, new XComment(c.Value));
                foreach (var c in comments) container.Add(c);
                container.Add(new XElement(e));
                existing.Add(norm);
                types.Add(type);
                r.Added++;
            }
            r.Xml = (a.Declaration != null ? a.Declaration + Environment.NewLine : "") + a.ToString();
            return r;
        }

        /// <summary>Élément qui porte les étapes : la racine « wap-provisioningdoc » (créée autour d'un profil d'une seule étape).</summary>
        private static XElement Container(XDocument d)
        {
            var root = d.Root;
            if (root.Name.LocalName == "wap-provisioningdoc") return root;
            if (root.Name.LocalName == "characteristic" && (string)root.Attribute("type") == "Profile") return root;
            var wrap = new XElement("wap-provisioningdoc", new XElement(root));
            root.ReplaceWith(wrap);
            return wrap;
        }

        /// <summary>Le XML d'une étape en Base64, prêt pour l'appli d'aide (ApplyMx).</summary>
        public static string StepPayload(MxStep s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Xml));

        /// <summary>« CODE|réponse en Base64 » renvoyé par l'appli d'aide → (code, réponse en clair).</summary>
        public static (string Status, string Detail) ParseHelperData(string data)
        {
            if (string.IsNullOrEmpty(data)) return ("", "");
            int bar = data.IndexOf('|');
            if (bar < 0) return (data, "");
            string detail = "";
            try { detail = Encoding.UTF8.GetString(Convert.FromBase64String(data.Substring(bar + 1))); } catch { }
            return (data.Substring(0, bar), detail);
        }

        /// <summary>Message d'erreur lisible tiré de la réponse MX (characteristic-error / parm-error desc="…").</summary>
        public static string ErrorSummary(string response)
        {
            var list = new List<string>();
            foreach (Match m in Regex.Matches(response ?? "", @"<(characteristic|parm)-error\b([^>]*)>"))
            {
                string attrs = m.Groups[2].Value;
                string Attr(string n) { var a = Regex.Match(attrs, n + @"=""([^""]*)"""); return a.Success ? System.Net.WebUtility.HtmlDecode(a.Groups[1].Value) : ""; }
                string desc = Attr("desc"), what = Attr("name").Length > 0 ? Attr("name") : Attr("type");
                list.Add(desc.Length > 0 ? (what.Length > 0 ? what + " : " + desc : desc) : Loc.F("{0} refusé", what));
            }
            return string.Join(" · ", list.Distinct().Take(3));
        }

        /// <summary>
        /// Réponse de la mini appli → étape acceptée ? EMDK répond souvent « CHECK_XML » (code 6, « Review the XML for
        /// details ») : c'est un succès tant que la réponse ne contient aucune erreur (characteristic-error / parm-error).
        /// </summary>
        public static bool IsMxSuccess(int code, string status, string response)
        {
            if (code == 1) return true;
            if (code != 2) return false;
            if (status != "SUCCESS" && status != "CHECK_XML") return false;
            return !Regex.IsMatch(response ?? "", @"<(characteristic|parm)-error\b");
        }

        /// <summary>Motif d'un refus, en clair (erreurs MX, sinon le statut d'EMDK).</summary>
        public static string RefusalText(string status, string response)
        {
            string sum = ErrorSummary(response);
            if (sum.Length > 0) return sum;
            if (status == "EXCEPTION" || status == "FEATURE_NOT_SUPPORTED")
                return Loc.F("refusé par EMDK : {0} (réglage non pris en charge par cette version ou par cette méthode)", (response ?? "").Trim().Length > 0 ? response.Trim() : status);
            return Loc.F("refusé par le terminal ({0})", status);
        }
    }
}

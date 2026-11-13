using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ADBToolsNew.Core
{
    /// <summary>Formats de profils constructeur pris en charge, et lecture commune (étapes, thèmes, étapes sensibles).</summary>
    public static class VendorKinds
    {
        public const string StageNow = "stagenow";        // Zebra : profil MX exporté (XML)
        public const string Scan2Deploy = "scan2deploy";  // Datalogic : profil Scan2Deploy (JSON)
        public const string DeviceConfig = "deviceconfig"; // Honeywell : DeviceConfig.xml (EZConfig)

        public const string Honeywell = "Honeywell";
        public const string Datalogic = "Datalogic";

        public static string BrandOf(string kind) =>
            kind == Scan2Deploy ? Datalogic : kind == DeviceConfig ? Honeywell : VendorModules.Zebra;

        public static bool IsJson(string kind) => kind == Scan2Deploy;

        /// <summary>« Profil StageNow », « Profil Scan2Deploy », « DeviceConfig Honeywell ».</summary>
        public static string FormatName(string kind) =>
            kind == Scan2Deploy ? "Scan2Deploy" : kind == DeviceConfig ? "DeviceConfig" : "StageNow";

        /// <summary>Format d'un fichier d'après son contenu : JSON → Scan2Deploy, racine ConfigDoc → DeviceConfig, sinon StageNow.</summary>
        public static string Detect(string text)
        {
            string t = (text ?? "").TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (t.StartsWith("{")) return Scan2Deploy;
            if (Regex.IsMatch(t, @"^(<\?xml[^>]*\?>\s*)?(<!--.*?-->\s*)*<ConfigDoc\b", RegexOptions.Singleline)) return DeviceConfig;
            return StageNow;
        }

        public static MxProfile Read(string kind, string text, bool allowEmpty = false)
        {
            switch (kind)
            {
                case Scan2Deploy: return Scan2DeployReader.Read(text, allowEmpty);
                case DeviceConfig: return DeviceConfigReader.Read(text, allowEmpty);
                default: return StageNowReader.Read(text, allowEmpty);
            }
        }

        /// <summary>Texte à envoyer au terminal avec les seules étapes cochées (formats appliqués d'un bloc).</summary>
        public static string BuildChecked(string kind, string text, Func<MxStep, bool> keep)
        {
            switch (kind)
            {
                case Scan2Deploy: return Scan2DeployReader.BuildChecked(text, keep);
                case DeviceConfig: return DeviceConfigReader.BuildChecked(text, keep);
                default: throw new NotSupportedException(kind);
            }
        }

        internal static int LineOf(string text, int index)
        {
            if (index <= 0) return 1;
            int n = 1;
            for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') n++;
            return n;
        }

        private static readonly Regex DangerName = new Regex(@"(?i)factory.?reset|enterprise.?reset|wipe|usb.?debug|debug.?bridge|\badb\b|developer");
        internal static bool LooksDangerous(string name) => DangerName.IsMatch(name ?? "");
    }

    // =====================================================================================================
    // Datalogic : profil Scan2Deploy (JSON). Format : schéma « Android-Scan2Deploy » publié par Datalogic
    // (sections layout, padlock, global, settings, network, deployment, blobs). Appliqué en une fois par
    // l'agent Scan2Deploy du terminal (intent START_SERVICE, extra json-path) ; bilan dans S2DStatus.log.
    // =====================================================================================================
    public static class Scan2DeployReader
    {
        public const string Agent = "com.datalogic.scan2deploy";
        public const string StatusFile = "/sdcard/Download/S2DStatus.log";

        private static readonly JsonDocumentOptions Options = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

        private static readonly string[] GlobalOptions = { "target-path", "install-path", "update-path", "purge-target-path", "auto-scan", "download-preinstalled", "downgrade-preinstalled", "backup-to-enterprise", "oemconfig" };

        public static MxProfile Read(string json, bool allowEmpty = false)
        {
            var p = new MxProfile();
            JsonObject root;
            try
            {
                root = JsonNode.Parse(json ?? "", null, Options) as JsonObject;
                if (root == null) { p.Error = Loc.T("Le profil Scan2Deploy doit être un objet JSON ({ … })."); p.ErrorLine = 1; return p; }
            }
            catch (JsonException ex)
            {
                p.Error = Loc.T("JSON invalide") + " : " + Regex.Replace(ex.Message, @"\s*(Path|LineNumber):.*$", "");
                p.ErrorLine = (int)(ex.LineNumber ?? 0) + 1;
                return p;
            }
            string text = json ?? "";
            int Line(string key, int from = 0)
            {
                var m = new Regex("\"" + Regex.Escape(key) + "\"\\s*:").Match(text, Math.Min(from, text.Length));
                return m.Success ? VendorKinds.LineOf(text, m.Index) : 1;
            }
            int IndexOfKey(string key) { var m = new Regex("\"" + Regex.Escape(key) + "\"\\s*:").Match(text); return m.Success ? m.Index : 0; }
            // Ligne de la n-ième occurrence d'une clé après une position (éléments d'un tableau).
            int IndexAfter(string key, int from, int nth)
            {
                var ms = new Regex("\"" + Regex.Escape(key) + "\"\\s*:").Matches(text, Math.Min(from, text.Length));
                return ms.Count > nth ? VendorKinds.LineOf(text, ms[nth].Index) : VendorKinds.LineOf(text, from);
            }
            MxStep Add(string type, string key, string theme, string label, JsonNode node, int line)
            {
                var s = new MxStep { Type = type, Key = "s2d:" + key, Theme = theme, Label = label, Line = line, Xml = node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "" };
                p.Steps.Add(s);
                return s;
            }

            if (root["layout"] is JsonObject layout && layout["description"] is JsonValue d && d.TryGetValue(out string desc) && desc != "none")
                p.Name = desc.Length > 60 ? desc.Substring(0, 60) + "…" : desc;

            // Wi-Fi : réseau de préparation, puis réseaux supplémentaires.
            string WifiLabel(JsonObject n, bool extra)
            {
                string essid = Str(n, "essid"), mode = Str(n, "mode");
                string sec = mode.Length == 0 ? "" : mode.ToLowerInvariant() == "open" ? Loc.T("ouvert") : mode.ToUpperInvariant();
                return (extra ? Loc.F("Réseau Wi-Fi supplémentaire « {0} »", essid.Length > 0 ? essid : "?") : Loc.F("Réseau Wi-Fi « {0} »", essid.Length > 0 ? essid : "?"))
                    + (sec.Length > 0 ? " · " + sec : "");
            }
            List<string> WifiNotes(JsonObject n)
            {
                var notes = new List<string>();
                if (Str(n, "eap-method") is string eap && eap.Length > 0 && eap != "none") notes.Add(Loc.F("entreprise ({0})", eap.ToUpperInvariant()));
                if (Bool(n, "mode-key-encrypted") == true || Bool(n, "eap-password-encrypted") == true) notes.Add(Loc.T("mot de passe chiffré par Scan2Deploy Studio"));
                return notes;
            }
            if (root["network"] is JsonObject net)
            {
                var s = Add("network", "network", StageNowReader.ThemeWifi, WifiLabel(net, false), net, Line("network"));
                var notes = WifiNotes(net);
                if (Bool(net, "purge") != false) notes.Insert(0, Loc.T("efface les autres réseaux Wi-Fi enregistrés"));
                s.Warning = string.Join(" · ", notes);
            }
            if (root["additional-networks"] is JsonArray extraNets)
            {
                int start = IndexOfKey("additional-networks");
                for (int i = 0; i < extraNets.Count; i++)
                    if (extraNets[i] is JsonObject n)
                    {
                        int at = IndexAfter("essid", start, i);
                        var s = Add("additional-networks", "additional-networks[" + i + "]", StageNowReader.ThemeWifi, WifiLabel(n, true), n, at);
                        s.Warning = string.Join(" · ", WifiNotes(n));
                    }
            }

            // Réglages du terminal : un réglage par clé.
            if (root["settings"] is JsonObject settings)
            {
                int start = IndexOfKey("settings");
                foreach (var kv in settings)
                {
                    string k = kv.Key;
                    string v = kv.Value?.ToString() ?? "";
                    string theme = k == "default-home" ? StageNowReader.ThemeApps
                        : k == "lock-screen" || k == "status-bar" || k == "navigation-bar" || k == "charge-threshold" ? StageNowReader.ThemeScreen
                        : StageNowReader.ThemeSystem;
                    var s = Add("settings", "settings." + k, theme, SettingLabel(k, kv.Value), kv.Value, Line(k, start));
                    if (k == "debug-bridge" && Bool(settings, k) == false)
                    {
                        s.Sensitive = true;
                        s.SensitiveReason = Loc.T("coupe le débogage USB : ADB Tools perd la main sur le terminal");
                    }
                    if (!KnownSettings.Contains(k)) { s.Recognized = false; s.Theme = StageNowReader.ThemeOther; }
                }
            }

            // Global : script, action de fin, options de l'agent.
            if (root["global"] is JsonObject global)
            {
                int start = IndexOfKey("global");
                if (global["script"] is JsonNode script)
                {
                    var s = Add("global", "global.script", StageNowReader.ThemeSystem, ScriptLabel(script), script, Line("script", start));
                    var cmds = ScriptCommands(script);
                    if (cmds.Contains("UPDATE"))
                    {
                        s.Sensitive = true;
                        s.SensitiveReason = Loc.T("le script installe une mise à jour du système (UPDATE)");
                    }
                    s.Warning = cmds.Contains("SHELL") ? Loc.T("le script lance des commandes shell, non vérifiées par ADB Tools")
                        : Loc.T("commandes exécutées par Scan2Deploy, non vérifiées par ADB Tools");
                }
                var opts = GlobalOptions.Where(o => global.ContainsKey(o)).ToList();
                if (opts.Count > 0)
                {
                    var o = new JsonObject();
                    foreach (var k in opts) o[k] = global[k]?.DeepClone();
                    Add("global", "global.options", StageNowReader.ThemeOther, Loc.F("Options de l'agent Scan2Deploy ({0})", string.Join(", ", opts)), o, Line(opts[0], start));
                }
                if (Str(global, "action") is string action && action.Length > 0 && action != "none")
                {
                    var s = Add("global", "global.action", StageNowReader.ThemeSystem, ActionLabel(action), global["action"], Line("action", start));
                    if (action == "factory-reset" || action == "enterprise-reset")
                    {
                        s.Sensitive = true;
                        s.SensitiveReason = action == "factory-reset" ? Loc.T("efface tout le terminal (retour usine)") : Loc.T("efface les données hors partition entreprise");
                    }
                }
                foreach (var kv in global.Where(x => x.Key != "script" && x.Key != "action" && !GlobalOptions.Contains(x.Key)))
                {
                    var s = Add("global", "global." + kv.Key, StageNowReader.ThemeOther, Loc.F("Option « {0} »", kv.Key), kv.Value, Line(kv.Key, start));
                    s.Recognized = false;
                }
            }

            // Déploiement : archive téléchargée puis déployée par l'agent.
            if (root["deployment"] is JsonObject dep)
            {
                string archive = Str(dep, "working-archive");
                string storage = Str(dep, "storage-type");
                if (Str(dep, "host").Length > 0)
                {
                    string scheme = Str(dep, "scheme").Length > 0 ? Str(dep, "scheme") : "http";
                    string port = dep["port"]?.ToString() ?? "";
                    string url = scheme + "://" + Str(dep, "host") + (port.Length > 0 && port != "80" && port != "443" && port != "21" && port != "22" ? ":" + port : "") + "/" + Str(dep, "path").TrimStart('/');
                    var s = Add("deployment", "deployment", StageNowReader.ThemeFiles, Loc.F("Télécharger et déployer l'archive {0}", url), dep, Line("deployment"));
                    s.Warning = Loc.T("le terminal doit joindre le serveur au moment d'appliquer");
                }
                else
                {
                    // Déploiement local : l'archive est lue sur le terminal (mémoire interne, carte SD ou clé USB).
                    string where = storage == "removable-sdcard" ? Loc.T("carte SD") : storage == "usb-drive" ? Loc.T("clé USB") : Loc.T("mémoire interne");
                    var s = Add("deployment", "deployment", StageNowReader.ThemeFiles,
                        Loc.F("Déployer l'archive {0} ({1})", archive.Length > 0 ? archive : "/sdcard/Download/scan2deploy.archive", where), dep, Line("deployment"));
                    if (storage == "removable-sdcard" || storage == "usb-drive") s.Warning = Loc.F("l'archive doit être sur la {0} du terminal", where);
                    else if (archive.StartsWith("/")) s.DeviceFiles.Add(archive);
                }
            }

            // Fichiers écrits par le profil (contenu inclus).
            if (root["blobs"] is JsonArray blobs)
            {
                int start = IndexOfKey("blobs");
                for (int i = 0; i < blobs.Count; i++)
                {
                    var b = blobs[i] as JsonObject;
                    string file = b != null ? Str(b, "file") : "";
                    int size = b != null ? (int)Math.Round(Str(b, "content").Length * 0.75) : 0;
                    Add("blobs", "blobs[" + i + "]", StageNowReader.ThemeFiles, Loc.F("Écrire le fichier {0} ({1} octets)", file.Length > 0 ? file : "?", size), b, Line("file", start));
                }
            }

            if (root["update-scan2deploy"] is JsonObject upd)
                Add("update-scan2deploy", "update-scan2deploy", StageNowReader.ThemeApps,
                    Loc.F("Mettre à jour l'agent Scan2Deploy (version {0})", upd["update-version"]?.ToString() ?? "?"), upd, Line("update-scan2deploy"));

            if (root["padlock"] is JsonObject pad)
                Add("padlock", "padlock", StageNowReader.ThemeSystem, Loc.F("Verrou de préparation : {0}", Str(pad, "state").Length > 0 ? Str(pad, "state") : "undefined"), pad, Line("padlock"));

            foreach (var kv in root.Where(x => !Known.Contains(x.Key) && !x.Key.StartsWith("$")))
            {
                var s = Add(kv.Key, kv.Key, StageNowReader.ThemeOther, Loc.F("Section « {0} »", kv.Key), kv.Value, Line(kv.Key));
                s.Recognized = false;
                s.Warning = Loc.T("section inconnue du format Scan2Deploy");
            }

            if (p.Steps.Count == 0 && !allowEmpty) { p.Error = Loc.T("Aucun réglage dans ce profil Scan2Deploy."); p.ErrorLine = 1; }
            return p;
        }

        private static readonly HashSet<string> Known = new HashSet<string> { "layout", "padlock", "global", "settings", "network", "additional-networks", "deployment", "blobs", "update-scan2deploy" };
        private static readonly HashSet<string> KnownSettings = new HashSet<string> { "date-time", "auto-time", "auto-time-zone", "time-zone", "auto-time-server", "debug-bridge", "lock-screen", "status-bar", "navigation-bar", "charge-threshold", "usb-profile", "usb-function", "default-home" };

        private static readonly Regex ScriptCmd = new Regex(@"^\s*([A-Z_]+)\b");

        /// <summary>Commandes d'un script Scan2Deploy (premier mot de chaque ligne), dans l'ordre, sans doublon.</summary>
        private static List<string> ScriptCommands(JsonNode script) =>
            script is JsonArray arr
                ? arr.Select(x => ScriptCmd.Match(x?.ToString() ?? "")).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().ToList()
                : new List<string>();

        private static string ScriptLabel(JsonNode script)
        {
            if (!(script is JsonArray arr)) return Loc.F("Script : {0}", script.ToString());
            var counts = arr.Select(x => ScriptCmd.Match(x?.ToString() ?? "")).Where(m => m.Success).GroupBy(m => m.Groups[1].Value)
                .Select(g => g.Count() > 1 ? g.Key + " ×" + g.Count() : g.Key).ToList();
            return counts.Count > 0 ? Loc.F("Script : {0} commande(s) ({1})", arr.Count, string.Join(", ", counts)) : Loc.F("Script ({0} ligne(s))", arr.Count);
        }

        /// <summary>Paquet visé par une action de fin « intent:…;package=…;end » ou « android-app://paquet/… ».</summary>
        private static string IntentPackage(string action)
        {
            var m = Regex.Match(action ?? "", @"(?:;package=|android-app://)([A-Za-z0-9_.]+)");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string s) ? s : (o[key]?.ToString() ?? "");
        private static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out bool b) ? b : (bool?)null;

        private static string OnOff(JsonNode n) => n is JsonValue v && v.TryGetValue(out bool b) ? (b ? Loc.T("oui") : Loc.T("non")) : n?.ToString() ?? "";

        private static string SettingLabel(string k, JsonNode v)
        {
            switch (k)
            {
                case "date-time": return Loc.F("Date et heure : {0}", v);
                case "auto-time": return Loc.F("Heure automatique : {0}", OnOff(v));
                case "auto-time-zone": return Loc.F("Fuseau automatique : {0}", OnOff(v));
                case "auto-time-server": return Loc.F("Serveur de temps : {0}", v);
                case "debug-bridge": return Loc.F("Débogage USB (ADB) : {0}", OnOff(v));
                case "lock-screen": return Loc.F("Écran de verrouillage : {0}", OnOff(v));
                case "status-bar": return Loc.F("Barre d'état : {0}", OnOff(v));
                case "navigation-bar": return Loc.F("Barre de navigation : {0}", OnOff(v));
                case "charge-threshold": return Loc.F("Seuil de charge : {0} %", v);
                case "usb-profile": return Loc.F("Profil USB : {0}", v);
                case "usb-function": return Loc.F("Mode USB : {0}", v);
                case "time-zone": return Loc.F("Fuseau horaire : {0}", v);
                case "default-home":
                    {
                        var o = v as JsonObject;
                        string type = o != null ? Str(o, "type") : "", pkg = o != null ? Str(o, "package-name") : "";
                        if (type == "aosp-launcher-app") return Loc.T("Lanceur par défaut : lanceur Android d'origine");
                        if (type == "no_default") return Loc.T("Lanceur par défaut : aucun (choix laissé à l'utilisateur)");
                        if (type == "no_action") return Loc.T("Lanceur par défaut : inchangé");
                        return Loc.F("Lanceur par défaut : {0}", pkg.Length > 0 ? pkg : "?");
                    }
                default:
                    {
                        string val = v is JsonValue ? v.ToString() : v?.ToJsonString() ?? "";
                        if (val.Length > 60) val = val.Substring(0, 60) + "…";
                        return Loc.F("Réglage « {0} » : {1}", k, val);
                    }
            }
        }

        private static string ActionLabel(string a)
        {
            switch (a)
            {
                case "close": return Loc.T("À la fin : fermer Scan2Deploy");
                case "reset": return Loc.T("À la fin : redémarrer le terminal");
                case "enterprise-reset": return Loc.T("À la fin : réinitialisation entreprise");
                case "factory-reset": return Loc.T("À la fin : remise à zéro usine");
                default:
                    string pkg = IntentPackage(a);
                    if (pkg.Length > 0) return Loc.F("À la fin : ouvrir l'appli {0}", pkg);
                    return Loc.F("À la fin : {0}", a.Length > 60 ? a.Substring(0, 60) + "…" : a);
            }
        }

        /// <summary>Le profil sans les étapes décochées, en JSON compact.</summary>
        public static string BuildChecked(string json, Func<MxStep, bool> keep)
        {
            var root = JsonNode.Parse(json, null, Options) as JsonObject;
            var p = Read(json, true);
            var dropBlobs = new List<int>();
            var dropNets = new List<int>();
            foreach (var s in p.Steps.Where(s => !keep(s)))
            {
                string k = s.Key.Substring(4);
                if (k.StartsWith("settings.")) (root["settings"] as JsonObject)?.Remove(k.Substring(9));
                else if (k == "global.options") foreach (var o in GlobalOptions) (root["global"] as JsonObject)?.Remove(o);
                else if (k.StartsWith("global.")) (root["global"] as JsonObject)?.Remove(k.Substring(7));
                else if (k.StartsWith("blobs[")) dropBlobs.Add(int.Parse(k.Substring(6, k.Length - 7)));
                else if (k.StartsWith("additional-networks[")) dropNets.Add(int.Parse(k.Substring(20, k.Length - 21)));
                else root.Remove(k);
            }
            if (root["blobs"] is JsonArray arr) foreach (int i in dropBlobs.OrderByDescending(x => x)) if (i < arr.Count) arr.RemoveAt(i);
            if (root["additional-networks"] is JsonArray nets) foreach (int i in dropNets.OrderByDescending(x => x)) if (i < nets.Count) nets.RemoveAt(i);
            foreach (var name in new[] { "settings", "global", "blobs", "additional-networks" })
                if (root[name] is JsonObject o && o.Count == 0 || root[name] is JsonArray a && a.Count == 0) root.Remove(name);
            return root.ToJsonString();
        }

        /// <summary>Contenu de S2DStatus.log : (succès ?, horodatage, détails). null si illisible.</summary>
        public static (bool Success, string Timestamp, string Details)? ParseStatus(string content)
        {
            try
            {
                if (!(JsonNode.Parse(content ?? "", null, Options) is JsonObject o)) return null;
                string status = Str(o, "status");
                if (status.Length == 0) return null;
                return (status.Equals("success", StringComparison.OrdinalIgnoreCase), Str(o, "timestamp"), Str(o, "details"));
            }
            catch { return null; }
        }

        /// <summary>Commande qui fait appliquer à l'agent le profil déposé sur le terminal.</summary>
        public static string ApplyCommand(string devicePath) =>
            "am broadcast -a datalogic.scan2deploy.intent.action.START_SERVICE -n " + Agent + "/.S2dServiceReceiver --es json-path " + MasterProfile.ShellQuote(devicePath);

        /// <summary>Le profil fait-il redémarrer ou réinitialiser le terminal (le bilan n'arrivera pas) ?</summary>
        public static bool EndsWithRestart(string json)
        {
            try { var g = (JsonNode.Parse(json, null, Options) as JsonObject)?["global"] as JsonObject; string a = g == null ? "" : Str(g, "action"); return a == "reset" || a.EndsWith("-reset"); }
            catch { return false; }
        }

        public static bool HasDeployment(string json)
        {
            try { return (JsonNode.Parse(json, null, Options) as JsonObject)?["deployment"] != null; } catch { return false; }
        }
    }

    // =====================================================================================================
    // Honeywell : DeviceConfig.xml (EZConfig). <ConfigDoc name="DeviceConfig"> avec des <Section name="…">
    // imbriquées et des <Key name="…">valeur</Key>. Déposé dans honeywell/persist (mode Provisioning activé),
    // appliqué au redémarrage. Une étape = une section qui porte directement des clés.
    // =====================================================================================================
    public static class DeviceConfigReader
    {
        public const string PersistDir = "/sdcard/honeywell/persist";
        public const string FileName = "DeviceConfig.xml";

        public static MxProfile Read(string xml, bool allowEmpty = false)
        {
            var p = new MxProfile();
            XDocument doc;
            try { doc = XDocument.Parse(xml ?? "", LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace); }
            catch (XmlException ex) { p.Error = ex.Message; p.ErrorLine = Math.Max(1, ex.LineNumber); return p; }
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "ConfigDoc")
            {
                p.Error = Loc.T("Ce n'est pas un fichier DeviceConfig Honeywell (racine <ConfigDoc> attendue).");
                p.ErrorLine = 1;
                return p;
            }
            string docName = (string)root.Attribute("name") ?? "";
            if (!docName.Equals("DeviceConfig", StringComparison.OrdinalIgnoreCase))
            {
                p.Error = docName.Equals("Provisioner", StringComparison.OrdinalIgnoreCase)
                    ? Loc.T("Fichier de tâches Enterprise Provisioner : pas encore pris en charge (seul DeviceConfig.xml l'est).")
                    : Loc.F("Document Honeywell « {0} » : pas encore pris en charge (seul DeviceConfig.xml l'est).", docName);
                p.ErrorLine = 1;
                return p;
            }

            var seen = new Dictionary<string, int>();
            foreach (var sec in root.Descendants("Section").Where(s => s.Elements("Key").Any() && !s.Ancestors("HHPReserved").Any()))
            {
                var path = sec.AncestorsAndSelf("Section").Reverse().Select(s => (string)s.Attribute("name") ?? "").ToList();
                string id = string.Join("/", path);
                seen[id] = seen.TryGetValue(id, out int n) ? n + 1 : 0;
                var keys = sec.Elements("Key").ToList();
                string pathText = string.Join(" › ", path);
                var step = new MxStep
                {
                    Type = path.Count > 0 ? path[0] : "Section",
                    Key = "hw:" + id + (seen[id] > 0 ? "#" + seen[id] : ""),
                    Line = ((IXmlLineInfo)sec).HasLineInfo() ? ((IXmlLineInfo)sec).LineNumber : 1,
                    Xml = sec.ToString(),
                    Theme = ThemeOf(pathText),
                    Label = LabelOf(path, keys),
                };
                var danger = keys.Select(k => (string)k.Attribute("name") ?? "").Where(VendorKinds.LooksDangerous).ToList();
                if (danger.Count > 0 || VendorKinds.LooksDangerous(pathText))
                {
                    step.Sensitive = true;
                    step.SensitiveReason = Loc.F("touche à {0}", danger.Count > 0 ? string.Join(", ", danger) : path.LastOrDefault());
                }
                if (keys.Any(k => ((string)k.Attribute("flags") ?? "") == "1024" && k.Value.Trim().Length > 0))
                    step.Warning = Loc.T("contient un mot de passe ou une clé (en clair ou chiffré par EZConfig)");
                p.Steps.Add(step);
            }
            if (p.Steps.Count == 0 && !allowEmpty) { p.Error = Loc.T("Aucun réglage dans ce DeviceConfig.xml."); p.ErrorLine = 1; }
            return p;
        }

        private static string ThemeOf(string path)
        {
            string s = path.ToLowerInvariant();
            if (Regex.IsMatch(s, "wi-?fi|wireless|wlan|supplicant")) return StageNowReader.ThemeWifi;
            if (Regex.IsMatch(s, "scan|data collection|decod|symbolog|imager|barcode")) return StageNowReader.ThemeScanner;
            if (Regex.IsMatch(s, "display|screen|sleep|bright|power|battery|sound|volume")) return StageNowReader.ThemeScreen;
            if (Regex.IsMatch(s, "launcher|app|package|install")) return StageNowReader.ThemeApps;
            return StageNowReader.ThemeSystem;
        }

        private static string LabelOf(List<string> path, List<XElement> keys)
        {
            // Profil Wi-Fi : le nom du réseau parle plus que « WiFiProfile1 ».
            var ssid = keys.FirstOrDefault(k => ((string)k.Attribute("name") ?? "").Equals("SSID", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
            if (!string.IsNullOrEmpty(ssid)) return Loc.F("Réseau Wi-Fi « {0} »", ssid);
            string where = string.Join(" › ", path.Skip(Math.Max(0, path.Count - 2)));
            if (keys.Count == 1)
                return Loc.F("{0} : {1} = {2}", where, (string)keys[0].Attribute("name"), keys[0].Value.Trim().Length > 0 ? keys[0].Value.Trim() : Loc.T("(vide)"));
            return Loc.F("{0} ({1} réglages)", where, keys.Count);
        }

        /// <summary>Le fichier sans les sections décochées (et sans les sections devenues vides).</summary>
        public static string BuildChecked(string xml, Func<MxStep, bool> keep)
        {
            var p = Read(xml, true);
            var doc = XDocument.Parse(xml, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            var drop = new HashSet<string>(p.Steps.Where(s => !keep(s)).Select(s => s.Key));
            if (drop.Count == 0) return xml;
            var seen = new Dictionary<string, int>();
            var remove = new List<XElement>();
            foreach (var sec in doc.Root.Descendants("Section").Where(s => s.Elements("Key").Any() && !s.Ancestors("HHPReserved").Any()).ToList())
            {
                string id = string.Join("/", sec.AncestorsAndSelf("Section").Reverse().Select(s => (string)s.Attribute("name") ?? ""));
                seen[id] = seen.TryGetValue(id, out int n) ? n + 1 : 0;
                if (drop.Contains("hw:" + id + (seen[id] > 0 ? "#" + seen[id] : ""))) remove.Add(sec);
            }
            foreach (var sec in remove)
            {
                // La section, sans ses clés ni ses sous-sections encore utiles : on retire les clés, puis la section si elle est vide.
                sec.Elements("Key").Remove();
                var cur = sec;
                while (cur != null && cur.Name.LocalName == "Section" && !cur.Elements().Any())
                {
                    var parent = cur.Parent;
                    cur.Remove();
                    cur = parent;
                }
            }
            string body = doc.Root.ToString(SaveOptions.DisableFormatting);
            return doc.Declaration != null ? doc.Declaration + "\r\n" + body : body;
        }
    }
}

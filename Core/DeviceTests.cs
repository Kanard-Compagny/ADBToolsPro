using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ADBToolsNew.Core
{
    /// <summary>Appli de diagnostic installée temporairement sur le terminal (DiagApp, compilée avec le programme).</summary>
    public static class DiagApp
    {
        public const string Package = "fr.kanard.adbtools.diag";
        public const string Activity = Package + "/.MainActivity";
        public const string LogTag = "ADBToolsDiag";
        public const string DataWedgeProfile = "ADBToolsDiag";
        /// <summary>Version de l'appli fournie avec le programme : à garder égale au versionCode de DiagApp/AndroidManifest.xml.</summary>
        public const int VersionCode = 11;

        /// <summary>Version installée lue dans « dumpsys package » (0 : appli absente).</summary>
        public static int ParseInstalledVersion(string dumpsys)
        {
            string s = dumpsys ?? "";
            if (s.IndexOf("Package [" + Package + "]", StringComparison.Ordinal) < 0) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"versionCode=(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
        }
        public static string ApkPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DiagApp", "ADBToolsDiag.apk");

        /// <summary>Arguments adb pour envoyer une commande à l'appli (elle passe au premier plan), avec la langue et le thème
        /// du programme : l'appli s'affiche comme lui.</summary>
        public static string CommandArgs(string serial, string cmd) =>
            $"-s {serial} shell am start -n {Activity} -e cmd {cmd} -e lang {(Loc.IsEnglish ? "en" : "fr")} -e theme {(ThemeManager.Current == AppTheme.Light ? "light" : "dark")}";

        /// <summary>Résultats des tests gardés sur le terminal par l'appli (Out.java).</summary>
        public const string ResultsRemotePath = "/sdcard/Android/data/" + Package + "/files/results/tests.json";

        /// <summary>
        /// tests.json de l'appli → messages RESULT puis VERDICT de chaque test (même format que le journal), avec l'heure.
        /// </summary>
        public static List<(DiagMessage Message, DateTime At)> ParseSavedResults(string json)
        {
            var list = new List<(DiagMessage, DateTime)>();
            try
            {
                using (var doc = JsonDocument.Parse(json ?? ""))
                {
                    if (!doc.RootElement.TryGetProperty("tests", out var tests) || tests.ValueKind != JsonValueKind.Object) return list;
                    foreach (var t in tests.EnumerateObject())
                    {
                        if (t.Value.ValueKind != JsonValueKind.Object) continue;
                        DateTime at = DateTime.Now;
                        if (t.Value.TryGetProperty("at", out var a) && a.TryGetInt64(out long ms))
                            at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                        foreach (var key in new[] { "result", "verdict" })
                        {
                            if (!t.Value.TryGetProperty(key, out var line) || line.ValueKind != JsonValueKind.String) continue;
                            var m = DiagMessage.Parse(LogTag + ": " + line.GetString());
                            if (m != null && m.Test == t.Name) list.Add((m, at));
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Flux du journal limité à l'appli, à partir de maintenant.</summary>
        public static string LogcatArgs(string serial) => $"-s {serial} logcat -T 1 -v brief -s {LogTag}:I";
    }

    /// <summary>Message de l'appli de diagnostic lu dans le journal (voir Out.java).</summary>
    public sealed class DiagMessage
    {
        public string Type { get; set; } = "";
        public string Test { get; set; } = "";
        public string Status { get; set; } = "";
        public JsonElement Json { get; set; }
        public bool HasJson { get; set; }

        /// <summary>« I/ADBToolsDiag( 123): RESULT \t microphone \t ok \t base64 » → message ; null sinon.</summary>
        public static DiagMessage Parse(string line)
        {
            if (string.IsNullOrEmpty(line) || line.IndexOf(DiagApp.LogTag, StringComparison.Ordinal) < 0) return null;
            int colon = line.IndexOf("): ", StringComparison.Ordinal);
            string body = colon >= 0 ? line.Substring(colon + 3) : line.Substring(line.IndexOf(DiagApp.LogTag, StringComparison.Ordinal) + DiagApp.LogTag.Length).TrimStart(':', ' ');
            var p = body.TrimEnd('\r', '\n').Split('\t');
            if (p.Length == 0) return null;
            var m = new DiagMessage { Type = p[0].Trim() };
            switch (m.Type)
            {
                case "FEATURES": m.SetJson(p.Length > 1 ? p[1] : ""); break;
                case "STATE": m.Test = Get(p, 1); m.Status = Get(p, 2); break;
                case "LIVE": m.Test = Get(p, 1); m.SetJson(Get(p, 2)); break;
                case "RESULT": m.Test = Get(p, 1); m.Status = Get(p, 2); m.SetJson(Get(p, 3)); break;
                case "VERDICT": m.Test = Get(p, 1); m.Status = Get(p, 2); break;
                case "READY": case "SCAN": case "INFO": case "EXPORT": break;
                default: return null;
            }
            return m;
        }

        private static string Get(string[] p, int i) => i < p.Length ? p[i].Trim() : "";

        private void SetJson(string b64)
        {
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
                using (var doc = JsonDocument.Parse(json)) Json = doc.RootElement.Clone();
                HasJson = true;
            }
            catch { HasJson = false; }
        }

        public string Str(string name) => HasJson && Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : "";

        public double Num(string name) => double.TryParse(Str(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    /// <summary>Matériel présent sur le terminal (inventaire fait par l'appli au démarrage).</summary>
    public sealed class DeviceFeatures
    {
        private readonly Dictionary<string, bool> _flags = new Dictionary<string, bool>();
        public string Manufacturer { get; private set; } = "";
        public string Model { get; private set; } = "";
        public string Android { get; private set; } = "";
        public string Scanner { get; private set; } = "";
        public List<string> Sensors { get; } = new List<string>();

        public bool Has(string feature)
        {
            if (string.IsNullOrEmpty(feature)) return true;
            if (feature == "sensors") return Sensors.Count > 0;
            if (feature == "scanner") return true; // toujours testable : lecteur intégré non reconnu, ou douchette
            return _flags.TryGetValue(feature, out bool b) && b;
        }

        public static DeviceFeatures From(DiagMessage m)
        {
            var f = new DeviceFeatures();
            if (m == null || !m.HasJson || m.Json.ValueKind != JsonValueKind.Object) return f;
            foreach (var p in m.Json.EnumerateObject())
            {
                switch (p.Value.ValueKind)
                {
                    case JsonValueKind.True: f._flags[p.Name] = true; break;
                    case JsonValueKind.False: f._flags[p.Name] = false; break;
                    case JsonValueKind.Array:
                        if (p.Name == "sensors") f.Sensors.AddRange(p.Value.EnumerateArray().Select(x => x.GetString()));
                        break;
                }
            }
            f.Manufacturer = m.Str("manufacturer");
            f.Model = m.Str("model");
            f.Android = m.Str("android");
            f.Scanner = m.Str("scanner");
            return f;
        }
    }

    /// <summary>Définition d'un test du terminal.</summary>
    public sealed class DeviceTestDef
    {
        public string Id { get; set; }
        public string Icon { get; set; }
        /// <summary>Nom de référence en français (traduit à l'affichage).</summary>
        public string Name { get; set; }
        /// <summary>Matériel requis (clé de l'inventaire), null si toujours possible.</summary>
        public string Feature { get; set; }
        public string Instruction { get; set; }
        /// <summary>Le résultat dépend de ce que constate le technicien (son, vibration, flash…).</summary>
        public bool NeedsConfirmation { get; set; }
        /// <summary>Test réalisé par une autre fenêtre (test du lecteur).</summary>
        public bool External { get; set; }
    }

    public static class DeviceTestCatalog
    {
        public static readonly List<DeviceTestDef> All = new List<DeviceTestDef>
        {
            new DeviceTestDef { Id = "screen", Icon = "🖥️", Name = "Écran (couleurs)", NeedsConfirmation = true,
                Instruction = "Le terminal affiche des couleurs unies en plein écran ; touchez l'écran pour passer à la suivante. Cherchez pixels morts, taches ou traces, puis donnez le résultat." },
            new DeviceTestDef { Id = "touch", Icon = "👆", Name = "Dalle tactile (grille)",
                Instruction = "Balayez tout l'écran du terminal du doigt : chaque case touchée devient verte. Réussi quand toutes les cases ont réagi." },
            new DeviceTestDef { Id = "camera_back", Icon = "📷", Name = "Caméra arrière", Feature = "camera_back",
                Instruction = "Visez un objet avec le terminal : l'aperçu s'affiche, puis une photo est prise et affichée ici." },
            new DeviceTestDef { Id = "camera_front", Icon = "🤳", Name = "Caméra avant", Feature = "camera_front",
                Instruction = "Placez-vous face à l'écran : une photo est prise avec la caméra avant et affichée ici." },
            new DeviceTestDef { Id = "flash", Icon = "🔦", Name = "Flash", Feature = "flash", NeedsConfirmation = true,
                Instruction = "Le flash clignote 3 fois. Confirmez ensuite ce que vous avez vu." },
            new DeviceTestDef { Id = "speaker", Icon = "🔊", Name = "Haut-parleur", Feature = "speaker", NeedsConfirmation = true,
                Instruction = "Le terminal émet 3 bips (volume à 80 %, rétabli ensuite). Confirmez que le son est net, sans grésillement." },
            new DeviceTestDef { Id = "earpiece", Icon = "📞", Name = "Écouteur (appel)", Feature = "earpiece", NeedsConfirmation = true,
                Instruction = "Portez le terminal à l'oreille : 3 bips sont joués dans l'écouteur d'appel." },
            new DeviceTestDef { Id = "microphone", Icon = "🎙️", Name = "Micro", Feature = "microphone",
                Instruction = "Parlez ou tapez des mains près du terminal pendant 5 secondes. Le niveau s'affiche en direct ; l'enregistrement peut être réécouté." },
            new DeviceTestDef { Id = "vibrator", Icon = "📳", Name = "Vibreur", Feature = "vibrator", NeedsConfirmation = true,
                Instruction = "Le terminal vibre 3 fois. Confirmez ce que vous avez senti." },
            new DeviceTestDef { Id = "sensors", Icon = "🧭", Name = "Capteurs", Feature = "sensors",
                Instruction = "Pendant 20 secondes : bougez et tournez le terminal, couvrez le haut de l'écran (proximité) puis le capteur de lumière." },
            new DeviceTestDef { Id = "nfc", Icon = "📶", Name = "NFC", Feature = "nfc",
                Instruction = "Posez un badge ou une étiquette NFC au dos du terminal (30 secondes)." },
            new DeviceTestDef { Id = "gps", Icon = "🛰️", Name = "GPS", Feature = "gps",
                Instruction = "Placez le terminal près d'une fenêtre ou dehors (2 minutes au plus). En intérieur, voir des satellites suffit à montrer que la puce répond." },
            new DeviceTestDef { Id = "bluetooth", Icon = "🔵", Name = "Bluetooth", Feature = "bluetooth",
                Instruction = "Recherche des appareils Bluetooth proches pendant 15 secondes (allumez un appareil à côté si besoin)." },
            new DeviceTestDef { Id = "keys", Icon = "⌨️", Name = "Clavier physique", Feature = "keyboard", NeedsConfirmation = true,
                Instruction = "Appuyez sur chaque touche du clavier du terminal, puis cliquez sur « Arrêter » et confirmez que toutes les touches sont apparues." },
            new DeviceTestDef { Id = "rfid", Icon = "🏷️", Name = "Lecteur RFID", Feature = "rfid",
                Instruction = "Appuyez sur la gâchette en visant des étiquettes RFID (UHF) : chaque tag lu s'affiche (60 secondes au plus). Proposé seulement si le terminal a un lecteur RFID." },
            new DeviceTestDef { Id = "storage_speed", Icon = "💾", Name = "Vitesse de la mémoire", Feature = "storage",
                Instruction = "Écriture puis lecture de 64 Mo dans la mémoire interne (quelques secondes)." },
            new DeviceTestDef { Id = "scanner", Icon = "🔫", Name = "Lecteur code-barres", Feature = "scanner", External = true,
                Instruction = "Ouvre le test du lecteur : lectures réelles, symbologie, cadence, taux de réussite." },
        };

        public static DeviceTestDef Get(string id) => All.FirstOrDefault(d => d.Id == id);

        /// <summary>Ordre de l'enchaînement : tests courts d'abord, GPS (jusqu'à 2 min) en dernier.</summary>
        public static readonly string[] ChainOrder =
        {
            "screen", "touch", "camera_back", "camera_front", "flash", "speaker", "earpiece", "microphone", "vibrator",
            "sensors", "nfc", "bluetooth", "keys", "rfid", "storage_speed", "gps",
        };
        public const string SystemIdPrefix = "dev_";
    }

    /// <summary>État d'un test dans la fenêtre « Tests du terminal ».</summary>
    public sealed class DeviceTestItem : INotifyPropertyChanged
    {
        public DeviceTestItem(DeviceTestDef def) { Def = def; }

        public DeviceTestDef Def { get; }
        public string Id => Def.Id;
        public string Name => Loc.T(Def.Name);
        public string Icon => Def.Icon;

        private bool _available = true;
        public bool Available { get => _available; set { _available = value; Changed(nameof(Available)); Changed(nameof(SubText)); Changed(nameof(Opacity)); } }

        private string _status = "⚪";
        /// <summary>⚪ non testé, ⏳ en cours, ❓ à confirmer, ✅ ⚠️ ❌ résultat, ➖ non disponible.</summary>
        public string Status { get => _status; set { _status = value; Changed(nameof(Status)); Changed(nameof(SubText)); } }

        private string _details = "";
        public string Details { get => _details; set { _details = value ?? ""; Changed(nameof(Details)); } }

        public string Comment { get; set; } = "";
        public string LocalFile { get; set; }
        /// <summary>Résultat choisi par le technicien pendant le test : un résultat arrivé ensuite ne l'écrase pas.</summary>
        public bool ManualVerdict { get; set; }
        public string Live { get; set; } = "";

        public double Opacity => Available ? 1.0 : 0.45;
        public string SubText
        {
            get
            {
                if (!Available) return Loc.T("Absent sur ce terminal");
                switch (Status)
                {
                    case "⏳": return Loc.T("En cours…");
                    case "❓": return Loc.T("À confirmer");
                    case "✅": return Loc.T("Conforme");
                    case "⚠️": return Loc.T("À surveiller");
                    case "❌": return Loc.T("Non conforme");
                    case "➖": return Loc.T("Non applicable");
                    default: return Def.External ? Loc.T("Fenêtre dédiée") : Loc.T("Non testé");
                }
            }
        }

        /// <summary>Résultat retenu pour le rapport (seulement ✅ ⚠️ ❌).</summary>
        public bool IsReported => Status == "✅" || Status == "⚠️" || Status == "❌";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>Textes des résultats et valeurs en direct (fonctions pures, testables).</summary>
    public static class DeviceTestText
    {
        public static string StatusIcon(string status)
        {
            switch (status)
            {
                case "skip": return "⚪";
                case "ok": return "✅";
                case "warn": return "⚠️";
                case "bad": return "❌";
                case "confirm": return "❓";
                case "na": return "➖";
                default: return "⚪";
            }
        }

        private static string F(double v, string fmt = "0.#") => double.IsNaN(v) ? "?" : v.ToString(fmt, CultureInfo.CurrentCulture);

        public static string Result(DiagMessage m)
        {
            string reason = m.Str("reason");
            if (m.Status == "na") return Loc.T("Non disponible sur ce terminal.");
            if (reason == "permission") return Loc.T("Autorisation refusée par le terminal (profil MDM ?).");
            switch (m.Test)
            {
                case "camera_back":
                case "camera_front":
                    if (m.Status == "bad") return Loc.F("La caméra ne s'ouvre pas ou ne prend pas de photo ({0}).", string.IsNullOrEmpty(reason) ? "?" : reason);
                    string photo = Loc.F("Photo {0} × {1} — luminosité {2}/255, contraste {3}", m.Str("width"), m.Str("height"), m.Str("brightness"), m.Str("contrast"));
                    return m.Status == "warn" ? photo + "\n" + Loc.T("Image trop sombre, trop claire ou uniforme : objectif masqué, sale ou capteur défaillant ?") : photo;
                case "rfid":
                    return m.Status == "ok" ? Loc.F("{0} tag(s) RFID lu(s).", m.Str("tags"))
                        : Loc.T("Aucun tag RFID lu en 60 secondes : étiquette UHF à portée ? lecteur connecté ?");
                case "flash": return m.Status == "confirm" ? Loc.T("Le flash a-t-il clignoté 3 fois ?") : Loc.T("Le flash ne s'allume pas (commande refusée).");
                case "speaker": return m.Status == "confirm" ? Loc.T("Les 3 bips étaient-ils audibles et nets, sans grésillement ?") : Loc.T("Lecture du son impossible.");
                case "earpiece": return m.Status == "confirm" ? Loc.T("Les 3 bips étaient-ils audibles dans l'écouteur ?") : Loc.T("Lecture du son impossible.");
                case "vibrator": return Loc.T("Les 3 vibrations ont-elles été ressenties ?");
                case "microphone":
                    if (reason == "init") return Loc.T("Le micro ne s'ouvre pas.");
                    string level = Loc.F("Niveau maximal : {0} dBFS", F(m.Num("peak_db")));
                    return m.Status == "ok" ? level : m.Status == "warn" ? level + "\n" + Loc.T("Son faible : micro obstrué, sale ou fatigué ? (ou personne n'a parlé)") : level + "\n" + Loc.T("Pratiquement aucun son capté.");
                case "storage_speed":
                    if (m.Status == "bad" && reason == "exception") return Loc.T("Écriture impossible dans la mémoire.");
                    string speed = Loc.F("Écriture {0} Mo/s, lecture {1} Mo/s ({2} Mo)", F(m.Num("write_mbs")), F(m.Num("read_mbs")), m.Str("size_mb"));
                    return m.Status == "ok" ? speed : speed + "\n" + Loc.T("Écriture lente : mémoire presque pleine ou fatiguée ?");
                case "nfc":
                    if (reason == "disabled") return Loc.T("NFC désactivé sur le terminal (et non activable par ADB).");
                    if (reason == "timeout") return Loc.T("Aucun badge présenté en 30 secondes.");
                    var techs = m.HasJson && m.Json.TryGetProperty("techs", out var t) && t.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", t.EnumerateArray().Select(x => x.GetString())) : "";
                    return Loc.F("Badge lu : UID {0} ({1})", m.Str("uid"), techs);
                case "gps":
                    if (reason == "disabled") return Loc.T("Localisation désactivée sur le terminal (et non activable par ADB).");
                    if (m.Status == "ok")
                        return Loc.F("Position obtenue en {0} s — {1} satellite(s) utilisé(s) sur {2}, précision {3} m, meilleur signal {4} dB-Hz",
                            F(m.Num("ttff_s")), m.Str("used"), m.Str("satellites"), m.Str("accuracy_m"), m.Str("best_cn0"));
                    if (m.Status == "warn") return Loc.F("Pas de position en 2 minutes, mais {0} satellite(s) vu(s) (meilleur signal {1} dB-Hz) : normal en intérieur.", m.Str("satellites"), m.Str("best_cn0"));
                    return Loc.T("Aucun satellite détecté en 2 minutes.");
                case "bluetooth":
                    if (reason == "enable") return Loc.T("Le Bluetooth ne s'allume pas.");
                    if (m.Status == "ok") return Loc.F("{0} appareil(s) détecté(s), meilleur signal {1} dBm", m.Str("devices"), m.Str("best_rssi"));
                    if (m.Status == "warn") return Loc.T("Aucun appareil détecté (aucun appareil Bluetooth allumé à proximité ?).");
                    return Loc.T("Recherche Bluetooth impossible.");
                case "keys":
                    if (!m.HasJson || !m.Json.TryGetProperty("keys", out var keys) || keys.GetArrayLength() == 0) return Loc.T("Aucune touche détectée.");
                    var list = keys.EnumerateArray().Select(k => $"{k.GetProperty("key").GetString()} ×{k.GetProperty("count").GetInt32()}");
                    return Loc.F("Touches détectées ({0}) : {1}", keys.GetArrayLength(), string.Join(", ", list)) + "\n" + Loc.T("Toutes les touches du clavier sont-elles dans la liste ?");
                case "sensors":
                    return Sensors(m);
                case "screen": return Loc.T("Couleurs affichées : des pixels morts, taches ou traces ont-ils été vus ?");
                case "touch": return Loc.F("{0} zone(s) sur {1} ont réagi ({2} %).", m.Str("covered"), m.Str("total"), m.Str("percent"));
                case "scanner": return Loc.F("{0} lecture(s), {1} code(s) différent(s) sur le terminal.", m.Str("reads"), m.Str("distinct"));
            }
            return m.Status == "bad" ? Loc.F("Échec du test ({0}).", reason) : "";
        }

        /// <summary>
        /// Texte du rapport une fois le résultat choisi par le technicien : la question (« Le flash a-t-il
        /// clignoté ? ») devient un constat ; un résultat choisi sans mesure est indiqué comme tel.
        /// </summary>
        public static string Confirmed(string testId, string statusIcon, string details)
        {
            details = details ?? "";
            bool ok = statusIcon == "✅";
            string defect = Loc.T("Défaut constaté par le technicien (voir commentaire).");
            switch (testId)
            {
                case "flash": return ok ? Loc.T("Le flash a clignoté normalement (3 fois).") : defect;
                case "speaker": return ok ? Loc.T("Les 3 bips étaient audibles et nets.") : defect;
                case "earpiece": return ok ? Loc.T("Les 3 bips étaient audibles dans l'écouteur.") : defect;
                case "vibrator": return ok ? Loc.T("Les 3 vibrations ont été ressenties.") : defect;
                case "keys":
                    // Liste des touches gardée, sans la question finale.
                    var lines = details.Replace("\r", "").Split('\n').Where(l => l.Length > 0 && l != Loc.T("Toutes les touches du clavier sont-elles dans la liste ?")).ToList();
                    lines.Add(ok ? Loc.T("Toutes les touches répondent.") : Loc.T("Touche(s) défaillante(s) : voir commentaire."));
                    return string.Join("\n", lines);
            }
            return details.Trim().Length > 0 ? details : Loc.T("Résultat constaté par le technicien.");
        }

        public static string SensorName(string key)
        {
            switch (key)
            {
                case "accelerometer": return Loc.T("Accéléromètre");
                case "gyroscope": return Loc.T("Gyroscope");
                case "compass": return Loc.T("Boussole");
                case "light": return Loc.T("Capteur de lumière");
                case "proximity": return Loc.T("Capteur de proximité");
                case "pressure": return Loc.T("Baromètre");
                default: return key;
            }
        }

        private static string Sensors(DiagMessage m)
        {
            if (!m.HasJson || m.Json.ValueKind != JsonValueKind.Object) return "";
            var lines = new List<string>();
            foreach (var s in m.Json.EnumerateObject())
            {
                if (s.Value.ValueKind != JsonValueKind.Object) continue;
                string st = s.Value.TryGetProperty("status", out var x) ? x.GetString() : "";
                string text = st == "ok" ? Loc.T("réagit") : st == "warn" ? Loc.T("aucune réaction (geste fait ?)") : Loc.T("aucune valeur reçue");
                lines.Add($"• {SensorName(s.Name)} : {StatusIcon(st)} {text}");
            }
            return string.Join("\n", lines);
        }

        /// <summary>Valeurs en direct (niveau du micro, satellites, appareils Bluetooth, capteurs…).</summary>
        public static string Live(DiagMessage m)
        {
            switch (m.Test)
            {
                case "rfid": return Loc.F("{0} tag(s) lu(s) — dernier : {1}", m.Str("tags"), m.Str("last"));
                case "microphone":
                    double lvl = m.Num("level_db");
                    int bars = double.IsNaN(lvl) ? 0 : (int)Math.Max(0, Math.Min(30, (lvl + 60) / 2));
                    return Loc.F("Niveau : {0} dBFS  (max {1})", F(lvl), F(m.Num("peak_db"))) + "\n" + new string('█', bars);
                case "gps": return Loc.F("{0} s — {1} satellite(s) vu(s), {2} utilisé(s), meilleur signal {3} dB-Hz", m.Str("seconds"), m.Str("satellites"), m.Str("used"), m.Str("best_cn0"));
                case "bluetooth": return Loc.F("{0} appareil(s) trouvé(s), meilleur signal {1} dBm", m.Str("devices"), m.Str("best_rssi"));
                case "keys": return Loc.F("Dernière touche : {0} — {1} touche(s) différente(s)", m.Str("key"), m.Str("distinct"));
                case "touch": return Loc.F("Zones touchées : {0} / {1}", m.Str("covered"), m.Str("total"));
                case "screen": return Loc.F("Couleur {0} / {1}", m.Str("color"), m.Str("total"));
                case "storage_speed": return Loc.F("Écriture : {0} / {1} Mo", m.Str("mb"), m.Str("total"));
                case "sensors":
                    if (!m.HasJson || m.Json.ValueKind != JsonValueKind.Object) return "";
                    return string.Join("\n", m.Json.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object).Select(p =>
                    {
                        bool reacted = p.Value.TryGetProperty("reacted", out var r) && r.ValueKind == JsonValueKind.True;
                        string v = p.Value.TryGetProperty("value", out var val) ? val.ToString() : "";
                        return $"{(reacted ? "✅" : "…")} {SensorName(p.Name)} : {v}";
                    }));
            }
            return "";
        }
    }
}

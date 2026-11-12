using System;
using System.Collections.Generic;
using System.Linq;

namespace ADBToolsNew.Core
{
    /// <summary>Définition d'une ligne de la fiche de diagnostic SAV.</summary>
    public sealed class DiagLineDef
    {
        public string Id;
        /// <summary>Nom de référence en français (rapport, traduction à l'affichage).</summary>
        public string Name;
        public string Category;
        public string Source;
        public string Instruction;
        public string AppTest;
        public bool PcCheck;
        public string PcLive;
    }

    /// <summary>
    /// Fiche de diagnostic SAV : une ligne par élément du terminal, testée par le PC (check-up, écoute en direct),
    /// par l'appli ADB Tools Diag, ou les deux. Les contrôles du PC gardent leur identifiant (rapport Word) ; les tests
    /// de l'appli sans équivalent PC sont des lignes « dev_… » ; capteurs, boutons, tactile, écran et lecteur
    /// réunissent les deux (une seule ligne).
    /// </summary>
    public static class DiagnosticSheet
    {
        public const string CatSystem = "Système";
        public const string CatScreen = "Écran, tactile et boutons";
        public const string CatReader = "Lecteur";
        public const string CatCameras = "Caméras";
        public const string CatAudio = "Audio et vibreur";
        public const string CatSensors = "Capteurs";
        public const string CatNetwork = "Connexions";

        private const string PC = "PC", Terminal = "terminal", Both = "terminal · PC", PcThenTerminal = "PC · terminal";

        /// <summary>Tests de l'appli qui complètent une ligne du PC au lieu d'avoir la leur.</summary>
        private static readonly HashSet<string> MergedAppTests = new HashSet<string> { "keys", "touch", "screen", "scanner", "sensors" };

        public static string LineIdForAppTest(string appTest) =>
            MergedAppTests.Contains(appTest) ? appTest : DeviceTestCatalog.SystemIdPrefix + appTest;

        private static DiagLineDef Pc(string id, string name, string cat, string instruction) =>
            new DiagLineDef { Id = id, Name = name, Category = cat, Source = PC, Instruction = instruction, PcCheck = true };

        private static DiagLineDef App(string appTest, string cat)
        {
            var d = DeviceTestCatalog.Get(appTest);
            return new DiagLineDef { Id = DeviceTestCatalog.SystemIdPrefix + appTest, Name = d?.Name ?? appTest, Category = cat, Source = Terminal, Instruction = d?.Instruction ?? "", AppTest = appTest };
        }

        public static readonly List<DiagLineDef> Lines = new List<DiagLineDef>
        {
            Pc("auth", "Autorisation ADB & Connexion", CatSystem, "Contrôle automatique du PC : connexion ADB, modèle, version d'Android, numéro de série."),
            Pc("root", "Privilèges Root", CatSystem, "Contrôle automatique du PC : le terminal est-il rooté ?"),
            Pc("battery", "État & Santé Batterie", CatSystem, "Contrôle automatique du PC : niveau, santé, température et usure de la batterie."),
            Pc("storage", "Stockage Interne", CatSystem, "Contrôle automatique du PC : espace libre de la mémoire interne."),
            App("storage_speed", CatSystem),
            Pc("thermal", "Diagnostic Thermique", CatSystem, "Contrôle automatique du PC : températures relevées par Android."),
            Pc("crashes", "Historique Crashs / ANR", CatSystem, "Contrôle automatique du PC : plantages et blocages d'applications récents."),

            new DiagLineDef { Id = "screen", Name = "Écran : pixels morts / brûlure", Category = CatScreen, Source = Both, AppTest = "screen", PcLive = "screen",
                Instruction = "Couleurs unies en plein écran, dans l'appli ou envoyées depuis le PC : cherchez pixels morts, taches ou traces de brûlure." },
            new DiagLineDef { Id = "touch", Name = "Dalle tactile", Category = CatScreen, Source = Both, AppTest = "touch", PcLive = "getevent",
                Instruction = "Grille à balayer du doigt dans l'appli, ou suivi des zones touchées depuis le PC (écoute en direct)." },
            new DiagLineDef { Id = "keys", Name = "Boutons physiques", Category = CatScreen, Source = Both, AppTest = "keys", PcLive = "getevent",
                Instruction = "Appuyez sur chaque touche : dans l'appli (Touches) ou en écoute directe depuis le PC. Marche : une pression (l'écran s'éteint), puis rallumez." },

            new DiagLineDef { Id = "scanner", Name = "Lecteur code-barres", Category = CatReader, Source = Both, AppTest = "scanner", PcLive = "scan",
                Instruction = "Lisez des codes-barres : test du lecteur dans l'appli, ou lectures en direct sur le PC (données, symbologie, cadence)." },
            App("rfid", CatReader),

            App("camera_back", CatCameras),
            App("camera_front", CatCameras),
            App("flash", CatCameras),

            App("speaker", CatAudio),
            App("earpiece", CatAudio),
            App("microphone", CatAudio),
            App("vibrator", CatAudio),

            new DiagLineDef { Id = "sensors", Name = "Capteurs", Category = CatSensors, Source = PcThenTerminal, AppTest = "sensors", PcCheck = true,
                Instruction = "Relevé du PC (capteurs présents et valeurs) et test de réaction dans l'appli : bougez le terminal, couvrez les capteurs." },

            Pc("wifi", "Interface Wi-Fi", CatNetwork, "Contrôle automatique du PC : état et qualité de la connexion Wi-Fi."),
            Pc("ping", "Ping Réseau (Local & Internet)", CatNetwork, "Contrôle automatique du PC : réponse du réseau local et d'Internet."),
            App("bluetooth", CatNetwork),
            App("nfc", CatNetwork),
            App("gps", CatNetwork),
        };

        /// <summary>Fiche vierge, dans l'ordre d'affichage (les catégories sont traduites ici).</summary>
        public static List<DiagComponent> CreateLines() => Lines.Select(d => new DiagComponent
        {
            SystemId = d.Id,
            StatusIcon = "⚪",
            ComponentName = d.Name,
            Details = "",
            Comment = "",
            Category = Loc.T(d.Category),
            Source = d.Source,
            Instruction = d.Instruction,
            AppTest = d.AppTest,
            PcCheck = d.PcCheck,
            PcLive = d.PcLive,
        }).ToList();

        /// <summary>Statut en clair (historique, fenêtre de récupération).</summary>
        public static string StatusWord(string icon)
        {
            switch (icon)
            {
                case "✅": return Loc.T("Conforme");
                case "⚠️": return Loc.T("À surveiller");
                case "❌": return Loc.T("Non conforme");
                case "❓": return Loc.T("À confirmer");
                case "➖": return Loc.T("Absent");
                case "⏳": return Loc.T("En cours");
                case "🔌": return Loc.T("Terminal déconnecté");
                default: return Loc.T("Non testé");
            }
        }

        public static bool IsDone(string icon) => icon == "✅" || icon == "⚠️" || icon == "❌";
    }

    /// <summary>Grille tactile balayée dans l'appli : « grid » = une case par zone, ligne par ligne (1 = touchée).</summary>
    public sealed class TouchMap
    {
        public int Cols { get; private set; }
        public int Rows { get; private set; }
        public string Map { get; private set; }
        public int Covered => Map.Count(c => c == '1');
        public bool Touched(int index) => index >= 0 && index < Map.Length && Map[index] == '1';

        /// <summary>Résultat « touch » de l'appli → grille ; null si le résultat n'en contient pas (anciennes versions).</summary>
        public static TouchMap Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("cols", out var c) || !c.TryGetInt32(out int cols)) return null;
                    if (!r.TryGetProperty("rows", out var rw) || !rw.TryGetInt32(out int rows)) return null;
                    string map = r.TryGetProperty("grid", out var g) ? g.GetString() ?? "" : "";
                    if (cols <= 0 || rows <= 0 || cols > 50 || rows > 100 || map.Length != cols * rows) return null;
                    return new TouchMap { Cols = cols, Rows = rows, Map = map };
                }
            }
            catch { return null; }
        }
    }
}

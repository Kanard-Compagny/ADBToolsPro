using System.Collections.ObjectModel;

namespace ADBToolsNew.Core
{
    /// <summary>Reconnaît les touches correspondant à une gâchette de scanner optique, à partir de leur nom brut getevent.</summary>
    public static class ScanTriggerKeys
    {
        /// <summary>
        /// Détecte les gâchettes scan par mot-clé plutôt que par une liste figée de modèles : sur nos
        /// terminaux Zebra réels, les gâchettes remontent en "TL"/"TR" ou "TRIGGER_HAPPYxx" selon le
        /// firmware. D'autres marques peuvent nommer différemment (d'où "SCAN" en repli), mais faute de
        /// terminal non-Zebra sous la main pour vérifier, cette liste reste à affiner à l'usage.
        /// </summary>
        public static bool IsScanTrigger(string rawKeyName)
        {
            if (string.IsNullOrEmpty(rawKeyName)) return false;
            string upper = rawKeyName.ToUpperInvariant();
            return upper == "TL" || upper == "TR"
                || upper.EndsWith("_TL") || upper.EndsWith("_TR")          // BTN_TL / BTN_TR (Zebra TC53)
                || upper.Contains("TRIGGER")
                || upper.Contains("SCAN");
        }
    }

    /// <summary>
    /// Historique des touches physiques détectées pendant une session d'écoute "getevent". Ne présuppose
    /// aucune liste de boutons attendue (le parc gère des marques/modèles trop variés pour ça) : chaque
    /// touche distincte pressée apparaît au fur et à mesure, avec son nombre d'appuis — au technicien de
    /// constater si un bouton attendu sur ce modèle n'apparaît jamais dans la liste.
    /// </summary>
    public class KeyPressHistory
    {
        public class Entry
        {
            public string RawKeyName;
            public string DisplayName;
            public int Count;

            /// <summary>Libellé prêt à l'affichage (évite tout binding XAML avec StringFormat imbriqué).</summary>
            public string Label => $"{DisplayName} ×{Count}";
        }

        private readonly System.Collections.Generic.Dictionary<string, Entry> _byRawName = new System.Collections.Generic.Dictionary<string, Entry>();

        public ObservableCollection<Entry> Entries { get; } = new ObservableCollection<Entry>();

        /// <summary>Enregistre un appui. Retourne true si le compteur d'une entrée existante a changé (nécessite un rafraîchissement visuel manuel).</summary>
        public bool RecordPress(string rawKeyName, string displayName)
        {
            if (_byRawName.TryGetValue(rawKeyName, out var entry))
            {
                entry.Count++;
                return true;
            }

            entry = new Entry { RawKeyName = rawKeyName, DisplayName = displayName, Count = 1 };
            _byRawName[rawKeyName] = entry;
            Entries.Add(entry);
            return false;
        }

        public void Reset()
        {
            Entries.Clear();
            _byRawName.Clear();
        }
    }
}

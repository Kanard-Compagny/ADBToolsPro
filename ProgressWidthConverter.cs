using System;
using System.Globalization;
using System.Windows.Data;

namespace ADBToolsNew
{
    /// <summary>
    /// Calcule explicitement la largeur en pixels du remplissage d'une ProgressBar personnalisée
    /// (Value/Maximum/ActualWidth du contrôle) plutôt que de compter sur le mécanisme interne
    /// PART_Indicator de WPF, dont le comportement exact avec un template personnalisé n'est pas garanti.
    /// </summary>
    public class ProgressWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 3) return 0.0;
            // Lecture directe des doubles : l'ancien aller-retour texte (ToString en culture fr-FR "312,5",
            // relu en culture invariante où la virgule sépare les milliers) transformait 312,5 en 3125 et la
            // barre paraissait pleine dès 10 % avec une mise à l'échelle Windows de 125 % / 150 %.
            if (!TryGetDouble(values[0], out double val)) return 0.0;
            if (!TryGetDouble(values[1], out double max)) return 0.0;
            if (!TryGetDouble(values[2], out double totalWidth)) return 0.0;

            if (max <= 0 || double.IsNaN(totalWidth) || totalWidth <= 0) return 0.0;
            double fraction = Math.Max(0, Math.Min(1, val / max));
            return totalWidth * fraction;
        }

        private static bool TryGetDouble(object value, out double result)
        {
            switch (value)
            {
                case double d: result = d; return !double.IsNaN(d);
                case int i: result = i; return true;
                case float f: result = f; return !float.IsNaN(f);
                case IConvertible c:
                    try { result = c.ToDouble(CultureInfo.InvariantCulture); return !double.IsNaN(result); }
                    catch { break; }
            }
            result = 0;
            return false;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}

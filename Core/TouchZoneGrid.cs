using System;
using System.Text.RegularExpressions;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Grille logique de zones tactiles (pixels/zones morts). Reçoit des positions normalisées (0..1) issues
    /// du flux "getevent" et retient quelles cellules ont déjà été touchées, pour détecter les zones jamais
    /// atteintes lors d'un balayage complet de l'écran par le technicien.
    /// </summary>
    public class TouchZoneGrid
    {
        public int Columns { get; }
        public int Rows { get; }
        public int TouchedCount { get; private set; }
        public int TotalCount => Columns * Rows;

        private readonly bool[,] _touched;

        public TouchZoneGrid(int columns, int rows)
        {
            Columns = Math.Max(1, columns);
            Rows = Math.Max(1, rows);
            _touched = new bool[Rows, Columns];
        }

        /// <summary>Enregistre une position normalisée (0..1, origine en haut à gauche). Retourne true si cette cellule n'avait encore jamais été touchée.</summary>
        public bool RecordTouch(double normalizedX, double normalizedY, out int row, out int col)
        {
            col = Clamp((int)(normalizedX * Columns), 0, Columns - 1);
            row = Clamp((int)(normalizedY * Rows), 0, Rows - 1);

            if (_touched[row, col]) return false;

            _touched[row, col] = true;
            TouchedCount++;
            return true;
        }

        public bool IsTouched(int row, int col) => _touched[row, col];

        /// <summary>
        /// Calcule un nombre de lignes/colonnes qui respecte le ratio réel de l'écran (portrait ou paysage),
        /// pour un nombre de colonnes cible donné. Évite qu'une zone "en haut à droite" sur la grille ne
        /// corresponde en réalité au centre de l'écran du terminal.
        /// </summary>
        public static (int Columns, int Rows) ComputeDimensions(int screenWidth, int screenHeight, int targetColumns = 6)
        {
            if (screenWidth <= 0 || screenHeight <= 0) return (targetColumns, targetColumns * 2);

            double aspect = (double)screenWidth / screenHeight;
            int rows = (int)Math.Round(targetColumns / aspect);
            rows = Math.Max(6, Math.Min(18, rows));
            return (targetColumns, rows);
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }

    /// <summary>
    /// Lit la plage réelle (min/max) des coordonnées brutes remontées par le digitizer tactile, via
    /// "getevent -lp". Nécessaire car ces valeurs ne correspondent pas toujours à la résolution d'affichage
    /// (ex: digitizer en 4095x4095 pour un écran 1080x1920) : sans cette étape, les touches seraient mal
    /// réparties dans la grille.
    /// </summary>
    public static class TouchCalibration
    {
        private static readonly Regex AbsXRegex = new Regex(@"ABS_MT_POSITION_X\s*:\s*value\s+-?\d+,\s*min\s+(-?\d+),\s*max\s+(-?\d+)");
        private static readonly Regex AbsYRegex = new Regex(@"ABS_MT_POSITION_Y\s*:\s*value\s+-?\d+,\s*min\s+(-?\d+),\s*max\s+(-?\d+)");

        public static (int MinX, int MaxX, int MinY, int MaxY)? Parse(string geteventLpOutput)
        {
            string output = geteventLpOutput ?? "";
            var matchX = AbsXRegex.Match(output);
            var matchY = AbsYRegex.Match(output);

            if (matchX.Success && matchY.Success
                && int.TryParse(matchX.Groups[1].Value, out int minX) && int.TryParse(matchX.Groups[2].Value, out int maxX)
                && int.TryParse(matchY.Groups[1].Value, out int minY) && int.TryParse(matchY.Groups[2].Value, out int maxY)
                && maxX > minX && maxY > minY)
            {
                return (minX, maxX, minY, maxY);
            }

            return null;
        }
    }
}

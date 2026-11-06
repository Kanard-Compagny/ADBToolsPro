using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Graphiques des rapports Word, dessinés pour l'impression : fond blanc, couleurs claires, quel que soit le
    /// thème de l'application (avant, on recopiait les graphiques de l'écran, aux couleurs du thème sombre).
    /// L'échelle s'adapte aux valeurs mesurées : un pic à 239 ms n'est plus coupé à 200.
    /// Toute la session est tracée (et non les 40 derniers points affichés à l'écran).
    /// </summary>
    public static class ReportChartRenderer
    {
        private static readonly Color Grid = Color.FromRgb(0xDD, 0xE3, 0xE0);
        private static readonly Color Axis = Color.FromRgb(0xB7, 0xC2, 0xBD);
        private static readonly Color Label = Color.FromRgb(0x5F, 0x6B, 0x66);
        private static readonly Color Lost = Color.FromRgb(0xC6, 0x28, 0x28);

        public static readonly Color PingColor = Color.FromRgb(0x14, 0x5C, 0x43);   // vert de la charte
        public static readonly Color RssiColor = Color.FromRgb(0xD9, 0x6C, 0x0B);   // orange bec de canard, assombri pour l'impression

        /// <summary>Latence : une valeur par ping, null pour un ping perdu (marqué en rouge sur l'axe).</summary>
        public static string RenderPing(IList<int?> samples, string path)
        {
            var ok = samples.Where(v => v.HasValue).Select(v => (double)v.Value).ToList();
            double max = ok.Count == 0 ? 100 : NiceCeiling(Math.Max(ok.Max() * 1.1, 50));
            return Render(samples.Select(v => v.HasValue ? (double?)v.Value : null).ToList(), 0, max, "ms", PingColor, true, path);
        }

        /// <summary>Signal Wi-Fi (dBm, valeurs négatives).</summary>
        public static string RenderRssi(IList<int> samples, string path)
        {
            double min = -100, max = -30;
            if (samples.Count > 0)
            {
                min = Math.Min(-90, Math.Floor((samples.Min() - 5) / 10.0) * 10);
                max = Math.Max(-30, Math.Ceiling((samples.Max() + 5) / 10.0) * 10);
            }
            return Render(samples.Select(v => (double?)v).ToList(), min, max, "dBm", RssiColor, false, path);
        }

        private static double NiceCeiling(double v)
        {
            double step = v <= 100 ? 20 : v <= 300 ? 50 : v <= 1000 ? 100 : 500;
            return Math.Ceiling(v / step) * step;
        }

        private static string Render(List<double?> values, double min, double max, string unit, Color color, bool markGaps, string path)
        {
            const int W = 1600, H = 520;
            const double left = 150, right = 30, top = 24, bottom = 40;
            double plotW = W - left - right, plotH = H - top - bottom;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double range = Math.Max(1e-6, max - min);
            double X(int i) => left + (values.Count <= 1 ? plotW / 2 : plotW * i / (values.Count - 1));
            double Y(double v) => top + plotH * (1 - (Math.Max(min, Math.Min(max, v)) - min) / range);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));

                // Grille horizontale + graduations (5 intervalles)
                var gridPen = Frozen(new Pen(new SolidColorBrush(Grid), 1.5));
                for (int k = 0; k <= 5; k++)
                {
                    double v = min + range * k / 5;
                    double y = Y(v);
                    dc.DrawLine(gridPen, new Point(left, y), new Point(left + plotW, y));
                    var ft = Text($"{v.ToString("0", CultureInfo.InvariantCulture)} {unit}", typeface, 26, Label);
                    dc.DrawText(ft, new Point(left - 14 - ft.Width, y - ft.Height / 2));
                }
                dc.DrawLine(Frozen(new Pen(new SolidColorBrush(Axis), 2)), new Point(left, top + plotH), new Point(left + plotW, top + plotH));

                // Courbe (segments continus ; un ping perdu interrompt la courbe)
                var fill = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)); fill.Freeze();
                var line = Frozen(new Pen(new SolidColorBrush(color), 3) { LineJoin = PenLineJoin.Round });
                int i0 = 0;
                while (i0 < values.Count)
                {
                    while (i0 < values.Count && !values[i0].HasValue) i0++;
                    int i1 = i0;
                    while (i1 < values.Count && values[i1].HasValue) i1++;
                    if (i1 > i0)
                    {
                        var area = new StreamGeometry();
                        var stroke = new StreamGeometry();
                        using (var a = area.Open())
                        using (var s = stroke.Open())
                        {
                            a.BeginFigure(new Point(X(i0), top + plotH), true, true);
                            s.BeginFigure(new Point(X(i0), Y(values[i0].Value)), false, false);
                            for (int i = i0; i < i1; i++)
                            {
                                var p = new Point(X(i), Y(values[i].Value));
                                a.LineTo(p, false, false);
                                if (i > i0) s.LineTo(p, true, true);
                            }
                            a.LineTo(new Point(X(i1 - 1), top + plotH), false, false);
                        }
                        area.Freeze(); stroke.Freeze();
                        dc.DrawGeometry(fill, null, area);
                        dc.DrawGeometry(null, line, stroke);
                        if (i1 - i0 == 1) dc.DrawEllipse(new SolidColorBrush(color), null, new Point(X(i0), Y(values[i0].Value)), 4, 4);
                    }
                    i0 = i1;
                }

                // Pings perdus : petit repère rouge sur l'axe
                if (markGaps)
                {
                    var lost = new SolidColorBrush(Lost); lost.Freeze();
                    for (int i = 0; i < values.Count; i++)
                        if (!values[i].HasValue) dc.DrawRectangle(lost, null, new Rect(X(i) - 3, top + plotH - 18, 6, 18));
                }

                if (values.Count == 0)
                {
                    var ft = Text(Loc.T("Aucune mesure"), typeface, 26, Label);
                    dc.DrawText(ft, new Point(left + (plotW - ft.Width) / 2, top + (plotH - ft.Height) / 2));
                }
            }

            var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

        /// <summary>
        /// Frise d'un relevé de logs pour le rapport Word : lignes (vert) et erreurs (rouge), puis un couloir par sujet
        /// (redémarrages, plantages, Wi-Fi, mémoire, batterie, lecteur) et les incidents signalés.
        /// </summary>
        public static string RenderTimeline(IList<LogArchive.TimelineBucket> buckets, IList<string> laneNames, string path)
        {
            const int W = 1600, H = 560;
            const double left = 240, right = 20, top = 16, bottom = 40;
            double plotW = W - left - right;
            double barsH = 210, laneH = (H - top - bottom - barsH - 14) / Math.Max(1, laneNames.Count);
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
                int n = Math.Max(1, buckets.Count);
                double bw = plotW / n;
                int maxL = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(x => x.Lines)), maxE = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(x => x.Errors));
                var green = Frozen(new SolidColorBrush(Color.FromArgb(0xAA, 0x73, 0xC9, 0x9C)));
                var red = Frozen(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)));
                var orange = Frozen(new SolidColorBrush(RssiColor));
                var gridPen = Frozen(new Pen(new SolidColorBrush(Grid), 1.5));
                var lbl = Text(Loc.T("Lignes / erreurs"), typeface, 24, Label);
                dc.DrawText(lbl, new Point(left - 14 - lbl.Width, top + barsH / 2 - lbl.Height / 2));
                dc.DrawLine(gridPen, new Point(left, top + barsH), new Point(left + plotW, top + barsH));
                for (int i = 0; i < buckets.Count; i++)
                {
                    var b = buckets[i];
                    double x = left + i * bw;
                    if (b.Lines > 0) { double h = Math.Max(1, barsH * Math.Sqrt(b.Lines / (double)maxL)); dc.DrawRectangle(green, null, new Rect(x, top + barsH - h, Math.Max(1, bw - 1), h)); }
                    if (b.Errors > 0) { double h = Math.Max(2, barsH * 0.85 * Math.Sqrt(b.Errors / (double)maxE)); dc.DrawRectangle(red, null, new Rect(x, top + barsH - h, Math.Max(1, bw - 1), h)); }
                    if (b.Incidents > 0) dc.DrawRectangle(orange, null, new Rect(x + bw / 2 - 2, top, 4, H - top - bottom));
                }
                Color[] laneColors = { Color.FromRgb(0x2F, 0x6F, 0xB3), Color.FromRgb(0xC6, 0x28, 0x28), RssiColor, PingColor, RssiColor, Color.FromRgb(0x8E, 0x24, 0xAA) };
                for (int l = 0; l < laneNames.Count; l++)
                {
                    double y = top + barsH + 14 + l * laneH;
                    dc.DrawLine(gridPen, new Point(left, y + laneH), new Point(left + plotW, y + laneH));
                    var ft = Text(laneNames[l], typeface, 22, Label);
                    dc.DrawText(ft, new Point(left - 14 - ft.Width, y + laneH / 2 - ft.Height / 2));
                    int maxLane = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(x => l < x.Lanes.Length ? x.Lanes[l] : 0));
                    for (int i = 0; i < buckets.Count; i++)
                    {
                        int c = l < buckets[i].Lanes.Length ? buckets[i].Lanes[l] : 0;
                        if (c == 0) continue;
                        var col = laneColors[l % laneColors.Length];
                        byte alpha = (byte)(90 + 165 * Math.Sqrt(c / (double)maxLane));
                        var br = Frozen(new SolidColorBrush(Color.FromArgb(alpha, col.R, col.G, col.B)));
                        dc.DrawRectangle(br, null, new Rect(left + i * bw, y + 3, Math.Max(3, bw - 1), Math.Max(4, laneH - 6)));
                    }
                }
                if (buckets.Count > 0)
                {
                    var a = Text(LogArchive.MinuteText(buckets[0].From), typeface, 22, Label);
                    var z = Text(LogArchive.MinuteText(buckets[buckets.Count - 1].To), typeface, 22, Label);
                    dc.DrawText(a, new Point(left, H - bottom + 8));
                    dc.DrawText(z, new Point(left + plotW - z.Width, H - bottom + 8));
                }
            }
            var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }

        private static SolidColorBrush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

        private static Pen Frozen(Pen p) { p.Freeze(); return p; }

        private static FormattedText Text(string s, Typeface tf, double size, Color c)
        {
#pragma warning disable CS0618 // constructeur sans PixelsPerDip : suffisant pour une image à 96 ppp
            return new FormattedText(s, CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight, tf, size, new SolidColorBrush(c));
#pragma warning restore CS0618
        }
    }
}

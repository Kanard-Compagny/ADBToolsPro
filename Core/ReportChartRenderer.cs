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
                    var ft = Text("Aucune mesure", typeface, 26, Label);
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

        private static Pen Frozen(Pen p) { p.Freeze(); return p; }

        private static FormattedText Text(string s, Typeface tf, double size, Color c)
        {
#pragma warning disable CS0618 // constructeur sans PixelsPerDip : suffisant pour une image à 96 ppp
            return new FormattedText(s, CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight, tf, size, new SolidColorBrush(c));
#pragma warning restore CS0618
        }
    }
}

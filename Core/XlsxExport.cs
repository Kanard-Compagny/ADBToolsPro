using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Petit classeur Excel (.xlsx) d'un seul tableau : lignes de titre facultatives, en-tête vert de la marque,
    /// en-tête figé, filtres automatiques et largeurs de colonnes ajustées au contenu. Texte uniquement (les
    /// valeurs sont écrites telles quelles, sans formule), via DocumentFormat.OpenXml déjà utilisé pour Word.
    /// </summary>
    public static class XlsxExport
    {
        // Index des styles (voir BuildStylesheet) : 0 normal, 1 en-tête, 2 titre, 3 vert (réussi), 4 jaune (à vérifier), 5 rouge (échec).
        public const uint StyleNormal = 0, StyleHeader = 1, StyleTitle = 2, StyleOk = 3, StyleWarn = 4, StyleBad = 5;

        /// <param name="titleLines">Lignes au-dessus du tableau (titre, paquet, date…), la première en gras.</param>
        /// <param name="rowStyle">Style de chaque ligne de données (ex. couleur selon le résultat), facultatif.</param>
        public static void Write(string path, string sheetName, IList<string> titleLines, IList<string> headers,
                                 IList<string[]> rows, Func<int, uint> rowStyle = null)
        {
            using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook();
                var styles = wbPart.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = BuildStylesheet();
                styles.Stylesheet.Save();

                var wsPart = wbPart.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                uint r = 0;
                titleLines = titleLines ?? new List<string>();
                for (int i = 0; i < titleLines.Count; i++)
                    data.Append(MakeRow(++r, new[] { titleLines[i] }, i == 0 ? StyleTitle : StyleNormal));
                if (titleLines.Count > 0) r++; // ligne vide entre le titre et le tableau
                uint headerRow = ++r;
                data.Append(MakeRow(headerRow, headers, StyleHeader));
                for (int i = 0; i < rows.Count; i++)
                    data.Append(MakeRow(++r, rows[i], rowStyle?.Invoke(i) ?? StyleNormal));

                var ws = new Worksheet();
                // En-tête figé : il reste visible quand on fait défiler un grand parc.
                ws.Append(new SheetViews(new SheetView(new Pane
                {
                    VerticalSplit = headerRow, TopLeftCell = "A" + (headerRow + 1), ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen
                }) { WorkbookViewId = 0U }));
                ws.Append(BuildColumns(headers, rows));
                ws.Append(data);
                string lastCol = ColumnName(headers.Count - 1);
                ws.Append(new AutoFilter { Reference = $"A{headerRow}:{lastCol}{Math.Max(headerRow, r)}" });
                wsPart.Worksheet = ws;
                wsPart.Worksheet.Save();

                var sheets = wbPart.Workbook.AppendChild(new Sheets());
                sheets.Append(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1U, Name = SafeSheetName(sheetName) });
                // Plage du filtre déclarée comme le fait Excel (nom masqué _FilterDatabase) : filtres actifs à l'ouverture.
                wbPart.Workbook.Append(new DefinedNames(new DefinedName($"'{SafeSheetName(sheetName)}'!$A${headerRow}:${lastCol}${Math.Max(headerRow, r)}")
                { Name = "_xlnm._FilterDatabase", LocalSheetId = 0U, Hidden = true }));
                wbPart.Workbook.Save();
            }
        }

        private static Row MakeRow(uint index, IList<string> values, uint style)
        {
            var row = new Row { RowIndex = index };
            for (int c = 0; c < values.Count; c++)
            {
                row.Append(new Cell
                {
                    CellReference = ColumnName(c) + index,
                    DataType = CellValues.InlineString,
                    StyleIndex = style,
                    InlineString = new InlineString(new Text(Clean(values[c])) { Space = SpaceProcessingModeValues.Preserve })
                });
            }
            return row;
        }

        private static Columns BuildColumns(IList<string> headers, IList<string[]> rows)
        {
            var cols = new Columns();
            for (int c = 0; c < headers.Count; c++)
            {
                int longest = Math.Max(headers[c].Length, rows.Select(x => c < x.Length ? (x[c] ?? "").Length : 0).DefaultIfEmpty(0).Max());
                double width = Math.Min(70, Math.Max(10, longest * 1.1 + 2));
                cols.Append(new Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = width, CustomWidth = true });
            }
            return cols;
        }

        private static Stylesheet BuildStylesheet()
        {
            var fonts = new Fonts(
                new Font(new FontSize { Val = 10.5 }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 10.5 }, new Color { Rgb = "FFFFFFFF" }, new FontName { Val = "Calibri" }),
                new Font(new Bold(), new FontSize { Val = 13 }, new Color { Rgb = "FF145C43" }, new FontName { Val = "Calibri" }));
            var fills = new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                Solid("FF1F7454"),   // 2 : vert marque (en-tête)
                Solid("FFE3F4EA"),   // 3 : vert pâle (réussi)
                Solid("FFFFF4D6"),   // 4 : jaune pâle (à vérifier)
                Solid("FFFBE3E1"));  // 5 : rouge pâle (échec)
            var borders = new Borders(
                new Border(),
                new Border(new LeftBorder(), new RightBorder(), new TopBorder(),
                           new BottomBorder(new Color { Rgb = "FFD5DED9" }) { Style = BorderStyleValues.Thin }, new DiagonalBorder()));
            var formats = new CellFormats(
                Fmt(0, 0, 1),     // 0 normal
                Fmt(1, 2, 0),     // 1 en-tête
                Fmt(2, 0, 0),     // 2 titre
                Fmt(0, 3, 1),     // 3 réussi
                Fmt(0, 4, 1),     // 4 à vérifier
                Fmt(0, 5, 1));    // 5 échec
            return new Stylesheet(fonts, fills, borders, formats);
        }

        private static Fill Solid(string argb) =>
            new Fill(new PatternFill(new ForegroundColor { Rgb = argb }, new BackgroundColor { Indexed = 64U }) { PatternType = PatternValues.Solid });

        private static CellFormat Fmt(uint font, uint fill, uint border) =>
            new CellFormat(new Alignment { Vertical = VerticalAlignmentValues.Center, WrapText = false })
            { FontId = font, FillId = fill, BorderId = border, ApplyFont = true, ApplyFill = fill > 0, ApplyBorder = border > 0, ApplyAlignment = true };

        /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
        public static string ColumnName(int index)
        {
            string name = "";
            for (int n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
            return name;
        }

        private static string SafeSheetName(string name)
        {
            var clean = new string((name ?? "Feuille").Where(ch => "[]:*?/\\'".IndexOf(ch) < 0).ToArray()).Trim();
            if (clean.Length == 0) clean = "Feuille";
            return clean.Length > 31 ? clean.Substring(0, 31) : clean;
        }

        // Caractères de contrôle interdits dans le XML d'Excel (hors tabulation et sauts de ligne).
        private static string Clean(string s) =>
            new string((s ?? "").Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ').ToArray());
    }
}

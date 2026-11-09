using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ADBToolsNew.Core;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class XlsxExportTests
    {
        [TestMethod]
        public void ColumnNames()
        {
            Assert.AreEqual("A", XlsxExport.ColumnName(0));
            Assert.AreEqual("Z", XlsxExport.ColumnName(25));
            Assert.AreEqual("AA", XlsxExport.ColumnName(26));
            Assert.AreEqual("AZ", XlsxExport.ColumnName(51));
        }

        [TestMethod]
        public void Workbook_IsValidAndReadable()
        {
            string path = Path.Combine(Path.GetTempPath(), "adbt_xlsx_" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                var rows = new List<string[]>
                {
                    new[] { "N° masqué A", "TC53", "11-47-20.00", "14-35-10.00", "✅ À jour" },
                    new[] { "N° masqué B", "TC58", "14-35-10.00", "14-35-10.00", "⚠️ Version inchangée\u0001" },
                };
                XlsxExport.Write(path, "Mise à jour OS", new[] { "Mise à jour OS", "Paquet : OS_Update.zip" },
                    new[] { "N° de série", "Modèle", "Avant", "Après", "Résultat" }, rows,
                    i => i == 0 ? XlsxExport.StyleOk : XlsxExport.StyleWarn);

                using (var doc = SpreadsheetDocument.Open(path, false))
                {
                    var errors = new OpenXmlValidator().Validate(doc).Select(e => e.Description).ToList();
                    Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
                    var ws = doc.WorkbookPart.WorksheetParts.First().Worksheet;
                    var texts = ws.Descendants<Text>().Select(t => t.Text).ToList();
                    CollectionAssert.Contains(texts, "TC58");
                    CollectionAssert.Contains(texts, "⚠️ Version inchangée"); // caractère de contrôle retiré
                    Assert.AreEqual("A4:E6", ws.Descendants<AutoFilter>().Single().Reference.Value);
                }
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}

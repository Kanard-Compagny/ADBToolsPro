using System.Collections.Generic;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class AdbManagerTests
    {
        /// <summary>Découpe une ligne de commande comme Windows (CommandLineToArgvW), pour vérifier l'échappement.</summary>
        private static List<string> SplitWindowsCommandLine(string s)
        {
            var result = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && s[i] == ' ') i++;
                if (i >= s.Length) break;
                var sb = new System.Text.StringBuilder();
                bool inQuotes = false;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '\\')
                    {
                        int n = 0;
                        while (i < s.Length && s[i] == '\\') { n++; i++; }
                        if (i < s.Length && s[i] == '"')
                        {
                            sb.Append('\\', n / 2);
                            if (n % 2 == 1) { sb.Append('"'); i++; }
                        }
                        else sb.Append('\\', n);
                        continue;
                    }
                    if (c == '"') { inQuotes = !inQuotes; i++; continue; }
                    if (c == ' ' && !inQuotes) break;
                    sb.Append(c);
                    i++;
                }
                result.Add(sb.ToString());
            }
            return result;
        }

        [DataTestMethod]
        [DataRow(@"E:\")]
        [DataRow(@"C:\Users\Demo\Mes Docs\")]
        [DataRow(@"C:\a b\c.txt")]
        [DataRow("weird\"quote")]
        [DataRow(@"\\serveur\partage\")]
        [DataRow("")]
        public void QuoteLocalArg_SurvitAuDecoupageWindows(string path)
        {
            var args = SplitWindowsCommandLine("pull " + AdbManager.QuoteLocalArg(path) + " " + AdbManager.QuoteLocalArg("suivant"));

            Assert.AreEqual(3, args.Count);
            Assert.AreEqual(path, args[1]);
            Assert.AreEqual("suivant", args[2]);
        }

        [TestMethod]
        public void QuoteRemoteArg_EchappeApostrophe()
        {
            Assert.AreEqual("'l'\\''appli'", AdbManager.QuoteRemoteArg("l'appli"));
        }

        [TestMethod]
        public void SplitSections_DecoupeLesSorties()
        {
            var s = AdbManager.SplitSections("level: 80\r\n__ADBTOOLS_SECTION__\r\nMemTotal: 1\n__ADBTOOLS_SECTION__\n\n__ADBTOOLS_SECTION__\n/data 40%\n", 4);

            Assert.AreEqual(4, s.Length);
            Assert.AreEqual("level: 80", s[0]);
            Assert.AreEqual("MemTotal: 1", s[1]);
            Assert.AreEqual("", s[2]);
            Assert.AreEqual("/data 40%", s[3]);
        }

        [TestMethod]
        public void SplitSections_SortieTronquee_SectionsManquantesVides()
        {
            var s = AdbManager.SplitSections("seulement une", 3);
            CollectionAssert.AreEqual(new[] { "seulement une", "", "" }, s);
        }

        [TestMethod]
        public void ParseGetprop_LitClesEtValeurs()
        {
            var p = AdbManager.ParseGetprop("[ro.product.model]: [TC53]\n[ro.build.version.release]: [11]\n[ro.vide]: []\nligne parasite\n");

            Assert.AreEqual("TC53", p["ro.product.model"]);
            Assert.AreEqual("11", p["ro.build.version.release"]);
            Assert.AreEqual("", p["ro.vide"]);
            Assert.AreEqual(3, p.Count);
        }

        [TestMethod]
        public void FileNameHelper_RemplaceLesCaracteresInterdits()
        {
            Assert.AreEqual("192.168.1.20_5555", FileNameHelper.Sanitize("192.168.1.20:5555"));
            Assert.AreEqual("Inconnu", FileNameHelper.Sanitize(null));
            Assert.AreEqual("a_b_c", FileNameHelper.Sanitize("a<b>c"));
        }
    }
}

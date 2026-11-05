using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class TeamProfileTests
    {
        private static string MakeZip(params (string name, string content)[] entries)
        {
            string path = Path.Combine(Path.GetTempPath(), "profil_test_" + Guid.NewGuid().ToString("N") + TeamProfile.Extension);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (name, content) in entries)
                {
                    var e = zip.CreateEntry(name);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
                }
            }
            return path;
        }

        [TestMethod]
        public void Read_LitLesReglagesEtLaBibliotheque()
        {
            string path = MakeZip(
                ("profil.json", "{\"Format\":1,\"Name\":\"Atelier\",\"ReportCompany\":\"Kanard Compagny\",\"UpdateShareFolder\":\"\\\\\\\\srv\\\\diffusion\"}"),
                ("bibliotheque.json", "[]"),
                ("modeles/logo.png", "png"));
            try
            {
                var p = TeamProfile.Read(path);
                Assert.AreEqual("Atelier", p.Info.Name);
                Assert.AreEqual("Kanard Compagny", p.Info.ReportCompany);
                Assert.IsNull(p.Info.ReportContact);
                Assert.AreEqual(@"\\srv\diffusion", p.Info.UpdateShareFolder);
                Assert.AreEqual("[]", p.LibraryJson);
                Assert.IsNotNull(p.Logo);
                Assert.AreEqual(".png", p.LogoExtension);
                Assert.IsNull(p.SavTemplate);
            }
            finally { File.Delete(path); }
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidDataException))]
        public void Read_SansProfilJson_Refuse()
        {
            string path = MakeZip(("autre.txt", "x"));
            try { TeamProfile.Read(path); }
            finally { File.Delete(path); }
        }

        [TestMethod]
        [ExpectedException(typeof(InvalidDataException))]
        public void Read_FormatFutur_Refuse()
        {
            string path = MakeZip(("profil.json", "{\"Format\":2,\"Name\":\"X\"}"));
            try { TeamProfile.Read(path); }
            finally { File.Delete(path); }
        }
    }
}

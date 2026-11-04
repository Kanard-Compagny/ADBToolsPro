using System;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class AppUpdaterTests
    {
        private const string ReleaseJson = @"{
  ""tag_name"": ""v1.2.3"",
  ""html_url"": ""https://github.com/Kanard-Compagny/ADBToolsPro/releases/tag/v1.2.3"",
  ""draft"": false,
  ""prerelease"": false,
  ""published_at"": ""2026-11-05T10:00:00Z"",
  ""body"": ""## Nouveautés\n- Mises à jour"",
  ""assets"": [
    { ""name"": ""ADBToolsPro-1.2.3.zip"", ""size"": 2600000, ""browser_download_url"": ""https://example.org/ADBToolsPro-1.2.3.zip"" },
    { ""name"": ""ADBToolsPro-1.2.3.zip.sha256"", ""size"": 88, ""browser_download_url"": ""https://example.org/ADBToolsPro-1.2.3.zip.sha256"" }
  ]
}";

        [TestMethod]
        public void ParseRelease_LitVersionArchiveEtEmpreinte()
        {
            var r = AppUpdater.ParseRelease(ReleaseJson);

            Assert.IsNotNull(r);
            Assert.AreEqual(new Version(1, 2, 3), r.Version);
            Assert.AreEqual("1.2.3", r.VersionText);
            Assert.AreEqual("ADBToolsPro-1.2.3.zip", r.ZipName);
            Assert.AreEqual("https://example.org/ADBToolsPro-1.2.3.zip", r.ZipUrl);
            Assert.AreEqual("https://example.org/ADBToolsPro-1.2.3.zip.sha256", r.Sha256Url);
            Assert.AreEqual(2600000L, r.ZipSize);
            Assert.IsTrue(AppUpdater.CanInstall(r));
        }

        [TestMethod]
        public void ParseRelease_IgnoreLesPreversions()
        {
            Assert.IsNull(AppUpdater.ParseRelease(ReleaseJson.Replace(@"""prerelease"": false", @"""prerelease"": true")));
        }

        [TestMethod]
        public void ParseRelease_SansEmpreinte_NonInstallable()
        {
            var r = AppUpdater.ParseRelease(ReleaseJson.Replace("ADBToolsPro-1.2.3.zip.sha256", "autre.txt"));
            Assert.IsNotNull(r);
            Assert.IsFalse(AppUpdater.CanInstall(r));
        }

        [TestMethod]
        public void TryParseVersion_AccepteLesFormatsCourants()
        {
            Assert.IsTrue(AppUpdater.TryParseVersion("v1.1.0", out var a));
            Assert.AreEqual(new Version(1, 1, 0), a);
            Assert.IsTrue(AppUpdater.TryParseVersion("1.1.1-dev.12", out var b));
            Assert.AreEqual(new Version(1, 1, 1), b);
            Assert.IsTrue(AppUpdater.TryParseVersion("2.0", out var c));
            Assert.AreEqual(new Version(2, 0, 0), c);
            Assert.IsFalse(AppUpdater.TryParseVersion("version", out _));
        }

        [TestMethod]
        public void IsUpdate_ComparaisonNumerique()
        {
            var v = AppUpdater.CurrentVersion;
            Assert.IsFalse(AppUpdater.IsUpdate(new AppRelease { Version = v }));
            Assert.IsTrue(AppUpdater.IsUpdate(new AppRelease { Version = new Version(v.Major, v.Minor, v.Build + 1) }));
            Assert.IsTrue(AppUpdater.IsUpdate(new AppRelease { Version = new Version(v.Major, v.Minor + 1, 0) }));
            Assert.IsFalse(AppUpdater.IsUpdate(null));
        }
    }
}

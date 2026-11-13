using System.Collections.Generic;
using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class DevicePathMemoryTests
    {
        [TestMethod]
        public void SuggestsSameFileThenSameExtension()
        {
            var m = new List<string>();
            DevicePathMemory.Remember(m, @"C:\conf\enterprisehomescreen.xml", "/enterprise/usr");
            DevicePathMemory.Remember(m, @"C:\conf\velocity.wldep", "/sdcard/Velocity/");
            Assert.AreEqual("/enterprise/usr/", DevicePathMemory.SuggestFor(m, "enterprisehomescreen.xml"));
            Assert.AreEqual("/sdcard/Velocity/", DevicePathMemory.SuggestFor(m, "autre.wldep"), "même extension");
            Assert.AreEqual("", DevicePathMemory.SuggestFor(m, "image.png"));
        }

        [TestMethod]
        public void RecentIsDistinctMostRecentFirstAndForgettable()
        {
            var m = new List<string>();
            DevicePathMemory.Remember(m, "a.txt", "/sdcard/A/");
            DevicePathMemory.Remember(m, "b.txt", "/sdcard/B/");
            DevicePathMemory.Remember(m, "c.txt", "/sdcard/A/");
            CollectionAssert.AreEqual(new[] { "/sdcard/A/", "/sdcard/B/" }, DevicePathMemory.Recent(m));
            DevicePathMemory.Forget(m, "/sdcard/A");
            CollectionAssert.AreEqual(new[] { "/sdcard/B/" }, DevicePathMemory.Recent(m));
        }

        [TestMethod]
        public void KeepsABoundedList()
        {
            var m = new List<string>();
            for (int i = 0; i < 60; i++) DevicePathMemory.Remember(m, i + ".txt", "/sdcard/" + i + "/");
            Assert.AreEqual(DevicePathMemory.MaxEntries, m.Count);
        }
    }
}

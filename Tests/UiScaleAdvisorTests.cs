using ADBToolsNew.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ADBToolsNew.Tests
{
    [TestClass]
    public class UiScaleAdvisorTests
    {
        [TestMethod]
        public void Recommend_DependsOnAvailableSpace()
        {
            // Portable 1366 × 768 à 100 % (barre des tâches déduite) : 90 %.
            Assert.AreEqual(0.9, UiScaleAdvisor.Recommend(1366, 728), 0.001);
            // 1920 × 1080 avec zoom Windows 125 % : 1536 × 824 points → 100 %.
            Assert.AreEqual(1.0, UiScaleAdvisor.Recommend(1536, 824), 0.001);
            // 1920 × 1080 à 100 % : de la place, mais pas assez pour agrandir avec marge → 100 %.
            Assert.AreEqual(1.0, UiScaleAdvisor.Recommend(1920, 1040), 0.001);
            // 2560 × 1440 à 100 % → 130 %.
            Assert.AreEqual(1.3, UiScaleAdvisor.Recommend(2560, 1400), 0.001);
            // Très petit écran : jamais en dessous de 80 %.
            Assert.AreEqual(0.8, UiScaleAdvisor.Recommend(1024, 600), 0.001);
            Assert.AreEqual(1.0, UiScaleAdvisor.Recommend(0, 0), 0.001);
        }

        [TestMethod]
        public void Fits_ComparesInterfaceAndScreen()
        {
            Assert.IsTrue(UiScaleAdvisor.Fits(0.9, 1366, 728));
            Assert.IsFalse(UiScaleAdvisor.Fits(1.0, 1366, 728));
            var f = UiScaleAdvisor.Footprint(1.0, 2800, 1600);
            Assert.AreEqual(0.5, f.W, 0.001);
            Assert.AreEqual(0.5, f.H, 0.001);
        }
    }
}

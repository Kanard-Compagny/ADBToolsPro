using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Test d'écran (pixels morts / burn-in) : pousse une image unie plein écran sur l'appareil et l'ouvre
    /// avec la visionneuse par défaut. Ne nécessite aucune app compagnon : uniquement des commandes adb standard.
    /// </summary>
    public class ScreenTestService
    {
        private const string RemoteFolder = "/sdcard/Pictures";

        private readonly AdbManager _adbManager;

        public ScreenTestService(AdbManager adbManager)
        {
            _adbManager = adbManager;
        }

        private static string RemotePathFor(string colorName) => $"{RemoteFolder}/adbtools_screentest_{colorName.ToLowerInvariant()}.png";

        /// <summary>Masque (ou restaure) les barres système (statut/navigation) pour un affichage réellement plein écran.</summary>
        private Task SetImmersiveModeAsync(string serial, bool hideSystemBars) =>
            _adbManager.ExecuteAsync($"-s {serial} shell settings put global policy_control {(hideSystemBars ? "immersive.full=*" : "null*")}");

        /// <summary>
        /// Génère localement une image unie à la résolution exacte de l'écran, la pousse sur l'appareil, puis
        /// l'ouvre en plein écran via la visionneuse. Retourne un message d'erreur si "am start" échoue
        /// (ex: aucune application capable d'afficher l'image sur ce build verrouillé), sinon null.
        /// </summary>
        public async Task<string> ShowColorAsync(string serial, string colorName, Color color, string localTempPngPath)
        {
            var (width, height) = await DeviceDisplayInfo.ResolveScreenSizeAsync(_adbManager, serial);
            SaveSolidColorPng(color, localTempPngPath, width, height);

            await SetImmersiveModeAsync(serial, hideSystemBars: true);

            string remotePath = RemotePathFor(colorName);
            await _adbManager.ExecuteAsync($"-s {serial} push {AdbManager.QuoteLocalArg(localTempPngPath)} {AdbManager.QuoteLocalArg(remotePath)}");
            var resStart = await _adbManager.ExecuteAsync($"-s {serial} shell am start -a android.intent.action.VIEW -d {AdbManager.QuoteRemoteArg("file://" + remotePath)} -t image/png");

            string combined = ((resStart.StandardOutput ?? "") + (resStart.StandardError ?? "")).Trim();
            bool looksLikeFailure = combined.IndexOf("Error", System.StringComparison.OrdinalIgnoreCase) >= 0
                || combined.IndexOf("No Activity found", System.StringComparison.OrdinalIgnoreCase) >= 0
                || combined.IndexOf("Exception", System.StringComparison.OrdinalIgnoreCase) >= 0;

            return looksLikeFailure ? combined : null;
        }

        /// <summary>
        /// Ramène l'appareil à l'écran d'accueil (ferme la visionneuse), supprime les images de test,
        /// et restaure l'affichage normal des barres système.
        /// </summary>
        public async Task CleanupAsync(string serial)
        {
            await _adbManager.ExecuteAsync($"-s {serial} shell input keyevent KEYCODE_HOME");
            await _adbManager.ExecuteAsync($"-s {serial} shell rm -f {RemoteFolder}/adbtools_screentest_*.png");
            await SetImmersiveModeAsync(serial, hideSystemBars: false);
        }

        /// <summary>Génère un PNG uni via l'imagerie WPF native (pas de dépendance System.Drawing).</summary>
        private static void SaveSolidColorPng(Color color, string outputPath, int width, int height)
        {
            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);

            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 255;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);

            using (var stream = new FileStream(outputPath, FileMode.Create))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
        }
    }
}

using System;
using System.IO;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Mini appli « ADB Tools Helper » (dossier HelperApp), installée par le Master pour ce qu'ADB seul ne peut plus faire :
    /// enregistrer un réseau Wi-Fi (Android 11+) et changer la langue du système. Elle reste sur le terminal, sans icône :
    /// Android retire les réseaux d'une appli quand on la désinstalle.
    /// </summary>
    public static class MasterHelperApp
    {
        public const string Package = "fr.kanard.adbtools.helper";
        /// <summary>versionCode de l'appli livrée (HelperApp/AndroidManifest.xml) : une version plus ancienne sur le terminal est remplacée.</summary>
        public const int VersionCode = 3;
        /// <summary>Version minimale par opération : Wi-Fi depuis la 1, langue depuis la 3 (déclaration de WRITE_SETTINGS).</summary>
        public const int WifiMinVersion = 1;
        public const int LocaleMinVersion = 3;
        public static string ApkPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HelperApp", "ADBToolsHelper.apk");

        /// <summary>Arguments de « adb shell » pour enregistrer un réseau Wi-Fi.</summary>
        public static string AddNetworkCommand(string ssid, string type, string password, bool hidden) =>
            $"am broadcast -f 32 -n {Package}/.AddNetwork --es ssid {MasterProfile.ShellQuote(ssid)} --es type {type} " +
            $"--es pass {MasterProfile.ShellQuote(password ?? "")} --ez hidden {(hidden ? "true" : "false")}";

        /// <summary>Arguments de « adb shell » pour changer la langue du système (ex. fr-FR).</summary>
        public static string SetLocaleCommand(string locale) =>
            $"am broadcast -f 32 -n {Package}/.SetLocale --es locale {MasterProfile.ShellQuote(locale)}";
    }
}

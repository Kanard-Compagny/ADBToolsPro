package fr.kanard.adbtools.diag;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Color;

import java.util.Locale;

/**
 * Langue et thème de l'appli : une seule langue à la fois (français ou anglais) et les couleurs d'ADB Tools en
 * clair ou en sombre. Lancée par le PC, l'appli prend la langue et le thème du programme (extras « lang » et
 * « theme ») ; lancée seule, ceux du terminal. Le dernier choix est gardé.
 */
final class L {
    private L() { }

    private static final String PREFS = "diag";
    static boolean en;
    static boolean dark = true;

    // Couleurs (Themes/Theme.Dark.xaml et Theme.Light.xaml du programme).
    static int bg, surface, surface2, line, ink, muted, brandText, brandSoft, action, onAction,
            title, titleInk, titleMuted, side, sideInk, sideMuted, sideActive,
            okBg, okFg, warnBg, warnFg, badBg, badFg, infoBg, infoFg, previewBg;

    /** Texte dans la langue en cours. */
    static String t(String fr, String enText) { return en ? enText : fr; }

    /** Lit le choix gardé (ou celui du terminal) et applique la palette. */
    static void load(Context c) {
        SharedPreferences p = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        String lang = p.getString("lang", "");
        if (lang.length() == 0) lang = "fr".equals(Locale.getDefault().getLanguage()) ? "fr" : "en";
        en = "en".equals(lang);
        String theme = p.getString("theme", "");
        if (theme.length() == 0) {
            int night = c.getResources().getConfiguration().uiMode & Configuration.UI_MODE_NIGHT_MASK;
            theme = night == Configuration.UI_MODE_NIGHT_NO ? "light" : "dark";
        }
        dark = !"light".equals(theme);
        palette();
    }

    /** Langue / thème envoyés par le PC. Vrai si quelque chose a changé (l'écran doit être reconstruit). */
    static boolean apply(Context c, String lang, String theme) {
        boolean changed = false;
        SharedPreferences.Editor e = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit();
        if ("fr".equals(lang) || "en".equals(lang)) {
            boolean v = "en".equals(lang);
            if (v != en) { en = v; changed = true; }
            e.putString("lang", lang);
        }
        if ("dark".equals(theme) || "light".equals(theme)) {
            boolean v = "dark".equals(theme);
            if (v != dark) { dark = v; changed = true; }
            e.putString("theme", theme);
        }
        e.apply();
        if (changed) palette();
        return changed;
    }

    private static void palette() {
        if (dark) {
            bg = rgb(0x101314); surface = rgb(0x181C1E); surface2 = rgb(0x1F2427); line = rgb(0x2A3033);
            ink = rgb(0xE9ECEE); muted = rgb(0x9AA3A9); brandText = rgb(0x5FD3A1); brandSoft = rgb(0x173A2D);
            action = rgb(0xFF9A2E); onAction = rgb(0x1B0F02);
            title = rgb(0x071E17); titleInk = rgb(0xDCEBE3); titleMuted = rgb(0x86A897);
            side = rgb(0x0B2A20); sideInk = rgb(0xDCEBE3); sideMuted = rgb(0x8FB0A2); sideActive = rgb(0x185440);
            okBg = rgb(0x123326); okFg = rgb(0x73D9A9); warnBg = rgb(0x3A2E0C); warnFg = rgb(0xF5D57E);
            badBg = rgb(0x3A1714); badFg = rgb(0xFF8A80); infoBg = rgb(0x15263B); infoFg = rgb(0x9CC3F0);
        } else {
            bg = rgb(0xF4F5F6); surface = rgb(0xFFFFFF); surface2 = rgb(0xEDEFF1); line = rgb(0xE1E4E7);
            ink = rgb(0x15191C); muted = rgb(0x5D656C); brandText = rgb(0x145C43); brandSoft = rgb(0xE1EEE8);
            action = rgb(0xF07F13); onAction = rgb(0x1B0F02);
            title = rgb(0x0B3125); titleInk = rgb(0xE6F0EB); titleMuted = rgb(0x9DBFB1);
            side = rgb(0x0F3D2E); sideInk = rgb(0xE6F0EB); sideMuted = rgb(0xA9C3B7); sideActive = rgb(0x1D6248);
            okBg = rgb(0xE1F2E8); okFg = rgb(0x13633F); warnBg = rgb(0xFBF0C8); warnFg = rgb(0x6B5000);
            badBg = rgb(0xFCE5E3); badFg = rgb(0xA3261C); infoBg = rgb(0xE4ECF7); infoFg = rgb(0x244F86);
        }
        previewBg = rgb(0x0C0F10);
    }

    private static int rgb(int v) { return Color.rgb((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF); }

    static { palette(); }
}

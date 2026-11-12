package fr.kanard.adbtools.diag;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.view.KeyEvent;

import org.json.JSONArray;

import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.Locale;
import java.util.regex.Pattern;

/**
 * Test du lecteur RFID (UHF), proposé seulement si le terminal en a un.
 *  - Zebra : DataWedge (entrée RFID, MC3300R, RFD40/RFD90, TC53E, EM45, ET6x) envoie les tags à l'appli par intent.
 *  - Autres marques : lecteur réglé en mode clavier (les tags arrivent comme une saisie terminée par Entrée).
 * Réussi dès qu'un tag est lu ; le test s'arrête 10 s après le dernier nouveau tag, ou au bout de 60 s.
 */
final class RfidTest implements DiagTest {
    static final String ACTION_RFID = "fr.kanard.adbtools.diag.RFID";
    private static final String DW_PROFILE = "ADBToolsDiag";
    private static final long MAX_MS = 60000, AFTER_LAST_MS = 10000;

    // Terminaux Zebra avec lecteur RFID intégré ou accessoire, d'après la documentation DataWedge.
    private static final Pattern ZEBRA_RFID_MODEL = Pattern.compile("^(MC33\\d*R|MC339\\d*R|MC3300R|TC53E.*RFID|EM45.*|ET6\\d.*)$", Pattern.CASE_INSENSITIVE);

    /** Mode de lecture possible sur ce terminal : « datawedge », « keyboard », ou vide (pas de RFID). */
    static String detect(Context c) {
        PackageManager pm = c.getPackageManager();
        boolean zebra = Build.MANUFACTURER != null && Build.MANUFACTURER.toLowerCase(Locale.ROOT).contains("zebra");
        boolean rfidApp = false;
        try {
            for (ApplicationInfo ai : pm.getInstalledApplications(0)) {
                String p = ai.packageName.toLowerCase(Locale.ROOT);
                if (p.equals(c.getPackageName())) continue;
                if (p.contains("rfid") || p.contains(".uhf")) { rfidApp = true; break; }
            }
        } catch (Exception ignored) { }
        if (zebra && installed(pm, "com.symbol.datawedge") && (rfidApp || ZEBRA_RFID_MODEL.matcher(Build.MODEL == null ? "" : Build.MODEL.trim()).matches()))
            return "datawedge";
        if (!zebra && rfidApp) return "keyboard";
        return "";
    }

    private static boolean installed(PackageManager pm, String pkg) {
        try { pm.getPackageInfo(pkg, 0); return true; } catch (Exception e) { return false; }
    }

    private final MainActivity a;
    private final String mode;
    private final LinkedHashSet<String> tags = new LinkedHashSet<String>();
    private final StringBuilder typed = new StringBuilder();
    private boolean registered, finished;
    private long startedAt, lastNewAt;
    private String last = "";

    RfidTest(MainActivity a, String mode) { this.a = a; this.mode = mode; }

    @Override public String id() { return "rfid"; }

    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            String data = intent.getStringExtra("com.symbol.datawedge.data_string");
            if (data != null) for (String t : data.split("[\\r\\n]+")) add(t);
        }
    };

    @Override
    public void start() {
        startedAt = System.currentTimeMillis();
        if ("datawedge".equals(mode)) {
            IntentFilter f = new IntentFilter(ACTION_RFID);
            f.addCategory(Intent.CATEGORY_DEFAULT);
            a.registerReceiver(receiver, f);
            registered = true;
            configure(true);
            a.setMessage(L.t("Appuyez sur la gâchette en visant des étiquettes RFID (UHF) : chaque tag lu s'affiche.",
                    "Press the trigger while aiming at RFID (UHF) tags: each tag read is shown."));
        } else {
            a.setMessage(L.t("Lisez des étiquettes RFID avec le lecteur (réglé en mode clavier) : chaque tag lu s'affiche.",
                    "Read RFID tags with the reader (set to keyboard mode): each tag read is shown."));
        }
        live();
        tick();
    }

    private void tick() {
        a.runLater(new Runnable() {
            @Override public void run() {
                if (finished) return;
                long now = System.currentTimeMillis();
                if (now - startedAt >= MAX_MS || (!tags.isEmpty() && now - lastNewAt >= AFTER_LAST_MS)) { finish(); return; }
                tick();
            }
        }, 1000);
    }

    /** Lecteur en mode clavier : caractères tapés jusqu'à Entrée. Vrai si la touche est prise par le test. */
    boolean onKey(KeyEvent e) {
        if ("datawedge".equals(mode) || finished) return false;
        if (e.getAction() != KeyEvent.ACTION_DOWN) return e.getKeyCode() == KeyEvent.KEYCODE_ENTER;
        if (e.getKeyCode() == KeyEvent.KEYCODE_ENTER || e.getKeyCode() == KeyEvent.KEYCODE_NUMPAD_ENTER) {
            add(typed.toString());
            typed.setLength(0);
            return true;
        }
        int ch = e.getUnicodeChar();
        if (ch > 31) { typed.append((char) ch); return true; }
        return false;
    }

    private void add(String raw) {
        final String t = raw == null ? "" : raw.trim();
        if (t.length() == 0) return;
        a.runLater(new Runnable() {
            @Override public void run() {
                if (finished) return;
                last = t;
                if (tags.add(t)) lastNewAt = System.currentTimeMillis();
                live();
            }
        }, 0);
    }

    private void live() {
        Out.live(id(), Out.obj("tags", tags.size(), "last", last, "seconds", (System.currentTimeMillis() - startedAt) / 1000));
    }

    private void finish() {
        if (finished) return;
        finished = true;
        JSONArray list = new JSONArray();
        int n = 0;
        for (String t : tags) { if (n++ >= 50) break; list.put(t); }
        if (tags.isEmpty()) Out.result(id(), "warn", Out.obj("reason", "timeout", "mode", mode));
        else Out.result(id(), "ok", Out.obj("tags", tags.size(), "list", list, "mode", mode));
        release();
        a.testFinished(this);
    }

    @Override
    public void stop() {
        finished = true;
        release();
    }

    private void release() {
        if (registered) {
            try { a.unregisterReceiver(receiver); } catch (Exception ignored) { }
            registered = false;
            configure(false);
        }
    }

    /** Profil DataWedge de l'appli : entrée RFID (et sortie intent dédiée) pendant le test, lecteur code-barres ensuite. */
    private void configure(boolean rfid) {
        try {
            Bundle profile = new Bundle();
            profile.putString("PROFILE_NAME", DW_PROFILE);
            profile.putString("PROFILE_ENABLED", "true");
            profile.putString("CONFIG_MODE", "CREATE_IF_NOT_EXIST");
            Bundle app = new Bundle();
            app.putString("PACKAGE_NAME", a.getPackageName());
            app.putStringArray("ACTIVITY_LIST", new String[]{"*"});
            profile.putParcelableArray("APP_LIST", new Bundle[]{app});
            ArrayList<Bundle> plugins = new ArrayList<Bundle>();
            Bundle rf = new Bundle();
            rf.putString("rfid_input_enabled", rfid ? "true" : "false");
            if (rfid) {
                rf.putString("rfid_hardware_trigger_enabled", "true");
                rf.putString("rfid_beeper_enable", "true");
                rf.putString("rfid_filter_duplicate_tags", "true");
                rf.putString("rfid_tag_read_duration", "0");
            }
            plugins.add(plugin("RFID", rf));
            Bundle bc = new Bundle();
            bc.putString("scanner_input_enabled", rfid ? "false" : "true");
            plugins.add(plugin("BARCODE", bc));
            Bundle out = new Bundle();
            out.putString("intent_output_enabled", "true");
            out.putString("intent_action", rfid ? ACTION_RFID : "fr.kanard.adbtools.diag.DATAWEDGE");
            out.putString("intent_category", Intent.CATEGORY_DEFAULT);
            out.putString("intent_delivery", "2");
            plugins.add(plugin("INTENT", out));
            profile.putParcelableArrayList("PLUGIN_CONFIG", plugins);
            Intent i = new Intent("com.symbol.datawedge.api.ACTION");
            i.putExtra("com.symbol.datawedge.api.SET_CONFIG", profile);
            a.sendBroadcast(i);
            if (rfid) a.runLater(new Runnable() {
                @Override public void run() {
                    Intent sw = new Intent("com.symbol.datawedge.api.ACTION");
                    sw.putExtra("com.symbol.datawedge.api.SWITCH_TO_PROFILE", DW_PROFILE);
                    a.sendBroadcast(sw);
                }
            }, 400);
        } catch (Exception ignored) { }
    }

    private static Bundle plugin(String name, Bundle params) {
        Bundle b = new Bundle();
        b.putString("PLUGIN_NAME", name);
        b.putString("RESET_CONFIG", "false");
        b.putBundle("PARAM_LIST", params);
        return b;
    }
}

package fr.kanard.adbtools.diag;

import android.util.Base64;
import android.util.Log;

import org.json.JSONObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.nio.charset.StandardCharsets;

/**
 * Messages vers ADB Tools, par le journal Android (tag ADBToolsDiag), une ligne par message :
 *   FEATURES \t json            matériel présent sur le terminal
 *   STATE    \t test \t état     started / stopped
 *   LIVE     \t test \t json     valeurs en direct (niveau du micro, capteurs, satellites…)
 *   RESULT   \t test \t statut \t json   ok / warn / bad / confirm (à confirmer par le technicien) / na
 *   SCAN     \t ms \t source \t symbologie \t données      test du lecteur
 * Les contenus sont en Base64 (UTF-8) : aucun caractère ne peut casser le découpage.
 */
final class Out {
    static final String TAG = "ADBToolsDiag";

    private Out() { }

    /** L'écran de l'appli suit les résultats (statut de chaque test). Appelé depuis n'importe quel fil. */
    interface Sink {
        void onResult(String test, String status, JSONObject json);
        void onVerdict(String test, String status);
        void onLive(String test, JSONObject json);
    }

    static volatile Sink sink;

    // ------------------------------------------------------------------ Résultats gardés sur le terminal
    // files/results/tests.json : pour chaque test, la dernière ligne RESULT et la dernière ligne VERDICT (même format
    // que le journal) et l'heure. Relu au démarrage de l'appli et par ADB Tools (« Récupérer du terminal »).

    private static File resultsFile;
    private static JSONObject saved = new JSONObject();

    /** Résultats propres aux tests (pas la mesure Wi-Fi, le suivi terrain ni les commandes). */
    private static boolean kept(String test) {
        return !(test.startsWith("survey") || test.startsWith("record") || "scan".equals(test) || "probe".equals(test));
    }

    static synchronized void init(File dir) {
        if (dir == null) return;
        //noinspection ResultOfMethodCallIgnored
        dir.mkdirs();
        resultsFile = new File(dir, "tests.json");
        saved = load();
    }

    static synchronized JSONObject saved() { return saved; }

    private static JSONObject load() {
        if (resultsFile == null || !resultsFile.exists()) return new JSONObject();
        try {
            byte[] b = new byte[(int) resultsFile.length()];
            FileInputStream in = new FileInputStream(resultsFile);
            int n = 0;
            while (n < b.length) { int r = in.read(b, n, b.length - n); if (r < 0) break; n += r; }
            in.close();
            JSONObject o = new JSONObject(new String(b, 0, n, StandardCharsets.UTF_8));
            JSONObject t = o.optJSONObject("tests");
            return t == null ? new JSONObject() : t;
        } catch (Exception e) { return new JSONObject(); }
    }

    private static synchronized void remember(String test, String key, String line) {
        if (resultsFile == null || !kept(test)) return;
        JSONObject e = saved.optJSONObject(test);
        if (e == null) e = new JSONObject();
        put(e, key, line);
        put(e, "at", System.currentTimeMillis());
        put(saved, test, e);
        save();
    }

    static synchronized void clearResults() {
        saved = new JSONObject();
        save();
    }

    private static void save() {
        if (resultsFile == null) return;
        try {
            JSONObject o = obj("version", 1, "updated", System.currentTimeMillis(), "tests", saved);
            OutputStreamWriter w = new OutputStreamWriter(new FileOutputStream(resultsFile), StandardCharsets.UTF_8);
            w.write(o.toString());
            w.close();
        } catch (Exception e) { Log.w(TAG, "results: " + e.getMessage()); }
    }

    /** « Envoyer au PC » : ADB Tools, s'il écoute, ouvre la fenêtre de récupération. */
    static void export() {
        Log.i(TAG, "EXPORT\ttests\t" + saved().length());
    }

    /** « Envoyer au PC » d'une mesure Wi-Fi (survey) ou d'un suivi terrain (record) : ADB Tools la récupère. */
    static void export(String kind) {
        Log.i(TAG, "EXPORT\t" + kind);
    }

    static String b64(String s) {
        return Base64.encodeToString((s == null ? "" : s).getBytes(StandardCharsets.UTF_8), Base64.NO_WRAP);
    }

    static void features(JSONObject json) {
        Log.i(TAG, "FEATURES\t" + b64(json.toString()));
    }

    static void state(String test, String state) {
        Log.i(TAG, "STATE\t" + test + "\t" + state);
    }

    static void live(String test, JSONObject json) {
        Log.i(TAG, "LIVE\t" + test + "\t" + b64(json.toString()));
        Sink s = sink;
        if (s != null) s.onLive(test, json);
    }

    static void result(String test, String status, JSONObject json) {
        String line = "RESULT\t" + test + "\t" + status + "\t" + b64(json == null ? "{}" : json.toString());
        Log.i(TAG, line);
        remember(test, "result", line);
        Sink s = sink;
        if (s != null) s.onResult(test, status, json);
    }

    static void scan(String source, String symbology, String data) {
        Log.i(TAG, "SCAN\t" + System.currentTimeMillis() + "\t" + source + "\t"
                + (symbology == null ? "" : symbology.replace('\t', ' ')) + "\t" + b64(data));
    }

    /** Résultat choisi par le technicien sur le terminal : ok / warn / bad / skip (test passé). */
    static void verdict(String test, String status) {
        String line = "VERDICT\t" + test + "\t" + status;
        Log.i(TAG, line);
        remember(test, "verdict", line);
        Sink s = sink;
        if (s != null) s.onVerdict(test, status);
    }

    static void info(String text) {
        Log.i(TAG, "INFO\t" + b64(text));
    }

    /** JSONObject.put sans exception vérifiée. */
    static JSONObject put(JSONObject o, String key, Object value) {
        try { o.put(key, value); } catch (Exception ignored) { }
        return o;
    }

    static JSONObject obj(Object... kv) {
        JSONObject o = new JSONObject();
        for (int i = 0; i + 1 < kv.length; i += 2) put(o, String.valueOf(kv[i]), kv[i + 1]);
        return o;
    }
}

package fr.kanard.adbtools.diag;

import android.util.Base64;
import android.util.Log;

import org.json.JSONObject;

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
    }

    static void result(String test, String status, JSONObject json) {
        Log.i(TAG, "RESULT\t" + test + "\t" + status + "\t" + b64(json == null ? "{}" : json.toString()));
    }

    static void scan(String source, String symbology, String data) {
        Log.i(TAG, "SCAN\t" + System.currentTimeMillis() + "\t" + source + "\t"
                + (symbology == null ? "" : symbology.replace('\t', ' ')) + "\t" + b64(data));
    }

    /** Résultat choisi par le technicien sur le terminal : ok / warn / bad / skip (test passé). */
    static void verdict(String test, String status) {
        Log.i(TAG, "VERDICT\t" + test + "\t" + status);
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

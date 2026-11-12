package fr.kanard.adbtools.diag;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.graphics.PorterDuff;
import android.graphics.Typeface;
import android.graphics.drawable.ColorDrawable;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.os.StatFs;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.TextureView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;

/**
 * ADB Tools Diag : une seule appli, pilotée par le PC ou utilisée seule.
 * Le PC envoie ses commandes par
 *   adb shell am start -n fr.kanard.adbtools.diag/.MainActivity -e cmd &lt;commande&gt; [-e lang fr|en] [-e theme dark|light]
 * (probe, scan, keys, survey, record, camera_back, camera_front, flash, speaker, earpiece, microphone, vibrator,
 * sensors, nfc, gps, bluetooth, storage_speed, stop…) et lit les résultats dans le journal Android (voir {@link Out}).
 * Sur le terminal : accueil, menu burger (Diagnostic, Lecteur, Wi-Fi, Touches, Suivi terrain, Résultats). Les tâches
 * de fond (mesure Wi-Fi, suivi terrain) restent visibles dans un bandeau en bas de chaque écran.
 */
public class MainActivity extends Activity implements Out.Sink {

    private static final String ACTION_DATAWEDGE = "fr.kanard.adbtools.diag.DATAWEDGE";
    private static final String ACTION_HONEYWELL = "fr.kanard.adbtools.diag.HONEYWELL";
    private static final String DW_PROFILE = "ADBToolsDiag";
    private static final String HONEYWELL_SERVICE = "com.intermec.datacollectionservice";

    /** Vert de la charte (notifications des services). */
    static final int C_BRAND = Color.rgb(0x1F, 0x74, 0x54);

    /** Tests du diagnostic, dans l'ordre de l'enchaînement « Tout tester ». */
    private static final String[] ALL_TESTS = { "screen", "touch", "camera_back", "camera_front", "flash", "speaker", "earpiece",
            "microphone", "vibrator", "sensors", "nfc", "gps", "bluetooth", "keys", "scanner", "rfid", "storage_speed" };

    /** Test qui occupe tout l'écran (couleurs, grille tactile) : la touche Retour l'arrête. */
    interface OverlayTest { void onBack(); }

    private static final String S_HOME = "home", S_DIAG = "diag", S_TEST = "test", S_SCAN = "scan", S_WIFI = "wifi",
            S_KEYS = "keys", S_FIELD = "field", S_RESULTS = "results", S_DONE = "done";

    private final Handler handler = new Handler(Looper.getMainLooper());
    private boolean resumed;
    private String pendingCmd;
    private Bundle pendingExtras;

    // Lien avec le PC : une commande du PC a été reçue (sinon : utilisation autonome).
    private boolean pcLinked;

    // Charpente de l'écran
    private FrameLayout rootFrame, contentHost, drawer;
    private LinearLayout drawerItems;
    private ImageView navButton;
    private TextView titleView, subtitleView, pillView;
    private LinearLayout bgBar;
    private TextView bgText;
    private String bgTarget;
    private String screen = S_HOME;
    private final Map<String, View> screens = new LinkedHashMap<String, View>();

    // Résultats de la session : test → statut (ok, warn, bad, confirm, na, skip).
    private final Map<String, String> status = new LinkedHashMap<String, String>();
    private final Map<String, Long> statusAt = new LinkedHashMap<String, Long>();

    // Test en cours
    private DiagTest current;
    private String activeTestId;
    private boolean pcDriven;      // test lancé par le PC
    private boolean runAll;        // enchaînement « Tout tester » lancé sur le terminal
    private String stepText = "";
    private TextureView preview;
    private FrameLayout previewBox;
    private TextView testMessage, testStatus, testAuto, testBanner, testDetail;
    private LinearLayout testLive;
    private Button testRestart;

    // Accueil
    private TextView homeModel, homeAndroid, homeBattery, homeWifi, homeStorage, homeLast;
    private TextView pillHome;

    // Diagnostic
    private TextView diagProgressText, diagSummary;
    private View diagProgressFill;
    private LinearLayout diagList;
    private Button diagContinue;

    // Résultats
    private LinearLayout resultsList;

    // Lecteur
    private boolean scanMode;
    private int scanCount;
    private final HashSet<String> scanDistinct = new HashSet<String>();
    private final List<String[]> scanRecent = new ArrayList<String[]>();
    private String lastIntentData;
    private long lastIntentAt;
    private boolean receiverRegistered;
    private EditText input;
    private TextView scanLastLabel, scanLastData, scanLastInfo, scanCountView, scanDistinctView, scanSourceView;
    private LinearLayout scanRecentList;

    // Mesure Wi-Fi
    private TextView wifiBig, wifiQuality, wifiDetail, wifiEvents, wifiIdle;
    private LinearLayout wifiRunning, wifiStopped;
    private long surveyStartRequestedAt;

    // Suivi terrain
    private TextView fieldBig, fieldDetail, fieldEvents, fieldIdle;
    private LinearLayout fieldRunning, fieldStopped;
    private long recorderStartRequestedAt;

    // Touches
    private boolean keysMode;
    private final Map<String, Integer> keys = new LinkedHashMap<String, Integer>();
    private TextView keysBig, keysCount, keysList;
    private LinearLayout keysVerdict;

    private int tick;
    private final Runnable ticker = new Runnable() {
        @Override public void run() {
            tick++;
            refreshBackground();
            if (S_WIFI.equals(screen)) refreshSurvey();
            if (S_FIELD.equals(screen)) refreshRecorder();
            if (S_HOME.equals(screen) && tick % 5 == 0) refreshHome();
            if (resumed) handler.postDelayed(this, 1000);
        }
    };

    private final Runnable flushKeyboard = new Runnable() { @Override public void run() { flushKeyboardInput(); } };

    private final BroadcastReceiver scanReceiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String action = intent.getAction();
            if (ACTION_DATAWEDGE.equals(action)) {
                onIntentScan(intent.getStringExtra("com.symbol.datawedge.data_string"), "datawedge",
                        intent.getStringExtra("com.symbol.datawedge.label_type"));
            } else if (ACTION_HONEYWELL.equals(action)) {
                String sym = intent.getStringExtra("codeId");
                String aim = intent.getStringExtra("aimId");
                if ((sym == null || sym.length() == 0) && aim != null) sym = aim;
                else if (aim != null && aim.length() > 0) sym = sym + " / " + aim;
                onIntentScan(intent.getStringExtra("data"), "honeywell", sym);
            }
        }
    };

    // ------------------------------------------------------------------ Cycle de vie

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        L.load(this);
        Out.init(new java.io.File(getExternalFilesDir(null) != null ? getExternalFilesDir(null) : getFilesDir(), "results"));
        detectTests();
        restoreResults();
        Intent launch = getIntent();
        if (launch != null) L.apply(this, launch.getStringExtra("lang"), launch.getStringExtra("theme"));
        buildUi();
        Out.sink = this;
        Log.i(Out.TAG, "READY\t" + Build.MANUFACTURER + "\t" + Build.MODEL + "\t" + Build.VERSION.RELEASE);
        pendingCmd = launch == null ? null : launch.getStringExtra("cmd");
        pendingExtras = launch == null ? null : launch.getExtras();
        if (pendingCmd != null) pcLinked = true;
    }

    @Override
    protected void onDestroy() {
        if (Out.sink == this) Out.sink = null;
        super.onDestroy();
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        if (intent == null) return;
        if (L.apply(this, intent.getStringExtra("lang"), intent.getStringExtra("theme"))) {
            // Langue ou thème du PC changés : l'écran est reconstruit (les tâches de fond continuent).
            stopCurrent();
            setScanMode(false);
            buildUi();
        }
        String cmd = intent.getStringExtra("cmd");
        if (cmd == null) return;
        pcLinked = true;
        updatePill();
        pendingExtras = intent.getExtras();
        if (resumed) handle(cmd); else pendingCmd = cmd;
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        if (scanMode) enableScanOutputs();
        if (pendingCmd != null) { String c = pendingCmd; pendingCmd = null; handle(c); }
        else if (S_HOME.equals(screen) && RecorderService.running != null) show(S_FIELD);
        else if (S_HOME.equals(screen) && SurveyService.running != null) show(S_WIFI);
        else show(screen);
        handler.removeCallbacks(ticker);
        handler.post(ticker);
    }

    @Override
    protected void onPause() {
        resumed = false;
        handler.removeCallbacks(ticker);
        // Matériel libéré dès que l'appli passe en arrière-plan (caméra, micro, GPS…).
        if (current != null) {
            stopCurrent();
            if (S_TEST.equals(screen)) setTestStatus(L.t("Test interrompu (appli mise en arrière-plan) : relancez-le ou donnez le résultat.",
                    "Test interrupted (app sent to the background): run it again or give the result."), L.warnFg);
        }
        if (scanMode) { flushKeyboardInput(); disableScanOutputs(); }
        super.onPause();
    }

    @Override
    public void onBackPressed() {
        if (overlay != null && overlay.getVisibility() == View.VISIBLE) {
            if (current instanceof OverlayTest) ((OverlayTest) current).onBack(); else hideOverlay();
            return;
        }
        if (drawer != null && drawer.getVisibility() == View.VISIBLE) { closeDrawer(); return; }
        if (S_TEST.equals(screen)) { leaveTest(); show(S_DIAG); return; }
        if (!S_HOME.equals(screen)) { show(S_HOME); return; }
        super.onBackPressed();
    }

    // ------------------------------------------------------------------ Commandes du PC

    private void handle(String cmd) {
        stepText = pendingExtras == null ? "" : nz(pendingExtras.getString("step", ""));
        switch (cmd) {
            case "survey":
                leaveTest();
                startSurvey(pendingExtras);
                return;
            case "survey_stop":
                if (SurveyService.running != null) SurveyService.running.stopSurvey("pc");
                else Out.result("survey_stop", "na", Out.obj("reason", "not_running"));
                return;
            case "record":
                leaveTest();
                startRecorder(pendingExtras);
                return;
            case "record_stop":
                if (RecorderService.running != null) RecorderService.running.stopRecording("pc");
                else Out.result("record_stop", "na", Out.obj("reason", "not_running"));
                return;
            case "probe":
                Out.features(Probe.run(this));
                return;
            case "stop":
            case "idle":
                leaveTest();
                if (S_TEST.equals(screen) || S_SCAN.equals(screen) || S_KEYS.equals(screen)) show(S_DIAG);
                return;
            case "scan":
                runAll = false;
                startTest("scanner", true);
                return;
            case "run_all":
                // « Tester sur le terminal » du PC : l'appli enchaîne ses tests ; le PC suit les résultats dans sa fiche.
                stopCurrent();
                startAll("1".equals(pendingExtras == null ? null : pendingExtras.getString("reset")));
                return;
        }
        if (!known(cmd)) { Out.result(cmd, "na", Out.obj("reason", "unknown")); return; }
        runAll = false;
        startTest(cmd, true);
    }

    /** Résultats gardés sur le terminal : relus au démarrage (le verdict du technicien prime sur la mesure). */
    private void restoreResults() {
        JSONObject all = Out.saved();
        java.util.Iterator<String> it = all.keys();
        while (it.hasNext()) {
            String id = it.next();
            JSONObject e = all.optJSONObject(id);
            if (e == null || indexOf(id) < 0) continue;
            String st = field(e.optString("verdict", ""), 2);
            if (st.length() == 0) st = field(e.optString("result", ""), 2);
            if (st.length() == 0) continue;
            status.put(id, st);
            statusAt.put(id, e.optLong("at", System.currentTimeMillis()));
        }
    }

    private static String field(String line, int i) {
        String[] p = line.split("\t");
        return i < p.length ? p[i].trim() : "";
    }

    // ------------------------------------------------------------------ Tests

    private int indexOf(String id) {
        for (int i = 0; i < tests.length; i++) if (tests[i].equals(id)) return i;
        return -1;
    }

    private static boolean known(String id) {
        for (String t : ALL_TESTS) if (t.equals(id)) return true;
        return false;
    }

    /** Tests proposés : seulement ceux que le matériel du terminal permet (RFID, NFC, GPS…). */
    private String[] tests = ALL_TESTS;
    private String rfidMode = "";

    private void detectTests() {
        JSONObject f;
        try { f = Probe.run(this); } catch (Exception e) { return; }
        rfidMode = f.optString("rfid_mode", "");
        List<String> list = new ArrayList<String>();
        for (String id : ALL_TESTS) {
            boolean ok;
            switch (id) {
                case "keys": case "storage_speed": ok = true; break;
                case "screen": ok = true; break;
                case "touch": ok = getPackageManager().hasSystemFeature(android.content.pm.PackageManager.FEATURE_TOUCHSCREEN); break;
                case "scanner": ok = f.optString("scanner", "").length() > 0; break;
                case "sensors": { JSONArray a = f.optJSONArray("sensors"); ok = a != null && a.length() > 0; break; }
                default: ok = f.optBoolean(id, true);
            }
            if (ok) list.add(id);
        }
        tests = list.toArray(new String[0]);
    }

    private DiagTest create(String cmd) {
        switch (cmd) {
            case "camera_back": return new CameraTest(this, false);
            case "camera_front": return new CameraTest(this, true);
            case "flash": return new TorchTest(this);
            case "speaker": return new AudioTests.Output(this, false);
            case "earpiece": return new AudioTests.Output(this, true);
            case "microphone": return new AudioTests.Microphone(this);
            case "vibrator": return new VibratorTest(this);
            case "sensors": return new SensorsTest(this);
            case "nfc": return new NfcTest(this);
            case "gps": return new GpsTest(this);
            case "bluetooth": return new BluetoothTest(this);
            case "storage_speed": return new StorageTest(this);
            case "screen": return new ScreenTest(this);
            case "touch": return new TouchTest(this);
            case "rfid": return rfidMode.length() > 0 ? new RfidTest(this, rfidMode) : null;
            default: return null;
        }
    }

    static String label(String cmd) {
        switch (cmd) {
            case "camera_back": return L.t("Caméra arrière", "Back camera");
            case "camera_front": return L.t("Caméra avant", "Front camera");
            case "flash": return L.t("Flash", "Flash");
            case "speaker": return L.t("Haut-parleur", "Speaker");
            case "earpiece": return L.t("Écouteur", "Earpiece");
            case "microphone": return L.t("Micro", "Microphone");
            case "vibrator": return L.t("Vibreur", "Vibrator");
            case "sensors": return L.t("Capteurs", "Sensors");
            case "nfc": return "NFC";
            case "gps": return "GPS";
            case "bluetooth": return "Bluetooth";
            case "keys": return L.t("Touches", "Keys");
            case "rfid": return "RFID";
            case "screen": return L.t("Écran", "Screen");
            case "touch": return L.t("Dalle tactile", "Touchscreen");
            case "scanner": return L.t("Lecteur code-barres", "Barcode scanner");
            case "storage_speed": return L.t("Mémoire", "Storage");
            default: return cmd;
        }
    }

    /** Lance un test : depuis le PC (fromPc) ou depuis l'appli. */
    private void startTest(String id, boolean fromPc) {
        stopCurrent();
        pcDriven = fromPc;
        activeTestId = id;
        if ("keys".equals(id)) { show(S_KEYS); return; }
        if ("scanner".equals(id)) { resetScanSession(); show(S_SCAN); return; }
        DiagTest t = create(id);
        if (t == null) { Out.result(id, "na", Out.obj("reason", "absent")); return; }
        show(S_TEST);
        testMessage.setText("");
        testAuto.setVisibility(View.GONE);
        testDetail.setText("");
        testLive.removeAllViews();
        testRestart.setVisibility(View.GONE);
        setTestStatus(L.t("En cours…", "Running…"), L.brandText);
        current = t;
        Out.state(t.id(), "started");
        try {
            t.start();
        } catch (Exception e) {
            Out.result(t.id(), "bad", Out.obj("reason", "exception", "message", String.valueOf(e.getMessage())));
            testFinished(t);
        }
    }

    private void stopCurrent() {
        if (keysMode) finishKeys();
        DiagTest t = current;
        current = null;
        if (t != null) {
            try { t.stop(); } catch (Exception ignored) { }
            Out.state(t.id(), "stopped");
        }
        showPreview(false);
    }

    /** Quitte le test affiché (le matériel est libéré ; pas de résultat). */
    private void leaveTest() {
        stopCurrent();
        activeTestId = null;
        runAll = false;
    }

    /** Appelé par un test terminé (sur le fil principal). */
    void testFinished(DiagTest t) {
        if (current != t) return;
        current = null;
        try { t.stop(); } catch (Exception ignored) { }
        Out.state(t.id(), "stopped");
        showPreview(false);
        setTestStatus(pcDriven ? L.t("Terminé : choisissez le résultat ci-dessous, ou sur le PC.", "Finished: choose the result below, or on the PC.")
                : L.t("Terminé : choisissez le résultat ci-dessous.", "Finished: choose the result below."), L.ink);
        testRestart.setVisibility(View.VISIBLE);
    }

    /** Le technicien tranche sur le terminal : le test s'arrête, le PC (s'il est relié) reçoit le résultat. */
    private void sendVerdict(String verdict) {
        String id = activeTestId;
        if (id == null) return;
        if ("scanner".equals(id) && !"skip".equals(verdict)) emitScannerResult();
        stopCurrent();
        Out.verdict(id, verdict);
        activeTestId = null;
        if (pcDriven) {
            Toast.makeText(this, "skip".equals(verdict) ? L.t("Test passé", "Test skipped") : L.t("Résultat envoyé au PC", "Result sent to the PC"), Toast.LENGTH_SHORT).show();
            show(S_DIAG);
            return;
        }
        if (runAll) {
            String next = nextUntested(id);
            if (next != null) { startTest(next, false); return; }
            runAll = false;
            show(S_DONE);
            return;
        }
        show(S_DIAG);
    }

    private String nextUntested(String after) {
        int start = after == null ? 0 : indexOf(after) + 1;
        for (int i = start; i < tests.length; i++) if (!status.containsKey(tests[i])) return tests[i];
        return null;
    }

    @Override
    public void onResult(final String test, final String st, JSONObject json) {
        runLater(new Runnable() {
            @Override public void run() {
                if (indexOf(test) < 0) return;
                // Résultat automatique : gardé jusqu'au verdict du technicien.
                if (!"confirm".equals(st) || !status.containsKey(test)) { status.put(test, st); statusAt.put(test, System.currentTimeMillis()); }
                if (test.equals(activeTestId) && testAuto != null) {
                    testAuto.setText(L.t("Mesure automatique : ", "Automatic check: ") + statusLabel(st));
                    styleStatus(testAuto, st);
                    testAuto.setVisibility(View.VISIBLE);
                    testDetail.setText(describeResult(test, st, json));
                    if (!"sensors".equals(test)) testLive.removeAllViews();
                }
                refreshLists();
            }
        }, 0);
    }

    @Override
    public void onLive(final String test, final JSONObject json) {
        runLater(new Runnable() {
            @Override public void run() { if (test.equals(activeTestId) && S_TEST.equals(screen) && testLive != null) renderLive(test, json); }
        }, 0);
    }

    /** Valeurs en direct pendant un test : le technicien voit ce qui est mesuré et ce qu'il reste à faire. */
    private void renderLive(String test, JSONObject j) {
        testLive.removeAllViews();
        switch (test) {
            case "microphone": {
                double level = j.optDouble("level_db", -90), peak = j.optDouble("peak_db", -90);
                testLive.addView(meter(L.t("Niveau", "Level"), Math.round(level) + " dB", (level + 60) / 60.0, level >= -25 ? L.okFg : level >= -45 ? L.warnFg : L.muted));
                testLive.addView(meter(L.t("Crête", "Peak"), Math.round(peak) + " dB", (peak + 60) / 60.0, peak >= -25 ? L.okFg : peak >= -45 ? L.warnFg : L.muted));
                TextView hint = text(peak >= -25 ? L.t("Le micro capte bien le son.", "The microphone picks up sound well.")
                        : L.t("Parlez plus fort ou tapez des mains : la crête doit dépasser −25 dB.", "Speak louder or clap: the peak must go above −25 dB."), 13, L.muted, false);
                hint.setPadding(0, dp(6), 0, 0);
                testLive.addView(hint);
                break;
            }
            case "sensors": {
                java.util.Iterator<String> it = j.keys();
                while (it.hasNext()) {
                    String k = it.next();
                    JSONObject o = j.optJSONObject(k);
                    if (o == null) continue;
                    boolean ok = o.optBoolean("reacted");
                    LinearLayout row = new LinearLayout(this);
                    row.setOrientation(LinearLayout.HORIZONTAL);
                    row.setGravity(Gravity.CENTER_VERTICAL);
                    row.setPadding(dp(12), dp(8), dp(12), dp(8));
                    row.setBackground(rounded(L.surface, 0, 8));
                    LinearLayout names = new LinearLayout(this);
                    names.setOrientation(LinearLayout.VERTICAL);
                    names.addView(text(sensorLabel(k), 14, L.ink, true));
                    names.addView(text(ok ? L.t("Réagit", "Reacting") + " · " + o.optDouble("value") : sensorHint(k), 12, L.muted, false));
                    row.addView(names, new LinearLayout.LayoutParams(0, -2, 1f));
                    TextView mark = text(ok ? "✔" : "…", 18, ok ? L.okFg : L.muted, true);
                    row.addView(mark);
                    testLive.addView(row, lpMatch(testLive.getChildCount() == 0 ? 0 : 6));
                }
                break;
            }
            case "gps":
                testLive.addView(text(L.t("Satellites vus : ", "Satellites in view: ") + j.optInt("satellites") + L.t("  ·  utilisés : ", "  ·  used: ") + j.optInt("used")
                        + "\n" + L.t("Meilleur signal : ", "Best signal: ") + j.optLong("best_cn0") + " dB-Hz  ·  " + j.optLong("seconds") + " s", 15, L.ink, false));
                break;
            case "bluetooth":
                testLive.addView(text(L.t("Appareils trouvés : ", "Devices found: ") + j.optInt("devices")
                        + (j.has("best_rssi") && !j.isNull("best_rssi") ? L.t("  ·  meilleur signal : ", "  ·  best signal: ") + j.opt("best_rssi") + " dBm" : ""), 15, L.ink, false));
                break;
            case "rfid":
                testLive.addView(text(L.t("Tags lus : ", "Tags read: ") + j.optInt("tags") + "  ·  " + j.optLong("seconds") + " s", 18, L.brandText, true));
                if (j.optString("last").length() > 0) {
                    TextView last = text(j.optString("last"), 13, L.ink, false);
                    last.setTypeface(Typeface.MONOSPACE);
                    last.setPadding(0, dp(6), 0, 0);
                    testLive.addView(last);
                }
                break;
            case "touch":
                testLive.addView(meter(L.t("Zones touchées", "Zones touched"), j.optInt("covered") + " / " + j.optInt("total"), j.optInt("covered") / (double) Math.max(1, j.optInt("total")), L.brandText));
                break;
            case "storage_speed":
                testLive.addView(meter(L.t("Écriture", "Writing"), j.optInt("mb") + " / " + j.optInt("total") + " Mo", j.optInt("mb") / (double) Math.max(1, j.optInt("total")), L.brandText));
                break;
        }
    }

    /** Jauge horizontale : libellé, valeur, barre remplie à fraction (0 à 1). */
    private View meter(String label, String value, double fraction, int color) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(0, dp(4), 0, dp(4));
        LinearLayout line = new LinearLayout(this);
        line.addView(text(label, 13, L.muted, false), new LinearLayout.LayoutParams(0, -2, 1f));
        line.addView(text(value, 13, L.ink, true));
        box.addView(line);
        FrameLayout bar = new FrameLayout(this);
        bar.setBackground(rounded(L.surface2, 0, 5));
        View fill = new View(this);
        fill.setBackground(rounded(color, 0, 5));
        int full = getResources().getDisplayMetrics().widthPixels - dp(32);
        bar.addView(fill, new FrameLayout.LayoutParams((int) Math.round(full * Math.max(0, Math.min(1, fraction))), dp(12)));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, dp(12));
        lp.topMargin = dp(4);
        box.addView(bar, lp);
        return box;
    }

    private static String sensorLabel(String k) {
        switch (k) {
            case "accelerometer": return L.t("Accéléromètre", "Accelerometer");
            case "gyroscope": return L.t("Gyroscope", "Gyroscope");
            case "compass": return L.t("Boussole", "Compass");
            case "light": return L.t("Capteur de lumière", "Light sensor");
            case "proximity": return L.t("Capteur de proximité", "Proximity sensor");
            case "pressure": return L.t("Baromètre", "Barometer");
            default: return k;
        }
    }

    private static String sensorHint(String k) {
        switch (k) {
            case "accelerometer": return L.t("Secouez ou retournez le terminal", "Shake or flip the device");
            case "gyroscope": return L.t("Faites pivoter le terminal", "Rotate the device");
            case "compass": return L.t("Tournez le terminal à plat sur lui-même", "Turn the device around while flat");
            case "light": return L.t("Couvrez puis découvrez le haut de l'écran", "Cover then uncover the top of the screen");
            case "proximity": return L.t("Passez la main devant le haut de l'écran", "Wave your hand over the top of the screen");
            case "pressure": return L.t("Lecture de la pression en cours", "Reading the pressure");
            default: return L.t("En attente", "Waiting");
        }
    }

    /** Résultat automatique en clair : valeurs mesurées ou raison. */
    private static String describeResult(String test, String st, JSONObject j) {
        if (j == null) j = new JSONObject();
        String reason = j.optString("reason", "");
        if (reason.length() > 0) {
            switch (reason) {
                case "absent": return L.t("Matériel absent sur ce terminal.", "No such hardware on this device.");
                case "disabled":
                    return "gps".equals(test) ? L.t("Localisation désactivée : activez-la dans les réglages, puis relancez le test.", "Location is off: turn it on in settings, then run the test again.")
                            : L.t("Désactivé dans les réglages : activez-le, puis relancez le test.", "Turned off in settings: turn it on, then run the test again.");
                case "permission": return L.t("Autorisation refusée : réinstallez l'appli depuis ADB Tools (bouton « Appli Diag »).", "Permission denied: reinstall the app from ADB Tools (\"Diag app\" button).");
                case "timeout":
                    if ("rfid".equals(test)) return L.t("Aucun tag RFID lu en 60 s : vérifiez qu'une étiquette UHF est à portée et que le lecteur est connecté.",
                            "No RFID tag read in 60 s: check that a UHF tag is in range and the reader is connected.");
                    return "gps".equals(test) ? L.t("Pas de position obtenue : " + j.optInt("satellites") + " satellite(s) vu(s).", "No position fix: " + j.optInt("satellites") + " satellite(s) in view.")
                            : L.t("Rien détecté dans le temps imparti.", "Nothing detected in time.");
                default: return L.t("Erreur : ", "Error: ") + reason + (j.has("message") ? " — " + j.optString("message") : "");
            }
        }
        switch (test) {
            case "storage_speed":
                return L.t("Écriture : " + j.optDouble("write_mbs") + " Mo/s  ·  lecture : " + j.optDouble("read_mbs") + " Mo/s (" + j.optInt("size_mb") + " Mo). La vitesse d'écriture décide du résultat.",
                        "Write: " + j.optDouble("write_mbs") + " MB/s  ·  read: " + j.optDouble("read_mbs") + " MB/s (" + j.optInt("size_mb") + " MB). The write speed decides the result.");
            case "microphone": return L.t("Crête mesurée : ", "Measured peak: ") + Math.round(j.optDouble("peak_db", -90)) + " dB";
            case "camera_back": case "camera_front":
                return L.t("Photo " + j.optInt("width") + "×" + j.optInt("height") + " · luminosité " + j.optLong("brightness") + " · contraste " + j.optLong("contrast"),
                        "Photo " + j.optInt("width") + "×" + j.optInt("height") + " · brightness " + j.optLong("brightness") + " · contrast " + j.optLong("contrast"));
            case "bluetooth": return L.t("Appareils trouvés : ", "Devices found: ") + j.optInt("devices");
            case "gps": return L.t("Position obtenue en " + j.optDouble("ttff_s") + " s · " + j.optInt("used") + " satellite(s) utilisé(s).", "Position fixed in " + j.optDouble("ttff_s") + " s · " + j.optInt("used") + " satellite(s) used.");
            case "nfc": return L.t("Badge lu : ", "Tag read: ") + j.optString("uid");
            case "rfid": return L.t("Tags RFID lus : ", "RFID tags read: ") + j.optInt("tags");
            case "scanner": return L.t(j.optInt("reads") + " lecture(s), " + j.optInt("distinct") + " code(s) différent(s).", j.optInt("reads") + " read(s), " + j.optInt("distinct") + " distinct code(s).");
            case "touch": return L.t(j.optInt("covered") + " zone(s) sur " + j.optInt("total") + " ont réagi (" + j.optInt("percent") + " %).", j.optInt("covered") + " of " + j.optInt("total") + " zones reacted (" + j.optInt("percent") + " %).");
            case "screen": return L.t("Avez-vous vu des pixels morts, taches ou traces ? Choisissez le résultat.", "Did you see dead pixels, spots or burn-in? Choose the result.");
            case "sensors": {
                StringBuilder sb = new StringBuilder();
                java.util.Iterator<String> it = j.keys();
                while (it.hasNext()) {
                    String k = it.next();
                    JSONObject o = j.optJSONObject(k);
                    if (o == null) continue;
                    if (sb.length() > 0) sb.append("\n");
                    sb.append(sensorLabel(k)).append(" : ").append(statusLabel(o.optString("status")));
                }
                return sb.toString();
            }
            case "speaker": case "earpiece": case "flash": case "vibrator":
                return L.t("À vous de confirmer ce que vous avez vu ou entendu.", "Confirm what you saw or heard.");
            default: return "";
        }
    }

    /** Aperçu caméra aux proportions de l'image (sinon elle était écrasée). */
    void setPreviewAspect(final int bufW, final int bufH, final int sensorOrientation) {
        runLater(new Runnable() {
            @Override public void run() {
                if (previewBox == null || bufW <= 0 || bufH <= 0) return;
                boolean rotated = sensorOrientation == 90 || sensorOrientation == 270;
                double ratio = rotated ? bufW / (double) bufH : bufH / (double) bufW;   // hauteur / largeur à l'écran
                int availW = getResources().getDisplayMetrics().widthPixels - dp(32);
                int maxH = (int) (getResources().getDisplayMetrics().heightPixels * 0.45);
                int w = availW, h = (int) Math.round(availW * ratio);
                if (h > maxH) { h = maxH; w = (int) Math.round(h / ratio); }
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(w, h);
                lp.gravity = Gravity.CENTER_HORIZONTAL;
                previewBox.setLayoutParams(lp);
            }
        }, 0);
    }

    @Override
    public void onVerdict(final String test, final String st) {
        runLater(new Runnable() {
            @Override public void run() {
                if (indexOf(test) < 0) return;
                status.put(test, st);
                statusAt.put(test, System.currentTimeMillis());
                refreshLists();
            }
        }, 0);
    }

    // ------------------------------------------------------------------ Aide aux tests (appelée par les tests)

    void setMessage(final String text) {
        runLater(new Runnable() { @Override public void run() { if (testMessage != null) testMessage.setText(text); } }, 0);
    }

    void runLater(Runnable r, long delayMs) {
        if (delayMs <= 0 && Looper.myLooper() == Looper.getMainLooper()) r.run();
        else handler.postDelayed(r, Math.max(0, delayMs));
    }

    TextureView preview() { return preview; }

    void showPreview(final boolean on) {
        runLater(new Runnable() {
            @Override public void run() { if (previewBox != null) previewBox.setVisibility(on ? View.VISIBLE : View.GONE); }
        }, 0);
    }

    // ------------------------------------------------------------------ Plein écran (tests Écran et Tactile)

    private FrameLayout overlay;

    @SuppressWarnings("deprecation")
    void showOverlay(final View v) {
        runLater(new Runnable() {
            @Override public void run() {
                overlay.removeAllViews();
                overlay.addView(v, new FrameLayout.LayoutParams(-1, -1));
                overlay.setVisibility(View.VISIBLE);
                getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                        | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
            }
        }, 0);
    }

    @SuppressWarnings("deprecation")
    void hideOverlay() {
        runLater(new Runnable() {
            @Override public void run() {
                if (overlay == null) return;
                overlay.removeAllViews();
                overlay.setVisibility(View.GONE);
                getWindow().getDecorView().setSystemUiVisibility(0);
            }
        }, 0);
    }

    // ------------------------------------------------------------------ Fin de la batterie de tests

    private TextView doneTitle, doneOk, doneWarn, doneBad, doneReview;

    private View buildDone() {
        LinearLayout p = page();
        LinearLayout c = card();
        doneTitle = text("", 15, L.ink, true);
        c.addView(doneTitle);
        LinearLayout row = new LinearLayout(this);
        row.setPadding(0, dp(12), 0, 0);
        doneOk = doneBox(row, L.okBg, L.okFg, "OK", false);
        doneWarn = doneBox(row, L.warnBg, L.warnFg, L.t("À surveiller", "To monitor"), true);
        doneBad = doneBox(row, L.badBg, L.badFg, "KO", true);
        c.addView(row);
        doneReview = text("", 13, L.muted, false);
        doneReview.setPadding(0, dp(12), 0, 0);
        c.addView(doneReview);
        p.addView(c);
        Button results = actionButton(L.t("Voir les résultats", "View the results"));
        results.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { show(S_RESULTS); } });
        p.addView(results, lpButton(results, 54, 20));
        TextView note = text(L.t("Dans Résultats : envoyer au PC, refaire un test ou changer un résultat.",
                "In Results: send to the PC, run a test again or change a result."), 12, L.muted, false);
        note.setGravity(Gravity.CENTER_HORIZONTAL);
        note.setPadding(0, dp(8), 0, 0);
        p.addView(note);
        Button home = secondaryButton(L.t("Retour à l'accueil", "Back to home"));
        home.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { show(S_HOME); } });
        p.addView(home, lpButton(home, 48, 16));
        return scroll(p);
    }

    private TextView doneBox(LinearLayout row, int bg, int fg, String label, boolean gap) {
        LinearLayout b = new LinearLayout(this);
        b.setOrientation(LinearLayout.VERTICAL);
        b.setGravity(Gravity.CENTER_HORIZONTAL);
        b.setPadding(dp(6), dp(10), dp(6), dp(10));
        b.setBackground(rounded(bg, 0, 8));
        TextView n = text("0", 22, fg, true);
        b.addView(n);
        b.addView(text(label, 12, fg, false));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1f);
        if (gap) lp.leftMargin = dp(8);
        row.addView(b, lp);
        return n;
    }

    private void refreshDone() {
        int[] c = counts();
        doneTitle.setText(L.t(c[3] + " tests faits sur " + tests.length, c[3] + " of " + tests.length + " tests done"));
        doneOk.setText(String.valueOf(c[0]));
        doneWarn.setText(String.valueOf(c[1]));
        doneBad.setText(String.valueOf(c[2]));
        StringBuilder sb = new StringBuilder();
        for (String id : tests) {
            String st = status.get(id);
            if ("warn".equals(st) || "bad".equals(st)) {
                if (sb.length() > 0) sb.append(", ");
                sb.append(label(id)).append(" (").append(statusLabel(st)).append(")");
            }
        }
        doneReview.setText(sb.length() == 0 ? L.t("Rien à revoir.", "Nothing to review.") : L.t("À revoir : ", "To review: ") + sb);
    }

    // ------------------------------------------------------------------ Construction de l'écran

    private void buildUi() {
        screens.clear();
        getWindow().getDecorView().setBackgroundColor(L.bg);
        if (Build.VERSION.SDK_INT >= 21) {
            getWindow().setStatusBarColor(L.title);
            getWindow().setNavigationBarColor(L.bg);
        }
        rootFrame = new FrameLayout(this);
        rootFrame.setBackgroundColor(L.bg);

        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        outer.addView(buildHeader(), new LinearLayout.LayoutParams(-1, dp(56)));
        contentHost = new FrameLayout(this);
        outer.addView(contentHost, new LinearLayout.LayoutParams(-1, 0, 1f));
        bgBar = buildBackgroundBar();
        outer.addView(bgBar, new LinearLayout.LayoutParams(-1, -2));
        rootFrame.addView(outer, new FrameLayout.LayoutParams(-1, -1));

        screens.put(S_HOME, buildHome());
        screens.put(S_DIAG, buildDiag());
        screens.put(S_TEST, buildTest());
        screens.put(S_SCAN, buildScan());
        screens.put(S_WIFI, buildWifi());
        screens.put(S_KEYS, buildKeys());
        screens.put(S_FIELD, buildField());
        screens.put(S_RESULTS, buildResults());
        screens.put(S_DONE, buildDone());
        for (View v : screens.values()) { v.setVisibility(View.GONE); contentHost.addView(v, new FrameLayout.LayoutParams(-1, -1)); }

        drawer = buildDrawer();
        rootFrame.addView(drawer, new FrameLayout.LayoutParams(-1, -1));
        overlay = new FrameLayout(this);
        overlay.setVisibility(View.GONE);
        rootFrame.addView(overlay, new FrameLayout.LayoutParams(-1, -1));
        setContentView(rootFrame);
        updatePill();
        show(screen);
    }

    private View buildHeader() {
        LinearLayout h = new LinearLayout(this);
        h.setOrientation(LinearLayout.HORIZONTAL);
        h.setGravity(Gravity.CENTER_VERTICAL);
        h.setBackgroundColor(L.title);
        h.setPadding(dp(4), 0, dp(10), 0);
        navButton = new ImageView(this);
        navButton.setScaleType(ImageView.ScaleType.CENTER);
        navButton.setFocusable(true);
        navButton.setBackground(selector(Color.TRANSPARENT, 0, L.sideActive, 8));
        navButton.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) {
                if (S_TEST.equals(screen)) { leaveTest(); show(S_DIAG); } else openDrawer();
            }
        });
        h.addView(navButton, new LinearLayout.LayoutParams(dp(48), dp(48)));
        LinearLayout names = new LinearLayout(this);
        names.setOrientation(LinearLayout.VERTICAL);
        names.setPadding(dp(6), 0, dp(6), 0);
        titleView = text("ADB Tools Diag", 18, L.titleInk, true);
        titleView.setSingleLine(true);
        titleView.setEllipsize(TextUtils.TruncateAt.END);
        subtitleView = text("", 12, L.titleMuted, false);
        subtitleView.setVisibility(View.GONE);
        names.addView(titleView);
        names.addView(subtitleView);
        h.addView(names, new LinearLayout.LayoutParams(0, -2, 1f));
        pillView = text("", 12, L.okFg, true);
        pillView.setPadding(dp(10), dp(5), dp(10), dp(5));
        h.addView(pillView);
        updatePill();
        return h;
    }

    private void updatePill() {
        if (pillView == null) return;
        if (pcLinked) {
            pillView.setText("● " + L.t("Relié au PC", "Linked to PC"));
            pillView.setTextColor(L.okFg);
            pillView.setBackground(rounded(L.okBg, 0, 14));
        } else {
            pillView.setText("○ " + L.t("Autonome", "Standalone"));
            pillView.setTextColor(L.titleInk);
            pillView.setBackground(rounded(L.sideActive, 0, 14));
        }
        if (pillHome != null) pillHome.setVisibility(pcLinked ? View.GONE : View.VISIBLE);
    }

    private LinearLayout buildBackgroundBar() {
        LinearLayout b = new LinearLayout(this);
        b.setOrientation(LinearLayout.HORIZONTAL);
        b.setGravity(Gravity.CENTER_VERTICAL);
        b.setBackgroundColor(L.brandSoft);
        b.setPadding(dp(16), dp(8), dp(8), dp(8));
        b.setVisibility(View.GONE);
        b.setFocusable(true);
        b.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { if (bgTarget != null) show(bgTarget); } });
        bgText = text("", 13, L.dark ? L.titleInk : L.ink, false);
        b.addView(bgText, new LinearLayout.LayoutParams(0, -2, 1f));
        TextView see = text(L.t("Voir", "View"), 14, L.brandText, true);
        see.setPadding(dp(12), dp(8), dp(8), dp(8));
        b.addView(see);
        return b;
    }

    private FrameLayout buildDrawer() {
        FrameLayout d = new FrameLayout(this);
        d.setVisibility(View.GONE);
        View scrim = new View(this);
        scrim.setBackgroundColor(Color.argb(140, 0, 0, 0));
        scrim.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { closeDrawer(); } });
        d.addView(scrim, new FrameLayout.LayoutParams(-1, -1));

        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setBackgroundColor(L.side);
        panel.setClickable(true);
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.VERTICAL);
        head.setPadding(dp(20), dp(22), dp(20), dp(16));
        head.addView(text("ADB Tools Diag", 20, L.sideInk, true));
        TextView dev = text(Build.MANUFACTURER + " " + Build.MODEL, 13, L.sideMuted, false);
        dev.setPadding(0, dp(4), 0, 0);
        head.addView(dev);
        panel.addView(head);
        View sep = new View(this);
        sep.setBackgroundColor(L.sideActive);
        panel.addView(sep, new LinearLayout.LayoutParams(-1, dp(1)));

        drawerItems = new LinearLayout(this);
        drawerItems.setOrientation(LinearLayout.VERTICAL);
        drawerItems.setPadding(dp(8), dp(8), dp(8), dp(8));
        ScrollView sv = new ScrollView(this);
        sv.addView(drawerItems);
        panel.addView(sv, new LinearLayout.LayoutParams(-1, 0, 1f));
        View sep2 = new View(this);
        sep2.setBackgroundColor(L.sideActive);
        panel.addView(sep2, new LinearLayout.LayoutParams(-1, dp(1)));
        LinearLayout foot = new LinearLayout(this);
        foot.setOrientation(LinearLayout.VERTICAL);
        foot.setPadding(dp(8), dp(4), dp(8), dp(10));
        foot.addView(drawerItem("about", "ic_info", L.t("À propos", "About"), false));
        panel.addView(foot);

        int w = Math.min(dp(300), (int) (getResources().getDisplayMetrics().widthPixels * 0.82f));
        d.addView(panel, new FrameLayout.LayoutParams(w, -1, Gravity.START));
        return d;
    }

    private void fillDrawer() {
        drawerItems.removeAllViews();
        drawerItems.addView(drawerItem(S_HOME, "ic_home", L.t("Accueil", "Home"), false));
        drawerItems.addView(drawerItem(S_DIAG, "ic_diag", L.t("Diagnostic", "Diagnostics"), false));
        drawerItems.addView(drawerItem(S_SCAN, "ic_scan", L.t("Lecteur", "Scanner"), false));
        drawerItems.addView(drawerItem(S_WIFI, "ic_wifi", "Wi-Fi", SurveyService.running != null));
        drawerItems.addView(drawerItem(S_KEYS, "ic_keys", L.t("Touches", "Keys"), false));
        drawerItems.addView(drawerItem(S_FIELD, "ic_field", L.t("Suivi terrain", "Field monitoring"), RecorderService.running != null));
        drawerItems.addView(drawerItem(S_RESULTS, "ic_results", L.t("Résultats", "Results"), false));
    }

    private View drawerItem(final String id, String icon, String label, boolean running) {
        boolean active = id.equals(screen) || (S_TEST.equals(screen) && S_DIAG.equals(id));
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(14), 0, dp(12), 0);
        row.setFocusable(true);
        row.setClickable(true);
        row.setBackground(selector(active ? L.sideActive : Color.TRANSPARENT, 0, L.sideActive, 10));
        row.addView(icon(icon, 22, active ? Color.WHITE : L.sideInk), new LinearLayout.LayoutParams(dp(24), dp(24)));
        TextView t = text(label, 15, active ? Color.WHITE : L.sideInk, active);
        t.setPadding(dp(14), 0, 0, 0);
        row.addView(t, new LinearLayout.LayoutParams(0, -2, 1f));
        if (running) {
            TextView badge = text(L.t("en cours", "running"), 11, L.onAction, true);
            badge.setPadding(dp(8), dp(3), dp(8), dp(3));
            badge.setBackground(rounded(L.action, 0, 10));
            row.addView(badge);
        }
        row.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) {
                closeDrawer();
                if ("about".equals(id)) { showAbout(); return; }
                if (S_TEST.equals(screen)) leaveTest();
                show(id);
            }
        });
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, dp(52));
        lp.bottomMargin = dp(2);
        row.setLayoutParams(lp);
        return row;
    }

    private void openDrawer() {
        fillDrawer();
        drawer.setVisibility(View.VISIBLE);
        if (drawerItems.getChildCount() > 0) drawerItems.getChildAt(0).requestFocus();
    }

    private void closeDrawer() { drawer.setVisibility(View.GONE); }

    private void showAbout() {
        String version = "";
        try { version = getPackageManager().getPackageInfo(getPackageName(), 0).versionName; } catch (Exception ignored) { }
        dialog().setTitle("ADB Tools Diag " + version)
                .setMessage(L.t("Appli de diagnostic d'ADB Tools Pro, toutes marques. Elle fonctionne reliée au PC ou seule. "
                                + "Aucune donnée n'est envoyée sur Internet.",
                        "ADB Tools Pro diagnostics app, for all brands. It works linked to the PC or on its own. "
                                + "No data is sent over the Internet."))
                .setPositiveButton("OK", null)
                .show();
    }

    private AlertDialog.Builder dialog() {
        return new AlertDialog.Builder(this, L.dark ? android.R.style.Theme_Material_Dialog_Alert : android.R.style.Theme_Material_Light_Dialog_Alert);
    }

    /** Affiche un écran ; quitter le lecteur ou les touches arrête leur mode. */
    private void show(String id) {
        if (!S_SCAN.equals(id) && scanMode) setScanMode(false);
        if (!S_KEYS.equals(id) && keysMode) { finishKeys(); }
        screen = id;
        for (Map.Entry<String, View> e : screens.entrySet()) e.getValue().setVisibility(e.getKey().equals(id) ? View.VISIBLE : View.GONE);
        boolean test = S_TEST.equals(id);
        navButton.setImageDrawable(iconDrawable(test ? "ic_back" : "ic_menu"));
        navButton.setColorFilter(L.titleInk, PorterDuff.Mode.SRC_IN);
        navButton.setContentDescription(test ? L.t("Retour au diagnostic", "Back to diagnostics") : L.t("Ouvrir le menu", "Open the menu"));
        String sub = "";
        switch (id) {
            case S_HOME: titleView.setText("ADB Tools Diag"); refreshHome(); break;
            case S_DIAG: titleView.setText(L.t("Diagnostic", "Diagnostics")); refreshLists(); break;
            case S_TEST:
                titleView.setText(activeTestId == null ? L.t("Test", "Test") : label(activeTestId));
                sub = stepText.length() > 0 && pcDriven ? "Test " + stepText
                        : activeTestId != null ? L.t("Test " + (indexOf(activeTestId) + 1) + " sur " + tests.length, "Test " + (indexOf(activeTestId) + 1) + " of " + tests.length) : "";
                testBanner.setVisibility(pcDriven ? View.VISIBLE : View.GONE);
                break;
            case S_SCAN:
                titleView.setText(L.t("Lecteur", "Scanner")); setScanMode(true);
                if (!"scanner".equals(activeTestId)) { activeTestId = "scanner"; pcDriven = false; }
                break;
            case S_DONE: titleView.setText(L.t("Diagnostic terminé", "Diagnostics complete")); refreshDone(); break;
            case S_WIFI: titleView.setText(L.t("Mesure Wi-Fi", "Wi-Fi survey")); refreshSurvey(); break;
            case S_KEYS: titleView.setText(L.t("Touches", "Keys")); startKeys(); break;
            case S_FIELD: titleView.setText(L.t("Suivi terrain", "Field monitoring")); refreshRecorder(); break;
            case S_RESULTS: titleView.setText(L.t("Résultats", "Results")); refreshLists(); break;
        }
        subtitleView.setText(sub);
        subtitleView.setVisibility(sub.length() > 0 ? View.VISIBLE : View.GONE);
        keepVisibleWhenLocked(S_WIFI.equals(id) && SurveyService.running != null || S_FIELD.equals(id) && RecorderService.running != null
                || S_KEYS.equals(id));
        refreshBackground();
    }

    // ------------------------------------------------------------------ Accueil

    private View buildHome() {
        LinearLayout p = page();
        LinearLayout card = card();
        LinearLayout top = new LinearLayout(this);
        top.setOrientation(LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.BOTTOM);
        homeModel = text(Build.MANUFACTURER + " " + Build.MODEL, 20, L.ink, true);
        top.addView(homeModel, new LinearLayout.LayoutParams(0, -2, 1f));
        homeAndroid = text("Android " + Build.VERSION.RELEASE, 12, L.muted, false);
        top.addView(homeAndroid);
        card.addView(top);
        LinearLayout stats = new LinearLayout(this);
        stats.setOrientation(LinearLayout.HORIZONTAL);
        stats.setPadding(0, dp(10), 0, 0);
        homeBattery = stat(stats, L.t("Batterie", "Battery"), true);
        homeWifi = stat(stats, "Wi-Fi", true);
        homeStorage = stat(stats, L.t("Stockage", "Storage"), false);
        card.addView(stats);
        homeLast = text("", 12, L.muted, false);
        homeLast.setPadding(0, dp(10), 0, 0);
        card.addView(homeLast);
        p.addView(card, lpMatch(0));

        pillHome = text(L.t("Sans PC : les résultats restent sur le terminal tant qu'il n'est pas relié à ADB Tools.",
                "Without a PC: results stay on the device until it is linked to ADB Tools."), 13, L.warnFg, false);
        pillHome.setBackground(rounded(L.warnBg, 0, 10));
        pillHome.setPadding(dp(14), dp(10), dp(14), dp(10));
        p.addView(pillHome, lpMatch(12));

        String[][] tiles = {
                { S_DIAG, "ic_diag", L.t("Diagnostic", "Diagnostics") }, { S_SCAN, "ic_scan", L.t("Lecteur", "Scanner") },
                { S_WIFI, "ic_wifi", "Wi-Fi" }, { S_KEYS, "ic_keys", L.t("Touches", "Keys") },
                { S_FIELD, "ic_field", L.t("Suivi terrain", "Field monitoring") }, { S_RESULTS, "ic_results", L.t("Résultats", "Results") } };
        for (int r = 0; r < tiles.length; r += 2) {
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            for (int c = 0; c < 2; c++) {
                final String[] t = tiles[r + c];
                LinearLayout tile = new LinearLayout(this);
                tile.setOrientation(LinearLayout.VERTICAL);
                tile.setPadding(dp(14), dp(12), dp(14), dp(12));
                tile.setFocusable(true);
                tile.setClickable(true);
                tile.setBackground(selector(L.surface, L.line, L.surface2, 12));
                tile.addView(icon(t[1], 26, L.brandText), new LinearLayout.LayoutParams(dp(26), dp(26)));
                View spacer = new View(this);
                tile.addView(spacer, new LinearLayout.LayoutParams(1, 0, 1f));
                tile.addView(text(t[2], 15, L.ink, true));
                tile.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { show(t[0]); } });
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(92), 1f);
                if (c == 0) lp.rightMargin = dp(5); else lp.leftMargin = dp(5);
                row.addView(tile, lp);
            }
            p.addView(row, lpMatch(10));
        }
        Button all = actionButton(L.t("Tout tester", "Test everything"));
        all.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { startAll(true); } });
        p.addView(all, lpButton(all, 52, 14));
        TextView foot = text(L.t("Le PC peut aussi lancer un test à tout moment : l'appli bascule sur le bon écran.",
                "The PC can also start a test at any time: the app switches to the right screen."), 12, L.muted, false);
        foot.setPadding(0, dp(14), 0, 0);
        p.addView(foot);
        return scroll(p);
    }

    private TextView stat(LinearLayout parent, String label, boolean gap) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setBackground(rounded(L.surface2, 0, 8));
        box.setPadding(dp(10), dp(8), dp(10), dp(8));
        box.addView(text(label, 11, L.muted, false));
        TextView v = text("—", 16, L.ink, true);
        box.addView(v);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1f);
        if (gap) lp.rightMargin = dp(8);
        parent.addView(box, lp);
        return v;
    }

    @SuppressWarnings("deprecation")
    private void refreshHome() {
        if (homeBattery == null) return;
        try {
            Intent b = registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
            int level = b == null ? -1 : b.getIntExtra(BatteryManager.EXTRA_LEVEL, -1), scale = b == null ? 100 : b.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
            homeBattery.setText(level < 0 ? "—" : Math.round(100f * level / Math.max(1, scale)) + " %");
        } catch (Exception e) { homeBattery.setText("—"); }
        try {
            WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            WifiInfo wi = wm == null ? null : wm.getConnectionInfo();
            homeWifi.setText(wm == null || !wm.isWifiEnabled() ? L.t("Coupé", "Off") : wi == null || wi.getNetworkId() == -1 ? "—" : wi.getRssi() + " dBm");
        } catch (Exception e) { homeWifi.setText("—"); }
        try {
            StatFs fs = new StatFs(Environment.getDataDirectory().getPath());
            long total = fs.getBlockCountLong() * fs.getBlockSizeLong(), free = fs.getAvailableBlocksLong() * fs.getBlockSizeLong();
            homeStorage.setText(total <= 0 ? "—" : Math.round(100f * (total - free) / total) + " %");
        } catch (Exception e) { homeStorage.setText("—"); }
        int[] c = counts();
        homeLast.setText(c[0] + c[1] + c[2] == 0 ? L.t("Aucun test pour l'instant.", "No test yet.")
                : L.t("Cette session : " + c[0] + " OK · " + c[1] + " à surveiller · " + c[2] + " KO",
                      "This session: " + c[0] + " OK · " + c[1] + " to monitor · " + c[2] + " faulty"));
    }

    /** OK, à surveiller, KO, testés. */
    private int[] counts() {
        int ok = 0, warn = 0, bad = 0, done = 0;
        for (String id : tests) {
            String s = status.get(id);
            if (s == null) continue;
            done++;
            if ("ok".equals(s)) ok++; else if ("warn".equals(s)) warn++; else if ("bad".equals(s)) bad++;
        }
        return new int[] { ok, warn, bad, done };
    }

    /** « Tout tester » : reprend au premier test non fait (ou recommence tout si reset). */
    private void startAll(boolean reset) {
        if (reset) { status.clear(); statusAt.clear(); Out.clearResults(); }
        String next = nextUntested(null);
        if (next == null) { show(S_DIAG); return; }
        runAll = true;
        startTest(next, false);
    }

    // ------------------------------------------------------------------ Diagnostic

    private View buildDiag() {
        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        LinearLayout head = new LinearLayout(this);
        head.setOrientation(LinearLayout.VERTICAL);
        head.setPadding(dp(16), dp(14), dp(16), dp(10));
        LinearLayout line = new LinearLayout(this);
        line.setGravity(Gravity.CENTER_VERTICAL);
        diagProgressText = text("", 13, L.muted, false);
        line.addView(diagProgressText, new LinearLayout.LayoutParams(0, -2, 1f));
        Button redo = secondaryButton(L.t("Tout refaire", "Start over"));
        redo.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        redo.setMinHeight(dp(36));
        redo.setMinimumHeight(dp(36));
        redo.setPadding(dp(12), 0, dp(12), 0);
        redo.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { startAll(true); } });
        line.addView(redo, new LinearLayout.LayoutParams(-2, dp(36)));
        head.addView(line);
        diagSummary = text("", 13, L.muted, false);
        diagSummary.setPadding(0, dp(6), 0, 0);
        FrameLayout bar = new FrameLayout(this);
        bar.setBackground(rounded(L.surface2, 0, 3));
        diagProgressFill = new View(this);
        diagProgressFill.setBackground(rounded(L.brandText, 0, 3));
        bar.addView(diagProgressFill, new FrameLayout.LayoutParams(0, dp(6)));
        LinearLayout.LayoutParams blp = new LinearLayout.LayoutParams(-1, dp(6));
        blp.topMargin = dp(8);
        head.addView(bar, blp);
        head.addView(diagSummary);
        outer.addView(head);

        diagList = new LinearLayout(this);
        diagList.setOrientation(LinearLayout.VERTICAL);
        diagList.setPadding(dp(16), 0, dp(16), dp(8));
        ScrollView sv = new ScrollView(this);
        sv.addView(diagList);
        outer.addView(sv, new LinearLayout.LayoutParams(-1, 0, 1f));

        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.VERTICAL);
        actions.setPadding(dp(16), dp(10), dp(16), dp(12));
        diagContinue = actionButton("");
        diagContinue.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { startAll(false); } });
        actions.addView(diagContinue, lpButton(diagContinue, 52, 0));
        outer.addView(actions);
        return outer;
    }

    private void refreshLists() {
        if (diagList != null) {
            diagList.removeAllViews();
            String next = nextUntested(null);
            for (final String id : tests) {
                LinearLayout row = new LinearLayout(this);
                row.setOrientation(LinearLayout.HORIZONTAL);
                row.setGravity(Gravity.CENTER_VERTICAL);
                row.setPadding(dp(14), 0, dp(14), 0);
                row.setFocusable(true);
                row.setClickable(true);
                boolean isNext = id.equals(next);
                row.setBackground(selector(L.surface, isNext ? L.action : 0, L.surface2, 10));
                row.addView(text(label(id), 15, L.ink, false), new LinearLayout.LayoutParams(0, -2, 1f));
                row.addView(statusPill(status.get(id)));
                row.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { runAll = false; startTest(id, false); } });
                row.setMinimumHeight(dp(52));
                row.setPadding(dp(14), dp(8), dp(14), dp(8));
                diagList.addView(row, lpMatch(6));
            }
            int[] c = counts();
            diagProgressText.setText(L.t(c[3] + " tests sur " + tests.length, c[3] + " of " + tests.length + " tests"));
            diagSummary.setText(c[1] + c[2] == 0 ? "" : L.t(c[1] + " à surveiller · " + c[2] + " KO", c[1] + " to monitor · " + c[2] + " faulty"));
            diagSummary.setVisibility(c[1] + c[2] == 0 ? View.GONE : View.VISIBLE);
            ViewGroup.LayoutParams lp = diagProgressFill.getLayoutParams();
            int full = getResources().getDisplayMetrics().widthPixels - dp(32);
            lp.width = Math.round(full * c[3] / (float) tests.length);
            diagProgressFill.setLayoutParams(lp);
            diagContinue.setText(next == null ? L.t("Diagnostic terminé", "Diagnostics complete") : (c[3] == 0 ? L.t("Commencer : ", "Start: ") : L.t("Continuer : ", "Continue: ")) + label(next));
            diagContinue.setEnabled(next != null);
            diagContinue.setAlpha(next == null ? 0.6f : 1f);
        }
        if (resultsList != null) {
            resultsList.removeAllViews();
            SimpleDateFormat f = new SimpleDateFormat("HH:mm", Locale.US);
            boolean any = false;
            for (String id : tests) {
                String s = status.get(id);
                if (s == null) continue;
                any = true;
                LinearLayout row = new LinearLayout(this);
                row.setOrientation(LinearLayout.HORIZONTAL);
                row.setGravity(Gravity.CENTER_VERTICAL);
                row.setPadding(dp(14), 0, dp(14), 0);
                row.setFocusable(true);
                row.setClickable(true);
                row.setBackground(selector(L.surface, 0, L.surface2, 10));
                final String tid = id;
                row.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { runAll = false; startTest(tid, false); } });
                row.addView(text(label(id), 15, L.ink, false), new LinearLayout.LayoutParams(0, -2, 1f));
                Long at = statusAt.get(id);
                if (at != null) { TextView tm = text(f.format(new Date(at)), 12, L.muted, false); tm.setPadding(0, 0, dp(10), 0); row.addView(tm); }
                row.addView(statusPill(s));
                row.setMinimumHeight(dp(52));
                row.setPadding(dp(14), dp(8), dp(14), dp(8));
                resultsList.addView(row, lpMatch(6));
            }
            if (!any) {
                TextView empty = text(L.t("Aucun résultat pour l'instant. Lancez un diagnostic depuis l'accueil ou depuis le PC.",
                        "No results yet. Start diagnostics from the home screen or from the PC."), 14, L.muted, false);
                resultsList.addView(empty);
            }
        }
        if (S_HOME.equals(screen)) refreshHome();
    }

    private TextView statusPill(String s) {
        TextView p = text(s == null ? L.t("Non testé", "Not tested") : statusLabel(s), 12, L.muted, s != null);
        p.setPadding(dp(10), dp(4), dp(10), dp(4));
        if (s != null) styleStatus(p, s);
        return p;
    }

    private static String statusLabel(String s) {
        switch (s) {
            case "ok": return "OK";
            case "warn": return L.t("À surveiller", "To monitor");
            case "bad": return "KO";
            case "confirm": return L.t("À confirmer", "To confirm");
            case "na": return L.t("Absent", "Not present");
            case "skip": return L.t("Passé", "Skipped");
            default: return s;
        }
    }

    private void styleStatus(TextView v, String s) {
        int bg = L.surface2, fg = L.muted;
        if ("ok".equals(s)) { bg = L.okBg; fg = L.okFg; }
        else if ("warn".equals(s)) { bg = L.warnBg; fg = L.warnFg; }
        else if ("bad".equals(s)) { bg = L.badBg; fg = L.badFg; }
        else if ("confirm".equals(s)) { bg = L.infoBg; fg = L.infoFg; }
        v.setTextColor(fg);
        v.setBackground(rounded(bg, 0, 10));
    }

    // ------------------------------------------------------------------ Écran d'un test

    private View buildTest() {
        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        testBanner = text(L.t("Lancé depuis le PC. Le résultat peut se donner ici ou sur le PC.",
                "Started from the PC. The result can be given here or on the PC."), 13, L.infoFg, false);
        testBanner.setBackgroundColor(L.infoBg);
        testBanner.setPadding(dp(16), dp(9), dp(16), dp(9));
        outer.addView(testBanner, new LinearLayout.LayoutParams(-1, -2));

        LinearLayout p = page();
        previewBox = new FrameLayout(this);
        previewBox.setBackground(rounded(L.previewBg, L.line, 12));
        previewBox.setPadding(dp(1), dp(1), dp(1), dp(1));
        preview = new TextureView(this);
        previewBox.addView(preview, new FrameLayout.LayoutParams(-1, -1));
        previewBox.setVisibility(View.GONE);
        p.addView(previewBox, lpHeight(300, 0));
        testMessage = text("", 16, L.ink, true);
        testMessage.setPadding(0, dp(12), 0, 0);
        p.addView(testMessage);
        testStatus = text("", 14, L.brandText, false);
        testStatus.setPadding(0, dp(8), 0, 0);
        p.addView(testStatus);
        testLive = new LinearLayout(this);
        testLive.setOrientation(LinearLayout.VERTICAL);
        p.addView(testLive, lpMatch(10));
        testAuto = text("", 13, L.okFg, true);
        testAuto.setPadding(dp(12), dp(6), dp(12), dp(6));
        testAuto.setVisibility(View.GONE);
        LinearLayout.LayoutParams alp = new LinearLayout.LayoutParams(-2, -2);
        alp.topMargin = dp(10);
        p.addView(testAuto, alp);
        testDetail = text("", 13, L.ink, false);
        testDetail.setPadding(0, dp(8), 0, 0);
        p.addView(testDetail);
        outer.addView(scroll(p), new LinearLayout.LayoutParams(-1, 0, 1f));
        outer.addView(verdictArea(true));
        return outer;
    }

    /** OK / À surveiller / KO, puis « Relancer » et « Passer ce test ». */
    private LinearLayout verdictArea(boolean withRestart) {
        LinearLayout v = new LinearLayout(this);
        v.setOrientation(LinearLayout.VERTICAL);
        v.setPadding(dp(16), dp(10), dp(16), dp(14));
        LinearLayout row = new LinearLayout(this);
        row.addView(verdictButton("OK", "ok", L.okBg, L.okFg), verdictWeight(false));
        row.addView(verdictButton(L.t("À surveiller", "To monitor"), "warn", L.warnBg, L.warnFg), verdictWeight(true));
        row.addView(verdictButton("KO", "bad", L.badBg, L.badFg), verdictWeight(true));
        v.addView(row);
        LinearLayout row2 = new LinearLayout(this);
        if (withRestart) {
            testRestart = secondaryButton(L.t("Relancer", "Run again"));
            testRestart.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View x) { if (activeTestId != null) { boolean all = runAll; startTest(activeTestId, pcDriven); runAll = all; } }
            });
            testRestart.setVisibility(View.GONE);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(48), 1f);
            lp.rightMargin = dp(8);
            row2.addView(testRestart, lp);
        }
        Button skip = secondaryButton(L.t("Passer ce test  »", "Skip this test  »"));
        skip.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View x) { sendVerdict("skip"); } });
        row2.addView(skip, new LinearLayout.LayoutParams(0, dp(48), 1f));
        LinearLayout.LayoutParams l2 = new LinearLayout.LayoutParams(-1, -2);
        l2.topMargin = dp(8);
        v.addView(row2, l2);
        return v;
    }

    private void setTestStatus(String text, int color) {
        if (testStatus == null) return;
        testStatus.setText(text);
        testStatus.setTextColor(color);
    }

    private Button verdictButton(String label, final String verdict, int bg, int fg) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, label.length() > 4 ? 13 : 15);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setTextColor(fg);
        b.setBackground(selector(bg, fg, bg, 10));
        b.setPadding(dp(4), 0, dp(4), 0);
        if (Build.VERSION.SDK_INT >= 21) b.setStateListAnimator(null);
        b.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { sendVerdict(verdict); } });
        return b;
    }

    private LinearLayout.LayoutParams verdictWeight(boolean gap) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(62), 1f);
        if (gap) lp.leftMargin = dp(8);
        return lp;
    }

    // ------------------------------------------------------------------ Lecteur code-barres

    private View buildScan() {
        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        LinearLayout p = page();
        LinearLayout last = card();
        last.setBackground(rounded(L.surface, L.sideActive, 12));
        scanLastLabel = text(L.t("Dernière lecture", "Last read"), 12, L.muted, false);
        last.addView(scanLastLabel);
        scanLastData = text(L.t("Scannez un code-barres", "Scan a barcode"), 22, L.brandText, true);
        scanLastData.setTypeface(Typeface.MONOSPACE, Typeface.BOLD);
        scanLastData.setPadding(0, dp(4), 0, dp(4));
        last.addView(scanLastData);
        scanLastInfo = text("", 12, L.muted, false);
        last.addView(scanLastInfo);
        p.addView(last, lpMatch(0));

        LinearLayout stats = new LinearLayout(this);
        scanCountView = stat(stats, L.t("Lectures", "Reads"), true);
        scanDistinctView = stat(stats, L.t("Codes différents", "Distinct codes"), true);
        scanSourceView = stat(stats, L.t("Réception", "Received by"), false);
        p.addView(stats, lpMatch(12));

        TextView prev = text(L.t("Lectures précédentes", "Previous reads"), 13, L.muted, true);
        prev.setPadding(0, dp(14), 0, dp(6));
        p.addView(prev);
        scanRecentList = new LinearLayout(this);
        scanRecentList.setOrientation(LinearLayout.VERTICAL);
        p.addView(scanRecentList);

        // Lecteur en mode clavier : les caractères arrivent dans ce champ (toutes marques).
        input = new EditText(this);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        input.setTextColor(L.ink);
        input.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        input.setHint(L.t("Lecture en mode clavier", "Keyboard-mode reading"));
        input.setHintTextColor(L.muted);
        input.setBackground(rounded(L.surface2, L.line, 8));
        input.setPadding(dp(12), dp(8), dp(12), dp(8));
        if (Build.VERSION.SDK_INT >= 21) input.setShowSoftInputOnFocus(false);
        input.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int st, int c, int a) { }
            @Override public void onTextChanged(CharSequence s, int st, int b, int c) { }
            @Override public void afterTextChanged(Editable s) {
                handler.removeCallbacks(flushKeyboard);
                if (s.length() == 0 || !scanMode) return;
                if (s.toString().indexOf('\n') >= 0) flushKeyboardInput();
                else handler.postDelayed(flushKeyboard, 250);
            }
        });
        p.addView(input, lpMatch(14));
        TextView hint = text(L.t("Appuyez sur la gâchette ou une touche de lecture. Les lectures s'affichent aussi dans ADB Tools si le terminal est relié.",
                "Press the trigger or a scan key. Reads are also shown in ADB Tools when the device is linked."), 13, L.muted, false);
        hint.setGravity(Gravity.CENTER_HORIZONTAL);
        hint.setPadding(0, dp(12), 0, 0);
        p.addView(hint);
        outer.addView(scroll(p), new LinearLayout.LayoutParams(-1, 0, 1f));
        outer.addView(verdictArea(false));
        return outer;
    }

    /** Nouvelle session de lecture (test « Lecteur code-barres » lancé). */
    private void resetScanSession() {
        scanCount = 0;
        scanDistinct.clear();
        scanRecent.clear();
        if (scanLastData != null) {
            scanLastData.setText(L.t("Scannez un code-barres", "Scan a barcode"));
            scanLastLabel.setText(L.t("Dernière lecture", "Last read"));
            scanLastInfo.setText("");
        }
        refreshScan();
    }

    /** Bilan des lectures, envoyé avec le verdict du technicien (gardé dans les résultats). */
    private void emitScannerResult() {
        JSONArray sources = new JSONArray();
        HashSet<String> seen = new HashSet<String>();
        for (String[] r : scanRecent) if (seen.add(r[2])) sources.put(r[2]);
        Out.result("scanner", scanCount > 0 ? "ok" : "warn", Out.obj("reads", scanCount, "distinct", scanDistinct.size(), "sources", sources,
                "last", scanRecent.isEmpty() ? "" : scanRecent.get(0)[0], "symbology", scanRecent.isEmpty() ? "" : scanRecent.get(0)[1]));
    }

    private void setScanMode(boolean on) {
        if (scanMode == on) return;
        scanMode = on;
        if (on) {
            input.requestFocus();
            if (resumed) enableScanOutputs();
            refreshScan();
        } else {
            handler.removeCallbacks(flushKeyboard);
            if (input != null) input.setText("");
            disableScanOutputs();
        }
    }

    private void enableScanOutputs() {
        if (!receiverRegistered) {
            IntentFilter filter = new IntentFilter();
            filter.addAction(ACTION_DATAWEDGE);
            filter.addAction(ACTION_HONEYWELL);
            filter.addCategory(Intent.CATEGORY_DEFAULT);
            registerReceiver(scanReceiver, filter);
            receiverRegistered = true;
        }
        configureDataWedge();
        claimHoneywell();
        input.requestFocus();
    }

    private void disableScanOutputs() {
        if (receiverRegistered) {
            try { unregisterReceiver(scanReceiver); } catch (Exception ignored) { }
            receiverRegistered = false;
        }
        releaseHoneywell();
    }

    private void flushKeyboardInput() {
        if (input == null) return;
        String text = input.getText().toString();
        if (text.length() == 0) return;
        input.setText("");
        for (String part : text.split("\n")) {
            if (part.length() == 0) continue;
            if (lastIntentData != null && part.equals(lastIntentData) && System.currentTimeMillis() - lastIntentAt < 1500) continue;
            emitScan(part, "keyboard", "");
        }
    }

    private void onIntentScan(String data, String source, String symbology) {
        if (data == null || !scanMode) return;
        lastIntentData = data.replace("\n", "");
        lastIntentAt = System.currentTimeMillis();
        emitScan(data, source, symbology);
    }

    private void emitScan(String data, String source, String symbology) {
        scanCount++;
        scanDistinct.add(data);
        scanRecent.add(0, new String[] { data, symbology == null ? "" : symbology, source, new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date()) });
        while (scanRecent.size() > 6) scanRecent.remove(scanRecent.size() - 1);
        Out.scan(source, symbology, data);
        refreshScan();
    }

    private static String sourceLabel(String s) {
        if ("datawedge".equals(s)) return "DataWedge";
        if ("honeywell".equals(s)) return "Honeywell";
        return L.t("Clavier", "Keyboard");
    }

    private void refreshScan() {
        if (scanCountView == null) return;
        scanCountView.setText(String.valueOf(scanCount));
        scanDistinctView.setText(String.valueOf(scanDistinct.size()));
        scanRecentList.removeAllViews();
        if (scanRecent.isEmpty()) { scanSourceView.setText("—"); return; }
        String[] last = scanRecent.get(0);
        scanSourceView.setText(sourceLabel(last[2]));
        scanLastData.setText(last[0]);
        scanLastLabel.setText(L.t("Dernière lecture", "Last read") + (last[1].length() > 0 ? " · " + last[1] : "") + " · " + last[3]);
        scanLastInfo.setText(L.t(last[0].length() + " caractères · reçu par ", last[0].length() + " characters · received by ") + sourceLabel(last[2]));
        for (int i = 1; i < scanRecent.size(); i++) {
            String[] r = scanRecent.get(i);
            LinearLayout row = new LinearLayout(this);
            row.setPadding(dp(12), dp(10), dp(12), dp(10));
            row.setBackground(rounded(L.surface, 0, 8));
            TextView d = text((r[1].length() > 0 ? r[1] + " · " : "") + r[0], 13, L.ink, false);
            d.setTypeface(Typeface.MONOSPACE);
            d.setSingleLine(true);
            d.setEllipsize(TextUtils.TruncateAt.END);
            row.addView(d, new LinearLayout.LayoutParams(0, -2, 1f));
            TextView tm = text(r[3], 12, L.muted, false);
            tm.setPadding(dp(10), 0, 0, 0);
            row.addView(tm);
            scanRecentList.addView(row, lpMatch(i == 1 ? 0 : 6));
        }
    }

    private void configureDataWedge() {
        try {
            Bundle profile = new Bundle();
            profile.putString("PROFILE_NAME", DW_PROFILE);
            profile.putString("PROFILE_ENABLED", "true");
            profile.putString("CONFIG_MODE", "CREATE_IF_NOT_EXIST");
            Bundle app = new Bundle();
            app.putString("PACKAGE_NAME", getPackageName());
            app.putStringArray("ACTIVITY_LIST", new String[]{"*"});
            profile.putParcelableArray("APP_LIST", new Bundle[]{app});

            ArrayList<Bundle> plugins = new ArrayList<Bundle>();
            Bundle barcode = new Bundle();
            barcode.putString("scanner_selection", "auto");
            barcode.putString("scanner_input_enabled", "true");
            plugins.add(plugin("BARCODE", barcode, "false"));
            Bundle intentParams = new Bundle();
            intentParams.putString("intent_output_enabled", "true");
            intentParams.putString("intent_action", ACTION_DATAWEDGE);
            intentParams.putString("intent_category", Intent.CATEGORY_DEFAULT);
            intentParams.putString("intent_delivery", "2");
            plugins.add(plugin("INTENT", intentParams, "true"));
            Bundle keystroke = new Bundle();
            keystroke.putString("keystroke_output_enabled", "false");
            plugins.add(plugin("KEYSTROKE", keystroke, "true"));
            profile.putParcelableArrayList("PLUGIN_CONFIG", plugins);

            Intent config = new Intent("com.symbol.datawedge.api.ACTION");
            config.putExtra("com.symbol.datawedge.api.SET_CONFIG", profile);
            sendBroadcast(config);
            handler.postDelayed(new Runnable() {
                @Override public void run() {
                    Intent sw = new Intent("com.symbol.datawedge.api.ACTION");
                    sw.putExtra("com.symbol.datawedge.api.SWITCH_TO_PROFILE", DW_PROFILE);
                    sendBroadcast(sw);
                }
            }, 400);
        } catch (Exception e) {
            Log.w(Out.TAG, "DataWedge: " + e.getMessage());
        }
    }

    private static Bundle plugin(String name, Bundle params, String reset) {
        Bundle b = new Bundle();
        b.putString("PLUGIN_NAME", name);
        b.putString("RESET_CONFIG", reset);
        b.putBundle("PARAM_LIST", params);
        return b;
    }

    private void claimHoneywell() {
        try {
            Bundle properties = new Bundle();
            properties.putBoolean("DPR_DATA_INTENT", true);
            properties.putString("DPR_DATA_INTENT_ACTION", ACTION_HONEYWELL);
            Intent claim = new Intent("com.honeywell.aidc.action.ACTION_CLAIM_SCANNER");
            claim.setPackage(HONEYWELL_SERVICE);
            claim.putExtra("com.honeywell.aidc.extra.EXTRA_SCANNER", "dcs.scanner.imager");
            claim.putExtra("com.honeywell.aidc.extra.EXTRA_PROFILE", "DEFAULT");
            claim.putExtra("com.honeywell.aidc.extra.EXTRA_PROPERTIES", properties);
            sendBroadcast(claim);
        } catch (Exception e) {
            Log.w(Out.TAG, "Honeywell: " + e.getMessage());
        }
    }

    private void releaseHoneywell() {
        try {
            Intent release = new Intent("com.honeywell.aidc.action.ACTION_RELEASE_SCANNER");
            release.setPackage(HONEYWELL_SERVICE);
            sendBroadcast(release);
        } catch (Exception ignored) { }
    }

    // ------------------------------------------------------------------ Touches

    private View buildKeys() {
        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);
        LinearLayout p = page();
        TextView help = text(L.t("Appuyez sur chaque touche du terminal : son nom s'affiche. Marche : appuyez une fois (l'écran s'éteint), puis rallumez. La touche Retour quitte ce test.",
                "Press every key on the device: its name is shown. Power: press it once (the screen turns off), then turn it back on. The Back key leaves this test."), 15, L.ink, true);
        p.addView(help);
        LinearLayout c = card();
        keysBig = text("—", 34, L.brandText, true);
        keysBig.setGravity(Gravity.CENTER_HORIZONTAL);
        c.addView(keysBig);
        keysCount = text("", 13, L.muted, false);
        keysCount.setGravity(Gravity.CENTER_HORIZONTAL);
        c.addView(keysCount);
        p.addView(c, lpMatch(14));
        keysList = text("", 13, L.ink, false);
        keysList.setTypeface(Typeface.MONOSPACE);
        keysList.setPadding(0, dp(12), 0, 0);
        p.addView(keysList);
        outer.addView(scroll(p), new LinearLayout.LayoutParams(-1, 0, 1f));
        keysVerdict = verdictArea(false);
        outer.addView(keysVerdict);
        return outer;
    }

    // La touche Marche n'arrive jamais à une appli : on la reconnaît à l'extinction de l'écran pendant le test.
    private final BroadcastReceiver powerReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) {
            if (!keysMode || !Intent.ACTION_SCREEN_OFF.equals(i.getAction())) return;
            Integer n = keys.get("POWER");
            keys.put("POWER", n == null ? 1 : n + 1);
            Out.live("keys", Out.obj("key", "POWER", "distinct", keys.size()));
            refreshKeys("POWER");
        }
    };
    private boolean powerRegistered;

    private void refreshKeys(String last) {
        keysBig.setText(last);
        keysCount.setText(L.t(keys.size() + " touche(s) différente(s)", keys.size() + " distinct key(s)"));
        StringBuilder sb = new StringBuilder();
        for (Map.Entry<String, Integer> e : keys.entrySet()) sb.append(e.getKey()).append(" ×").append(e.getValue()).append("   ");
        keysList.setText(sb.toString().trim());
    }

    private void startKeys() {
        if (keysMode) return;
        keysMode = true;
        keys.clear();
        if (!powerRegistered) {
            registerReceiver(powerReceiver, new IntentFilter(Intent.ACTION_SCREEN_OFF));
            powerRegistered = true;
        }
        if (!"keys".equals(activeTestId)) { activeTestId = "keys"; pcDriven = false; }
        keysBig.setText("—");
        keysCount.setText(L.t("Aucune touche pour l'instant", "No key yet"));
        keysList.setText("");
        Out.state("keys", "started");
    }

    private void finishKeys() {
        if (!keysMode) return;
        keysMode = false;
        if (powerRegistered) { try { unregisterReceiver(powerReceiver); } catch (Exception ignored) { } powerRegistered = false; }
        // Simple visite de l'écran Touches sans rien presser : pas de résultat.
        if (keys.isEmpty() && !pcDriven) { Out.state("keys", "stopped"); return; }
        JSONArray list = new JSONArray();
        for (Map.Entry<String, Integer> e : keys.entrySet()) list.put(Out.obj("key", e.getKey(), "count", e.getValue()));
        Out.result("keys", keys.isEmpty() ? "warn" : "confirm", Out.obj("keys", list));
        Out.state("keys", "stopped");
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (current instanceof RfidTest && ((RfidTest) current).onKey(event)) return true;
        if (keysMode && S_KEYS.equals(screen)) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                String name = KeyEvent.keyCodeToString(event.getKeyCode()).replace("KEYCODE_", "");
                Integer n = keys.get(name);
                keys.put(name, n == null ? 1 : n + 1);
                refreshKeys(name);
                Out.live("keys", Out.obj("key", name, "distinct", keys.size()));
            }
            // Retour et accueil gardent leur rôle ; les autres touches restent dans le test.
            if (event.getKeyCode() == KeyEvent.KEYCODE_BACK || event.getKeyCode() == KeyEvent.KEYCODE_HOME) return super.dispatchKeyEvent(event);
            return true;
        }
        if (scanMode && event.getAction() == KeyEvent.ACTION_UP
                && (event.getKeyCode() == KeyEvent.KEYCODE_ENTER || event.getKeyCode() == KeyEvent.KEYCODE_NUMPAD_ENTER)) {
            handler.removeCallbacks(flushKeyboard);
            flushKeyboardInput();
            return true;
        }
        if (event.getAction() == KeyEvent.ACTION_UP && event.getKeyCode() == KeyEvent.KEYCODE_MENU) {
            if (drawer.getVisibility() == View.VISIBLE) closeDrawer(); else openDrawer();
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    // ------------------------------------------------------------------ Mesure Wi-Fi

    private View buildWifi() {
        LinearLayout p = page();
        wifiRunning = new LinearLayout(this);
        wifiRunning.setOrientation(LinearLayout.VERTICAL);
        LinearLayout c = card();
        wifiBig = text("—", 44, L.brandText, true);
        wifiBig.setGravity(Gravity.CENTER_HORIZONTAL);
        c.addView(wifiBig);
        wifiQuality = text("", 15, L.ink, true);
        wifiQuality.setGravity(Gravity.CENTER_HORIZONTAL);
        c.addView(wifiQuality);
        wifiDetail = text("", 13, L.ink, false);
        wifiDetail.setPadding(0, dp(12), 0, 0);
        c.addView(wifiDetail);
        wifiRunning.addView(c);
        wifiEvents = text("", 13, L.muted, false);
        wifiEvents.setPadding(0, dp(12), 0, 0);
        wifiRunning.addView(wifiEvents);
        Button mark = actionButton(L.t("Poser un repère", "Add a marker"));
        mark.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { askMark(); } });
        wifiRunning.addView(mark, lpButton(mark, 54, 16));
        Button stop = secondaryButton(L.t("Arrêter la mesure", "Stop measuring"));
        stop.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { confirmStopSurvey(); } });
        wifiRunning.addView(stop, lpButton(stop, 48, 10));
        TextView tip = text(L.t("Vous pouvez débrancher le terminal et vous déplacer. Posez des repères aux endroits clés (quai, rayon, bureau…).",
                "You can unplug the device and walk around. Add markers at key places (dock, aisle, office…)."), 13, L.muted, false);
        tip.setPadding(0, dp(12), 0, 0);
        wifiRunning.addView(tip);
        p.addView(wifiRunning);

        wifiStopped = new LinearLayout(this);
        wifiStopped.setOrientation(LinearLayout.VERTICAL);
        wifiIdle = text("", 15, L.ink, false);
        wifiStopped.addView(wifiIdle);
        Button start = actionButton(L.t("Démarrer une mesure", "Start a survey"));
        start.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { askSurveyTarget(); } });
        wifiStopped.addView(start, lpButton(start, 52, 16));
        p.addView(wifiStopped);
        return scroll(p);
    }

    /** Mesure lancée depuis l'appli : cible du ping (passerelle, serveur public ou adresse au choix). */
    private void askSurveyTarget() {
        final String[] targets = { "", "8.8.8.8", "1.1.1.1", null };
        String[] labels = { L.t("Passerelle du réseau (automatique)", "Network gateway (automatic)"), "8.8.8.8 (Google)", "1.1.1.1 (Cloudflare)",
                L.t("Autre adresse…", "Other address…") };
        dialog().setTitle(L.t("Cible du ping", "Ping target"))
                .setItems(labels, new android.content.DialogInterface.OnClickListener() {
                    @Override public void onClick(android.content.DialogInterface d, int which) {
                        if (targets[which] != null) { startSurveyTo(targets[which]); return; }
                        final EditText addr = new EditText(MainActivity.this);
                        addr.setHint(L.t("Adresse IP ou nom de serveur", "IP address or server name"));
                        addr.setSingleLine(true);
                        dialog().setTitle(L.t("Cible du ping", "Ping target"))
                                .setView(addr)
                                .setPositiveButton(L.t("Démarrer", "Start"), new android.content.DialogInterface.OnClickListener() {
                                    @Override public void onClick(android.content.DialogInterface d2, int w) {
                                        String a = addr.getText().toString().trim();
                                        startSurveyTo(a.matches("[A-Za-z0-9.\\-:]+") ? a : "");
                                    }
                                })
                                .setNegativeButton(L.t("Annuler", "Cancel"), null).show();
                    }
                })
                .setNegativeButton(L.t("Annuler", "Cancel"), null)
                .show();
    }

    private void startSurveyTo(String target) {
        Bundle b = new Bundle();
        b.putString(SurveyService.EXTRA_TARGET, target);
        b.putInt(SurveyService.EXTRA_INTERVAL, 1000);
        b.putInt(SurveyService.EXTRA_DURATION, 0);
        startSurvey(b);
    }

    private void startSurvey(Bundle extras) {
        Intent i = new Intent(this, SurveyService.class);
        i.putExtra(SurveyService.EXTRA_INTERVAL, 1000);
        if (extras != null) {
            i.putExtra(SurveyService.EXTRA_TARGET, extras.getString(SurveyService.EXTRA_TARGET, ""));
            i.putExtra(SurveyService.EXTRA_INTERVAL, parseInt(extras.get(SurveyService.EXTRA_INTERVAL), 1000));
            i.putExtra(SurveyService.EXTRA_DURATION, parseInt(extras.get(SurveyService.EXTRA_DURATION), 0));
        }
        surveyStartRequestedAt = System.currentTimeMillis();
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);
        show(S_WIFI);
    }

    private static int parseInt(Object o, int def) {
        if (o == null) return def;
        if (o instanceof Integer) return (Integer) o;
        try { return Integer.parseInt(String.valueOf(o).trim()); } catch (Exception e) { return def; }
    }

    @SuppressWarnings("deprecation")
    private void keepVisibleWhenLocked(boolean on) {
        try {
            if (Build.VERSION.SDK_INT >= 27) { setShowWhenLocked(on); setTurnScreenOn(on); }
            else if (on) getWindow().addFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
            else getWindow().clearFlags(WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED | WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON);
        } catch (Exception ignored) { }
    }

    private void refreshSurvey() {
        if (wifiRunning == null) return;
        SurveyService s = SurveyService.running;
        boolean starting = s == null && System.currentTimeMillis() - surveyStartRequestedAt < 15000;
        wifiRunning.setVisibility(s != null || starting ? View.VISIBLE : View.GONE);
        wifiStopped.setVisibility(s != null || starting ? View.GONE : View.VISIBLE);
        if (starting) {
            wifiBig.setText("…");
            wifiBig.setTextColor(L.muted);
            wifiQuality.setText(L.t("Démarrage de la mesure…", "Starting the survey…"));
            wifiDetail.setText("");
            return;
        }
        if (s == null) {
            wifiIdle.setText(surveyStartRequestedAt > 0
                    ? L.t("Mesure terminée. Rebranchez le terminal au PC et récupérez-la dans ADB Tools (Analyse Wi-Fi ▸ Mesure sans câble).",
                          "Survey finished. Plug the device back into the PC and retrieve it in ADB Tools (Wi-Fi Analysis ▸ Cable-free survey).")
                    : L.t("Mesure le signal Wi-Fi en vous déplaçant, terminal débranché : force du signal, changements de borne, coupures. "
                          + "La mesure se récupère ensuite dans ADB Tools (Analyse Wi-Fi ▸ Mesure sans câble), qui peut aussi la lancer.",
                          "Measures the Wi-Fi signal while you walk around with the device unplugged: signal strength, roaming, drops. "
                          + "Retrieve it afterwards in ADB Tools (Wi-Fi Analysis ▸ Cable-free survey), which can also start it."));
            return;
        }
        long now = System.currentTimeMillis();
        long sec = (now - s.startedAt) / 1000;
        int rssi = s.lastRssi;
        wifiBig.setText(rssi != 0 ? rssi + " dBm" : "—");
        wifiBig.setTextColor(rssi == 0 ? L.muted : rssi >= -65 ? L.okFg : rssi >= -75 ? L.warnFg : L.badFg);
        String quality = rssi == 0 ? L.t("Pas de Wi-Fi", "No Wi-Fi") : rssi >= -60 ? L.t("Excellent", "Excellent") : rssi >= -67 ? L.t("Bon", "Good")
                : rssi >= -75 ? L.t("Moyen", "Fair") : L.t("Faible", "Weak");
        long age = s.lastSampleAt == 0 ? -1 : (now - s.lastSampleAt) / 1000;
        boolean stalled = age > Math.max(5, 3 * s.intervalForUi / 1000);
        wifiQuality.setText(quality + (stalled ? L.t("  ·  mesure bloquée", "  ·  survey stalled") : ""));
        int lossPct = s.samples == 0 ? 0 : Math.round(100f * s.lost / s.samples);
        wifiDetail.setText(String.format(Locale.US, "%s %02d:%02d:%02d   ·   %s\nBSSID %s\nPing %s   ·   %s %d %%\n%s",
                (sec % 2 == 0 ? "●" : "○"), sec / 3600, (sec / 60) % 60, sec % 60, nz(s.lastSsid), nz(s.lastBssid), nz(s.lastPing),
                L.t("pertes", "loss"), lossPct,
                L.t(s.samples + " mesures · " + s.roams + " changements de borne · " + s.disconnects + " coupures · " + s.marks + " repères",
                    s.samples + " samples · " + s.roams + " roams · " + s.disconnects + " drops · " + s.marks + " markers")));
        String events = s.recentText();
        if ("—".equals(s.lastPing) && nz(s.pingError).length() > 0)
            events = L.t("Ping vers ", "Ping to ") + nz(s.target) + " : " + s.pingError + (events.length() > 0 ? "\n\n" + events : "");
        wifiEvents.setText(events);
    }

    private void askMark() {
        final SurveyService s = SurveyService.running;
        if (s == null) return;
        final EditText name = new EditText(this);
        name.setText(L.t("Repère ", "Marker ") + (s.marks + 1));
        name.setSelectAllOnFocus(true);
        dialog().setTitle(L.t("Poser un repère", "Add a marker"))
                .setMessage(L.t("Où êtes-vous ? (quai, rayon, bureau…)", "Where are you? (dock, aisle, office…)"))
                .setView(name)
                .setPositiveButton("OK", new android.content.DialogInterface.OnClickListener() {
                    @Override public void onClick(android.content.DialogInterface d, int w) {
                        String label = name.getText().toString().trim();
                        s.mark(label.length() > 0 ? label : L.t("Repère ", "Marker ") + (s.marks + 1));
                    }
                })
                .setNegativeButton(L.t("Annuler", "Cancel"), null)
                .show();
    }

    private void confirmStopSurvey() {
        dialog().setMessage(L.t("Arrêter la mesure ?", "Stop measuring?"))
                .setPositiveButton(L.t("Arrêter", "Stop"), new android.content.DialogInterface.OnClickListener() {
                    @Override public void onClick(android.content.DialogInterface d, int w) {
                        SurveyService s = SurveyService.running;
                        if (s != null) s.stopSurvey("user");
                    }
                })
                .setNegativeButton(L.t("Continuer", "Keep going"), null)
                .show();
    }

    // ------------------------------------------------------------------ Suivi terrain

    private View buildField() {
        LinearLayout p = page();
        fieldRunning = new LinearLayout(this);
        fieldRunning.setOrientation(LinearLayout.VERTICAL);
        LinearLayout c = card();
        fieldBig = text("—", 40, L.brandText, true);
        fieldBig.setGravity(Gravity.CENTER_HORIZONTAL);
        c.addView(fieldBig);
        fieldDetail = text("", 13, L.ink, false);
        fieldDetail.setPadding(0, dp(10), 0, 0);
        c.addView(fieldDetail);
        fieldRunning.addView(c);
        Button inc = actionButton(L.t("Signaler un incident", "Report an incident"));
        inc.setTextSize(TypedValue.COMPLEX_UNIT_SP, 17);
        inc.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { askIncident(); } });
        fieldRunning.addView(inc, lpButton(inc, 64, 16));
        Button stop = secondaryButton(L.t("Arrêter le suivi", "Stop monitoring"));
        stop.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { confirmStopRecorder(); } });
        fieldRunning.addView(stop, lpButton(stop, 48, 10));
        TextView tip = text(L.t("Utilisez le terminal normalement. Au moindre problème, appuyez sur « Signaler un incident » (aussi dans la notification).",
                "Use the device as usual. When a problem occurs, press \"Report an incident\" (also in the notification)."), 13, L.muted, false);
        tip.setPadding(0, dp(12), 0, 0);
        fieldRunning.addView(tip);
        fieldEvents = text("", 13, L.muted, false);
        fieldEvents.setPadding(0, dp(12), 0, 0);
        fieldRunning.addView(fieldEvents);
        p.addView(fieldRunning);
        fieldStopped = new LinearLayout(this);
        fieldStopped.setOrientation(LinearLayout.VERTICAL);
        fieldIdle = text("", 15, L.ink, false);
        fieldStopped.addView(fieldIdle);
        Button start = actionButton(L.t("Démarrer le suivi", "Start monitoring"));
        start.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { askRecorderTarget(); } });
        fieldStopped.addView(start, lpButton(start, 52, 16));
        p.addView(fieldStopped);
        return scroll(p);
    }

    private void startRecorder(Bundle extras) {
        Intent i = new Intent(this, RecorderService.class);
        if (extras != null) {
            i.putExtra(RecorderService.EXTRA_TARGET, extras.getString(RecorderService.EXTRA_TARGET, ""));
            i.putExtra(RecorderService.EXTRA_DURATION, parseInt(extras.get(RecorderService.EXTRA_DURATION), 0));
            i.putExtra(RecorderService.EXTRA_INTERVAL, parseInt(extras.get(RecorderService.EXTRA_INTERVAL), 15));
        }
        recorderStartRequestedAt = System.currentTimeMillis();
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);
        show(S_FIELD);
    }

    private void refreshRecorder() {
        if (fieldRunning == null) return;
        RecorderService s = RecorderService.running;
        boolean starting = s == null && System.currentTimeMillis() - recorderStartRequestedAt < 15000;
        fieldRunning.setVisibility(s != null || starting ? View.VISIBLE : View.GONE);
        fieldStopped.setVisibility(s != null || starting ? View.GONE : View.VISIBLE);
        if (starting) { fieldBig.setText("…"); fieldDetail.setText(L.t("Démarrage du suivi…", "Starting…")); return; }
        if (s == null) {
            fieldIdle.setText(recorderStartRequestedAt > 0
                    ? L.t("Suivi terminé. Rebranchez le terminal au PC et récupérez-le dans ADB Tools (Analyse de fichiers ▸ Suivi terrain).",
                          "Monitoring finished. Plug the device back into the PC and retrieve it in ADB Tools (File analysis ▸ Field monitoring).")
                    : L.t("Le suivi terrain confie le terminal à un utilisateur pendant plusieurs heures : il note le journal Android, la batterie, "
                          + "le Wi-Fi, les coupures et les incidents signalés d'un bouton. Au retour, ADB Tools le récupère et l'ouvre sur les incidents.",
                          "Field monitoring hands the device to a user for several hours: it records the Android log, battery, Wi-Fi, drops "
                          + "and incidents reported with one button. Back at the PC, ADB Tools retrieves it and opens it on the incidents."));
            return;
        }
        long sec = (System.currentTimeMillis() - s.startedAt) / 1000;
        fieldBig.setText(String.format(Locale.US, "%02d:%02d:%02d", sec / 3600, (sec / 60) % 60, sec % 60));
        StringBuilder d = new StringBuilder();
        d.append(sec % 2 == 0 ? "● " : "○ ").append(L.t("Suivi en cours", "Monitoring"));
        if (s.endAt > 0) {
            long left = Math.max(0, (s.endAt - System.currentTimeMillis()) / 60000);
            d.append(L.t(" · fin dans ", " · ends in ")).append(left / 60).append(" h ").append(String.format(Locale.US, "%02d", left % 60));
        }
        d.append("\n").append(L.t(s.incidents + " incident(s) signalé(s)", s.incidents + " incident(s) reported"));
        d.append("\n").append(String.format(Locale.US, "%,d", s.logLines)).append(L.t(" lignes de journal · ", " log lines · ")).append(s.logBytes / (1024 * 1024)).append(" Mo");
        if (!s.logsReadable) d.append("\n").append(L.t("Journal système non accessible (droit non accordé) : seuls les relevés sont enregistrés.",
                "System log not readable (permission not granted): only readings are recorded."));
        if (nz(s.target).length() > 0) d.append("\n").append(L.t("Appli suivie : ", "Monitored app: ")).append(s.target);
        if (nz(s.lastBattery).length() > 0) d.append("\n").append(L.t("Batterie : ", "Battery: ")).append(s.lastBattery);
        if (nz(s.lastWifi).length() > 0) d.append("\nWi-Fi : ").append(s.lastWifi);
        fieldDetail.setText(d.toString());
        fieldEvents.setText(s.recentText());
    }

    /** Suivi lancé depuis l'appli : appli métier à suivre (facultatif), puis durée. */
    private void askRecorderTarget() {
        final List<String[]> apps = new ArrayList<String[]>();
        try {
            android.content.pm.PackageManager pm = getPackageManager();
            Intent main = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
            HashSet<String> seen = new HashSet<String>();
            for (android.content.pm.ResolveInfo ri : pm.queryIntentActivities(main, 0)) {
                String pkg = ri.activityInfo.packageName;
                if (pkg.equals(getPackageName()) || !seen.add(pkg)) continue;
                apps.add(new String[] { String.valueOf(ri.loadLabel(pm)), pkg });
            }
            java.util.Collections.sort(apps, new java.util.Comparator<String[]>() {
                @Override public int compare(String[] a, String[] b) { return a[0].compareToIgnoreCase(b[0]); }
            });
        } catch (Exception ignored) { }
        final String[] labels = new String[apps.size() + 1];
        labels[0] = L.t("Aucune appli en particulier", "No specific app");
        for (int i = 0; i < apps.size(); i++) labels[i + 1] = apps.get(i)[0];
        dialog().setTitle(L.t("Appli métier à suivre", "Business app to monitor"))
                .setItems(labels, new android.content.DialogInterface.OnClickListener() {
                    @Override public void onClick(android.content.DialogInterface d, int which) {
                        askRecorderDuration(which == 0 ? "" : apps.get(which - 1)[1]);
                    }
                })
                .setNegativeButton(L.t("Annuler", "Cancel"), null)
                .show();
    }

    private void askRecorderDuration(final String target) {
        final int[] minutes = { 0, 60, 120, 240, 480 };
        String[] labels = { L.t("Sans limite (arrêt manuel)", "No limit (stop manually)"), "1 h", "2 h", "4 h", "8 h" };
        dialog().setTitle(L.t("Durée du suivi", "Monitoring duration"))
                .setItems(labels, new android.content.DialogInterface.OnClickListener() {
                    @Override public void onClick(android.content.DialogInterface d, int which) {
                        Bundle b = new Bundle();
                        b.putString(RecorderService.EXTRA_TARGET, target);
                        b.putInt(RecorderService.EXTRA_DURATION, minutes[which]);
                        b.putInt(RecorderService.EXTRA_INTERVAL, 15);
                        startRecorder(b);
                    }
                })
                .setNegativeButton(L.t("Annuler", "Cancel"), null)
                .show();
    }

    /** Incident : motif rapide en un geste, précision facultative. */
    private void askIncident() {
        final RecorderService s = RecorderService.running;
        if (s == null) return;
        final EditText note = new EditText(this);
        note.setHint(L.t("Précisez si besoin", "Details (optional)"));
        note.setSingleLine(false);
        // Motif enregistré en français (lu par ADB Tools dans le rapport), affiché dans la langue de l'appli.
        final String[][] reasons = {
                { "Le lecteur ne lit pas", "Scanner not reading" }, { "Application bloquée ou lente", "App frozen or slow" },
                { "Application fermée toute seule", "App closed by itself" }, { "Wi-Fi ou réseau coupé", "Wi-Fi or network down" },
                { "Terminal lent ou redémarré", "Device slow or restarted" }, { "Autre", "Other" } };
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(16), dp(4), dp(16), 0);
        final AlertDialog[] dlg = new AlertDialog[1];
        for (final String[] r : reasons) {
            Button b = secondaryButton(L.t(r[0], r[1]));
            b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
            b.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) {
                    String extra = note.getText().toString().trim();
                    s.incident(r[0] + (extra.length() > 0 ? " — " + extra : ""));
                    if (dlg[0] != null) dlg[0].dismiss();
                    Toast.makeText(MainActivity.this, L.t("Incident noté", "Incident recorded"), Toast.LENGTH_SHORT).show();
                }
            });
            b.setMinHeight(dp(48));
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
            lp.topMargin = dp(6);
            box.addView(b, lp);
        }
        LinearLayout.LayoutParams nl = new LinearLayout.LayoutParams(-1, -2);
        nl.topMargin = dp(10);
        box.addView(note, nl);
        ScrollView sv = new ScrollView(this);
        sv.addView(box);
        dlg[0] = dialog().setTitle(L.t("Que se passe-t-il ?", "What happened?"))
                .setView(sv)
                .setNegativeButton(L.t("Annuler", "Cancel"), null)
                .show();
    }

    private void confirmStopRecorder() {
        dialog().setMessage(L.t("Arrêter le suivi terrain ?", "Stop field monitoring?"))
                .setPositiveButton(L.t("Arrêter", "Stop"), new android.content.DialogInterface.OnClickListener() {
                    @Override public void onClick(android.content.DialogInterface d, int w) {
                        RecorderService s = RecorderService.running;
                        if (s != null) s.stopRecording("user");
                    }
                })
                .setNegativeButton(L.t("Continuer", "Keep going"), null)
                .show();
    }

    // ------------------------------------------------------------------ Résultats

    private View buildResults() {
        LinearLayout p = page();
        TextView intro = text(L.t("Résultats gardés sur le terminal. Appuyez sur un test pour le refaire ou changer son résultat.",
                "Results kept on the device. Tap a test to run it again or change its result."), 13, L.muted, false);
        intro.setPadding(0, 0, 0, dp(12));
        p.addView(intro);
        resultsList = new LinearLayout(this);
        resultsList.setOrientation(LinearLayout.VERTICAL);
        p.addView(resultsList);
        Button send = actionButton(L.t("Envoyer au PC", "Send to the PC"));
        send.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) {
                Out.export();
                dialog().setTitle(L.t("Envoyer au PC", "Send to the PC"))
                        .setMessage(L.t("Si le terminal est branché au PC avec ADB Tools ouvert, la fenêtre de récupération s'ouvre sur le PC.\n\n"
                                        + "Sinon, branchez-le et appuyez de nouveau, ou utilisez « Récupérer du terminal » dans Diagnostics SAV ▸ Tests du terminal.",
                                "If the device is plugged into the PC with ADB Tools open, the retrieval window opens on the PC.\n\n"
                                        + "Otherwise, plug it in and tap again, or use \"Retrieve from the device\" in SAV Diagnostics ▸ Device tests."))
                        .setPositiveButton("OK", null).show();
            }
        });
        p.addView(send, lpButton(send, 52, 16));
        return scroll(p);
    }

    // ------------------------------------------------------------------ Tâches de fond

    private void refreshBackground() {
        if (bgBar == null) return;
        SurveyService sv = SurveyService.running;
        RecorderService rc = RecorderService.running;
        String text = null;
        bgTarget = null;
        if (sv != null && !S_WIFI.equals(screen)) {
            long sec = (System.currentTimeMillis() - sv.startedAt) / 1000;
            text = String.format(Locale.US, "%s · %02d:%02d · %s", L.t("Mesure Wi-Fi en fond", "Wi-Fi survey running"), sec / 60, sec % 60,
                    sv.lastRssi != 0 ? sv.lastRssi + " dBm" : "—");
            bgTarget = S_WIFI;
        }
        if (rc != null && !S_FIELD.equals(screen)) {
            long sec = (System.currentTimeMillis() - rc.startedAt) / 1000;
            String t = String.format(Locale.US, "%s · %02d:%02d", L.t("Suivi terrain en cours", "Field monitoring running"), sec / 3600, (sec / 60) % 60);
            text = text == null ? t : text + "\n" + t;
            if (bgTarget == null) bgTarget = S_FIELD;
        }
        bgBar.setVisibility(text == null ? View.GONE : View.VISIBLE);
        if (text != null) bgText.setText(text);
    }

    // ------------------------------------------------------------------ Outils d'interface

    private LinearLayout page() {
        LinearLayout p = new LinearLayout(this);
        p.setOrientation(LinearLayout.VERTICAL);
        p.setPadding(dp(16), dp(16), dp(16), dp(16));
        return p;
    }

    private ScrollView scroll(View content) {
        ScrollView s = new ScrollView(this);
        s.setFillViewport(true);
        s.addView(content);
        return s;
    }

    private LinearLayout card() {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        c.setPadding(dp(16), dp(14), dp(16), dp(14));
        c.setBackground(rounded(L.surface, L.line, 12));
        return c;
    }

    private LinearLayout.LayoutParams lpMatch(int topDp) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
        lp.topMargin = dp(topDp);
        return lp;
    }

    /** Bouton pleine largeur : hauteur minimale, grandit si le texte passe sur deux lignes (petits écrans). */
    private LinearLayout.LayoutParams lpButton(Button b, int hDp, int topDp) {
        b.setMinHeight(dp(hDp));
        b.setMinimumHeight(dp(hDp));
        b.setPadding(dp(14), dp(8), dp(14), dp(8));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
        lp.topMargin = dp(topDp);
        return lp;
    }

    private LinearLayout.LayoutParams lpHeight(int hDp, int topDp) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, dp(hDp));
        lp.topMargin = dp(topDp);
        return lp;
    }

    private Drawable iconDrawable(String name) {
        int id = getResources().getIdentifier(name, "drawable", getPackageName());
        if (id == 0) return new ColorDrawable(Color.TRANSPARENT);
        return Build.VERSION.SDK_INT >= 21 ? getResources().getDrawable(id, getTheme()) : getResources().getDrawable(id);
    }

    private ImageView icon(String name, int sizeDp, int color) {
        ImageView v = new ImageView(this);
        v.setImageDrawable(iconDrawable(name));
        v.setColorFilter(color, PorterDuff.Mode.SRC_IN);
        v.setScaleType(ImageView.ScaleType.FIT_CENTER);
        return v;
    }

    /** Orange : action principale. */
    private Button actionButton(String label) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 16);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setTextColor(L.onAction);
        b.setBackground(selector(L.action, 0, L.action, 10));
        if (Build.VERSION.SDK_INT >= 21) b.setStateListAnimator(null);
        return b;
    }

    /** Bouton secondaire : contour fin. */
    private Button secondaryButton(String label) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        b.setTextColor(L.ink);
        b.setPadding(dp(14), 0, dp(14), 0);
        b.setBackground(selector(Color.TRANSPARENT, L.line, L.surface2, 10));
        if (Build.VERSION.SDK_INT >= 21) b.setStateListAnimator(null);
        return b;
    }

    /** Fond avec états : appuyé (pressedFill) et sélection au clavier (contour orange). */
    private StateListDrawable selector(int fill, int stroke, int pressedFill, int radiusDp) {
        StateListDrawable s = new StateListDrawable();
        GradientDrawable focused = rounded(fill, 0, radiusDp);
        focused.setStroke(dp(2), L.action);
        s.addState(new int[] { android.R.attr.state_focused }, focused);
        s.addState(new int[] { android.R.attr.state_pressed }, rounded(pressedFill, stroke, radiusDp));
        s.addState(new int[0], rounded(fill, stroke, radiusDp));
        return s;
    }

    static GradientDrawable rounded(int fill, int stroke, int radiusDp, Context c) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(fill);
        float r = radiusDp * c.getResources().getDisplayMetrics().density;
        g.setCornerRadius(r);
        if (stroke != 0) g.setStroke(Math.max(1, Math.round(c.getResources().getDisplayMetrics().density)), stroke);
        return g;
    }

    private GradientDrawable rounded(int fill, int stroke, int radiusDp) { return rounded(fill, stroke, radiusDp, this); }

    private TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private static String nz(String s) { return s == null ? "" : s; }

    private int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics());
    }
}

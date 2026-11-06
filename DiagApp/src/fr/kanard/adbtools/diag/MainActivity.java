package fr.kanard.adbtools.diag;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.TextureView;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.graphics.drawable.GradientDrawable;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Appli de diagnostic pilotée par ADB Tools. Chaque commande arrive par
 *   adb shell am start -n fr.kanard.adbtools.diag/.MainActivity -e cmd &lt;commande&gt;
 * (probe, scan, camera_back, camera_front, flash, speaker, earpiece, microphone, vibrator, sensors, nfc, gps,
 * bluetooth, keys, storage_speed, stop) et les résultats repartent par le journal Android (voir {@link Out}).
 * Une seule chose à la fois : une nouvelle commande arrête le test en cours.
 */
public class MainActivity extends Activity {

    private static final String ACTION_DATAWEDGE = "fr.kanard.adbtools.diag.DATAWEDGE";
    private static final String ACTION_HONEYWELL = "fr.kanard.adbtools.diag.HONEYWELL";
    private static final String DW_PROFILE = "ADBToolsDiag";
    private static final String HONEYWELL_SERVICE = "com.intermec.datacollectionservice";

    // Couleurs du thème sombre d'ADB Tools (Themes/Theme.Dark.xaml).
    static final int C_BG = Color.rgb(0x10, 0x13, 0x14);
    static final int C_SURFACE = Color.rgb(0x18, 0x1C, 0x1E);
    static final int C_SURFACE2 = Color.rgb(0x1F, 0x24, 0x27);
    static final int C_LINE = Color.rgb(0x2A, 0x30, 0x33);
    static final int C_INK = Color.rgb(0xE9, 0xEC, 0xEE);
    static final int C_MUTED = Color.rgb(0x9A, 0xA3, 0xA9);
    static final int C_BRAND = Color.rgb(0x1F, 0x74, 0x54);
    static final int C_BRAND_TEXT = Color.rgb(0x5F, 0xD3, 0xA1);
    static final int C_ACTION = Color.rgb(0xFF, 0x9A, 0x2E);   // orange « bec de canard » : action principale
    static final int C_ON_ACTION = Color.rgb(0x1B, 0x0F, 0x02);
    static final int C_TITLE = Color.rgb(0x07, 0x1E, 0x17);
    static final int C_TITLE_INK = Color.rgb(0xDC, 0xEB, 0xE3);
    static final int C_TITLE_MUTED = Color.rgb(0x86, 0xA8, 0x97);
    static final int C_OK_BG = Color.rgb(0x12, 0x33, 0x26), C_OK_FG = Color.rgb(0x73, 0xD9, 0xA9);
    static final int C_WARN_BG = Color.rgb(0x3A, 0x2E, 0x0C), C_WARN_FG = Color.rgb(0xF5, 0xD5, 0x7E);
    static final int C_BAD_BG = Color.rgb(0x3A, 0x17, 0x14), C_BAD_FG = Color.rgb(0xFF, 0x8A, 0x80);

    private final Handler handler = new Handler(Looper.getMainLooper());
    private ImageView logoView;
    private LinearLayout verdictRow;
    private String activeTestId;
    private String stepText = "";
    private LinearLayout card;
    private boolean wifiEntry;
    private TextView titleView, messageView, bigView, detailView;
    private TextureView preview;
    private EditText input;
    private DiagTest current;
    private boolean resumed;
    private String pendingCmd;

    // Mode « lecteur »
    private boolean scanMode;
    private int scanCount;
    private String lastIntentData;
    private long lastIntentAt;
    private boolean receiverRegistered;

    // Mesure Wi-Fi autonome
    private LinearLayout surveyButtons;
    private final Runnable surveyRefresh = new Runnable() { @Override public void run() { refreshSurvey(); } };

    // Test « clavier »
    private boolean keysMode;
    private final Map<String, Integer> keys = new LinkedHashMap<String, Integer>();

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

        FrameLayout frame = new FrameLayout(this);
        frame.setBackgroundColor(C_BG);
        preview = new TextureView(this);
        preview.setVisibility(View.GONE);
        frame.addView(preview, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        LinearLayout outer = new LinearLayout(this);
        outer.setOrientation(LinearLayout.VERTICAL);

        // Bandeau comme la barre de titre d'ADB Tools : logo, nom, mode en cours.
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setBackgroundColor(C_TITLE);
        header.setPadding(dp(16), dp(12), dp(16), dp(12));
        logoView = new ImageView(this);
        setLogo(false);
        header.addView(logoView, new LinearLayout.LayoutParams(dp(40), dp(40)));
        LinearLayout names = new LinearLayout(this);
        names.setOrientation(LinearLayout.VERTICAL);
        names.setPadding(dp(12), 0, 0, 0);
        names.addView(text("ADB Tools Pro", 18, C_TITLE_INK, true));
        titleView = text("Diagnostic", 13, C_TITLE_MUTED, false);
        names.addView(titleView);
        header.addView(names);
        outer.addView(header, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(20);
        root.setPadding(pad, pad, pad, pad);
        messageView = text("Piloté depuis ADB Tools sur le PC.\nControlled from ADB Tools on the PC.", 15, C_MUTED, false);
        messageView.setPadding(0, 0, 0, dp(16));

        // Carte des valeurs (fond de bloc, bord fin, coins arrondis) : valeur principale en vert de la charte.
        card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(16), dp(14), dp(16), dp(14));
        card.setBackground(rounded(C_SURFACE, C_LINE, 12));
        bigView = text("", 48, C_BRAND_TEXT, true);
        bigView.setGravity(Gravity.CENTER_HORIZONTAL);
        detailView = text("", 15, C_INK, false);
        detailView.setTypeface(Typeface.MONOSPACE);
        detailView.setPadding(0, dp(8), 0, 0);
        card.addView(bigView);
        card.addView(detailView);
        card.setVisibility(View.GONE);

        input = new EditText(this);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        input.setTextColor(C_INK);
        input.setHint("…");
        input.setHintTextColor(C_MUTED);
        if (Build.VERSION.SDK_INT >= 21) input.setShowSoftInputOnFocus(false);
        input.setVisibility(View.GONE);
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

        root.addView(messageView);
        root.addView(card, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        LinearLayout.LayoutParams inputLp = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        inputLp.topMargin = dp(12);
        root.addView(input, inputLp);

        surveyButtons = new LinearLayout(this);
        surveyButtons.setOrientation(LinearLayout.VERTICAL);
        surveyButtons.setVisibility(View.GONE);
        Button markBtn = button("📍 Poser un repère / Add a marker", true);
        markBtn.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { askMark(); } });
        Button stopBtn = button("⏹ Arrêter la mesure / Stop measuring", false);
        stopBtn.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { confirmStopSurvey(); } });
        LinearLayout.LayoutParams b1 = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(52));
        b1.topMargin = dp(16);
        LinearLayout.LayoutParams b2 = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(48));
        b2.topMargin = dp(10);
        surveyButtons.addView(markBtn, b1);
        surveyButtons.addView(stopBtn, b2);
        // Boutons en bas de l'écran, à portée de pouce (mesure Wi-Fi et résultat des tests).
        LinearLayout bottomBar = new LinearLayout(this);
        bottomBar.setOrientation(LinearLayout.VERTICAL);
        bottomBar.setBackgroundColor(C_BG);
        bottomBar.setPadding(dp(16), dp(4), dp(16), dp(16));
        bottomBar.addView(surveyButtons, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        // Résultat du test choisi directement sur le terminal (le PC le reçoit et passe au test suivant).
        verdictRow = new LinearLayout(this);
        verdictRow.setOrientation(LinearLayout.VERTICAL);
        verdictRow.setVisibility(View.GONE);
        TextView vLabel = text("Résultat du test / Test result", 13, C_MUTED, false);
        vLabel.setPadding(0, dp(8), 0, dp(6));
        verdictRow.addView(vLabel);
        LinearLayout vr1 = new LinearLayout(this);
        vr1.setOrientation(LinearLayout.HORIZONTAL);
        // Icônes seules (le libellé « À surveiller » prenait toute la place sur les petits écrans) ;
        // le libellé complet reste lu par TalkBack via la description du bouton.
        vr1.addView(iconVerdictButton("✅", "OK", "ok", C_OK_BG, C_OK_FG), verdictWeight());
        vr1.addView(iconVerdictButton("⚠️", "À surveiller / Monitor", "warn", C_WARN_BG, C_WARN_FG), verdictWeight());
        vr1.addView(iconVerdictButton("❌", "HS / Faulty", "bad", C_BAD_BG, C_BAD_FG), verdictWeight());
        verdictRow.addView(vr1);
        Button skip = verdictButton("⏭ Passer ce test / Skip", "skip", C_SURFACE2, C_MUTED);
        LinearLayout.LayoutParams sk = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(46));
        sk.topMargin = dp(8);
        verdictRow.addView(skip, sk);
        bottomBar.addView(verdictRow, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        android.widget.ScrollView scroll = new android.widget.ScrollView(this);
        scroll.addView(root, new android.widget.ScrollView.LayoutParams(android.widget.ScrollView.LayoutParams.MATCH_PARENT, android.widget.ScrollView.LayoutParams.WRAP_CONTENT));
        outer.addView(scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        outer.addView(bottomBar, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        frame.addView(outer, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));
        setContentView(frame);

        Intent launch = getIntent();
        wifiEntry = launch != null && launch.getComponent() != null && launch.getComponent().getClassName().endsWith(".WifiLauncher");

        Log.i(Out.TAG, "READY\t" + Build.MANUFACTURER + "\t" + Build.MODEL + "\t" + Build.VERSION.RELEASE);
        pendingCmd = getIntent() == null ? null : getIntent().getStringExtra("cmd");
        pendingExtras = getIntent() == null ? null : getIntent().getExtras();
    }

    private Bundle pendingExtras;

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        String cmd = intent == null ? null : intent.getStringExtra("cmd");
        if (cmd == null) return;
        pendingExtras = intent.getExtras();
        if (resumed) handle(cmd); else pendingCmd = cmd;
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        if (scanMode) enableScanOutputs();
        if (pendingCmd != null) { String c = pendingCmd; pendingCmd = null; handle(c); }
        else if (SurveyService.running != null) showSurvey();
        else if (wifiEntry) {
            setLogo(true);
            titleView.setText("Mesure Wi-Fi / Wi-Fi survey");
            setMessage("Aucune mesure en cours : démarrez-la depuis ADB Tools (Analyse Wi-Fi ▸ Mesure sans câble).\n"
                    + "No survey running: start it from ADB Tools (Wi-Fi Analysis ▸ Cable-free survey).");
        }
    }

    @Override
    protected void onPause() {
        resumed = false;
        // Matériel libéré dès que l'appli passe en arrière-plan (caméra, micro, GPS…).
        stopCurrent();
        if (scanMode) { flushKeyboardInput(); disableScanOutputs(); }
        super.onPause();
    }

    // ------------------------------------------------------------------ Commandes

    private void handle(String cmd) {
        stopCurrent();
        setScanMode(false);
        keysMode = false;
        hideSurvey();
        verdictRow.setVisibility(View.GONE);
        activeTestId = null;
        stepText = pendingExtras == null ? "" : pendingExtras.getString("step", "");
        bigView.setText("");
        detailView.setText("");
        resetCardStyle();
        card.setVisibility(View.GONE);
        switch (cmd) {
            case "survey":
                startSurvey(pendingExtras);
                return;
            case "survey_stop":
                if (SurveyService.running != null) SurveyService.running.stopSurvey("pc");
                else Out.result("survey_stop", "na", Out.obj("reason", "not_running"));
                setMessage("Prêt / Ready");
                return;
            case "probe":
                Out.features(Probe.run(this));
                setMessage("Prêt / Ready");
                return;
            case "stop":
            case "idle":
                setMessage("Prêt / Ready");
                return;
            case "scan":
                setScanMode(true);
                return;
            case "keys":
                startKeys();
                activeTestId = "keys";
                showVerdictRow();
                return;
        }
        DiagTest t = create(cmd);
        if (t == null) { Out.result(cmd, "na", Out.obj("reason", "unknown")); return; }
        current = t;
        activeTestId = t.id();
        Out.state(t.id(), "started");
        titleView.setText(stepText.length() > 0 ? "Test " + stepText : "Diagnostic");
        showTestCard(label(cmd));
        showVerdictRow();
        try {
            t.start();
        } catch (Exception e) {
            Out.result(t.id(), "bad", Out.obj("reason", "exception", "message", String.valueOf(e.getMessage())));
            testFinished(t);
        }
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
            default: return null;
        }
    }

    private static String label(String cmd) {
        switch (cmd) {
            case "camera_back": return "Caméra arrière / Back camera";
            case "camera_front": return "Caméra avant / Front camera";
            case "flash": return "Flash";
            case "speaker": return "Haut-parleur / Speaker";
            case "earpiece": return "Écouteur / Earpiece";
            case "microphone": return "Micro / Microphone";
            case "vibrator": return "Vibreur / Vibrator";
            case "sensors": return "Capteurs / Sensors";
            case "nfc": return "NFC";
            case "gps": return "GPS";
            case "bluetooth": return "Bluetooth";
            case "storage_speed": return "Mémoire / Storage";
            default: return cmd;
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
        titleView.setText("Diagnostic"); setLogo(false);
    }

    /** Appelé par un test terminé (sur le fil principal). */
    void testFinished(DiagTest t) {
        if (current != t) return;
        current = null;
        try { t.stop(); } catch (Exception ignored) { }
        Out.state(t.id(), "stopped");
        showPreview(false);
        titleView.setText("Diagnostic"); setLogo(false);
        setMessage("Choisissez le résultat en bas de l'écran, ou sur le PC.\nChoose the result at the bottom of the screen, or on the PC.");
        detailView.setText((stepText.length() > 0 ? "Test " + stepText + "   ·   " : "") + "✔ Terminé / Finished");
    }

    // ------------------------------------------------------------------ Aide aux tests

    void setMessage(final String text) {
        runLater(new Runnable() { @Override public void run() { messageView.setText(text); } }, 0);
    }

    void runLater(Runnable r, long delayMs) {
        if (delayMs <= 0 && Looper.myLooper() == Looper.getMainLooper()) r.run();
        else handler.postDelayed(r, Math.max(0, delayMs));
    }

    TextureView preview() { return preview; }

    void showPreview(final boolean on) {
        runLater(new Runnable() {
            @Override public void run() {
                preview.setVisibility(on ? View.VISIBLE : View.GONE);
                // Aperçu caméra : la carte du test s'efface pour laisser voir l'image.
                if (on) card.setVisibility(View.GONE);
                else if (activeTestId != null && !keysMode && !scanMode) card.setVisibility(View.VISIBLE);
            }
        }, 0);
    }

    // ------------------------------------------------------------------ Mesure Wi-Fi autonome

    private void startSurvey(Bundle extras) {
        Intent i = new Intent(this, SurveyService.class);
        if (extras != null) {
            i.putExtra(SurveyService.EXTRA_TARGET, extras.getString(SurveyService.EXTRA_TARGET, ""));
            i.putExtra(SurveyService.EXTRA_INTERVAL, parseInt(extras.get(SurveyService.EXTRA_INTERVAL), 1000));
            i.putExtra(SurveyService.EXTRA_DURATION, parseInt(extras.get(SurveyService.EXTRA_DURATION), 0));
        }
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);
        showSurvey();
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

    private void showSurvey() {
        keepVisibleWhenLocked(true); // l'écran de mesure reste consultable sans déverrouiller
        titleView.setText("Mesure Wi-Fi / Wi-Fi survey");
        setLogo(true);
        card.setVisibility(View.VISIBLE);
        setMessage("Vous pouvez débrancher le terminal et vous déplacer. Posez des repères aux endroits clés.\n"
                + "You can unplug the device and walk around. Add markers at key places.");
        surveyButtons.setVisibility(View.VISIBLE);
        handler.removeCallbacks(surveyRefresh);
        refreshSurvey();
    }

    private void hideSurvey() {
        handler.removeCallbacks(surveyRefresh);
        keepVisibleWhenLocked(false);
        surveyButtons.setVisibility(View.GONE);
    }

    private void refreshSurvey() {
        SurveyService s = SurveyService.running;
        if (s == null) {
            surveyButtons.setVisibility(View.GONE);
            if (titleView.getText().toString().startsWith("Mesure Wi-Fi")) {
                bigView.setText("");
                detailView.setText("Mesure terminée / Survey finished");
                setMessage("Rebranchez le terminal au PC et récupérez les mesures dans ADB Tools.\n"
                        + "Plug the device back into the PC and retrieve the data in ADB Tools.");
            }
            return;
        }
        long now = System.currentTimeMillis();
        long sec = (now - s.startedAt) / 1000;
        int rssi = s.lastRssi;
        bigView.setText(rssi != 0 ? rssi + " dBm" : "—");
        bigView.setTextColor(rssi == 0 ? C_MUTED : rssi >= -65 ? C_OK_FG : rssi >= -75 ? C_WARN_FG : C_BAD_FG);
        String quality = rssi == 0 ? "Pas de Wi-Fi / No Wi-Fi" : rssi >= -60 ? "Excellent" : rssi >= -67 ? "Bon / Good"
                : rssi >= -75 ? "Moyen / Fair" : "Faible / Weak";
        // Preuve que la mesure tourne : âge de la dernière mesure (alerte si elle n'avance plus).
        long age = s.lastSampleAt == 0 ? -1 : (now - s.lastSampleAt) / 1000;
        boolean stalled = age > Math.max(5, 3 * s.intervalForUi / 1000);
        String pulse = (sec % 2 == 0 ? "●" : "○") + (stalled ? "  ⚠ mesure bloquée / survey stalled" : "  Mesure en cours / Recording")
                + (age >= 0 ? " — " + age + " s" : "");
        int lossPct = s.samples == 0 ? 0 : Math.round(100f * s.lost / s.samples);
        String events = s.recentText();
        detailView.setText(String.format(java.util.Locale.US,
                "%s\n%s\n\n⏱ %02d:%02d:%02d   📶 %s\nBSSID %s\nPing %s   ·   pertes %d %%\n%d mesures · %d changements de borne · %d coupures · %d repères%s",
                pulse, quality, sec / 3600, (sec / 60) % 60, sec % 60, s.lastSsid, s.lastBssid, s.lastPing, lossPct,
                s.samples, s.roams, s.disconnects, s.marks, events.length() > 0 ? "\n\n" + events : ""));
        handler.postDelayed(surveyRefresh, 1000);
    }

    private void askMark() {
        final SurveyService s = SurveyService.running;
        if (s == null) return;
        final EditText name = new EditText(this);
        name.setText("Repère " + (s.marks + 1));
        name.setSelectAllOnFocus(true);
        new AlertDialog.Builder(this)
                .setTitle("📍 Repère / Marker")
                .setMessage("Où êtes-vous ? (quai, rayon, bureau…) / Where are you?")
                .setView(name)
                .setPositiveButton("OK", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        String label = name.getText().toString().trim();
                        s.mark(label.length() > 0 ? label : "Repère " + (s.marks + 1));
                    }
                })
                .setNegativeButton("✖", null)
                .show();
    }

    private void confirmStopSurvey() {
        new AlertDialog.Builder(this)
                .setTitle("⏹")
                .setMessage("Arrêter la mesure ? / Stop measuring?")
                .setPositiveButton("OK", new DialogInterface.OnClickListener() {
                    @Override public void onClick(DialogInterface d, int w) {
                        SurveyService s = SurveyService.running;
                        if (s != null) s.stopSurvey("user");
                    }
                })
                .setNegativeButton("✖", null)
                .show();
    }

    // ------------------------------------------------------------------ Clavier physique

    private void startKeys() {
        keysMode = true;
        keys.clear();
        titleView.setText(stepText.length() > 0 ? "Clavier / Keypad · " + stepText : "Clavier / Keypad");
        setMessage("Appuyez sur chaque touche du terminal / Press every key on the device");
        Out.state("keys", "started");
    }

    private void finishKeys() {
        keysMode = false;
        JSONArray list = new JSONArray();
        for (Map.Entry<String, Integer> e : keys.entrySet()) list.put(Out.obj("key", e.getKey(), "count", e.getValue()));
        Out.result("keys", keys.isEmpty() ? "warn" : "confirm", Out.obj("keys", list));
        Out.state("keys", "stopped");
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (keysMode) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                String name = KeyEvent.keyCodeToString(event.getKeyCode()).replace("KEYCODE_", "");
                Integer n = keys.get(name);
                keys.put(name, n == null ? 1 : n + 1);
                bigView.setText(name); card.setVisibility(View.VISIBLE);
                detailView.setText(keys.size() + " touche(s) / key(s)");
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
        return super.dispatchKeyEvent(event);
    }

    // ------------------------------------------------------------------ Lecteur code-barres

    private void setScanMode(boolean on) {
        if (scanMode == on) return;
        scanMode = on;
        if (on) {
            scanCount = 0;
            titleView.setText("Test du lecteur / Scanner test");
            setMessage("Scannez des codes-barres : ils s'affichent dans ADB Tools.\nScan barcodes: they are shown in ADB Tools.");
            bigView.setText("0");
            card.setVisibility(View.VISIBLE);
            input.setVisibility(View.VISIBLE);
            input.requestFocus();
            if (resumed) enableScanOutputs();
        } else {
            handler.removeCallbacks(flushKeyboard);
            input.setText("");
            input.setVisibility(View.GONE);
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
        bigView.setText(String.valueOf(scanCount));
        detailView.setText(data);
        Out.scan(source, symbology, data);
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

    // ------------------------------------------------------------------ Outils

    /** Logo du bandeau : ADB Tools Diag, ou ADB Tools Wi-Fi pendant la mesure sans câble. */
    private void setLogo(boolean wifi) {
        if (logoView == null) return;
        int id = getResources().getIdentifier(wifi ? "ic_launcher_wifi" : "ic_launcher", "mipmap", getPackageName());
        if (id != 0) logoView.setImageResource(id);
    }

    static GradientDrawable rounded(int fill, int stroke, int radiusDp, android.content.Context c) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(fill);
        float r = radiusDp * c.getResources().getDisplayMetrics().density;
        g.setCornerRadius(r);
        if (stroke != 0) g.setStroke(Math.max(1, Math.round(c.getResources().getDisplayMetrics().density)), stroke);
        return g;
    }

    private GradientDrawable rounded(int fill, int stroke, int radiusDp) { return rounded(fill, stroke, radiusDp, this); }

    private LinearLayout.LayoutParams weight() {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(50), 1f);
        lp.setMargins(dp(3), 0, dp(3), 0);
        return lp;
    }

    private Button verdictButton(String label, final String status, int bg, int fg) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setTextColor(fg);
        b.setBackground(rounded(bg, fg, 8));
        if (Build.VERSION.SDK_INT >= 21) b.setStateListAnimator(null);
        b.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { sendVerdict(status); } });
        return b;
    }

    private Button iconVerdictButton(String icon, String description, String status, int bg, int fg) {
        Button b = verdictButton(icon, status, bg, fg);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 26);
        b.setPadding(0, 0, 0, 0);
        b.setContentDescription(description);
        return b;
    }

    private LinearLayout.LayoutParams verdictWeight() {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(58), 1f);
        lp.setMargins(dp(4), 0, dp(4), 0);
        return lp;
    }

    private void showVerdictRow() { verdictRow.setVisibility(View.VISIBLE); }

    /** Test en cours bien visible : nom en grand, étape de l'enchaînement, état. */
    private void showTestCard(String name) {
        bigView.setText(name.replace(" / ", "\n"));
        bigView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 30);
        bigView.setTextColor(C_INK);
        detailView.setTypeface(Typeface.DEFAULT_BOLD);
        detailView.setTextColor(C_BRAND_TEXT);
        detailView.setText((stepText.length() > 0 ? "Test " + stepText + "   ·   " : "") + "⏳ En cours… / Running…");
        card.setBackground(rounded(C_SURFACE, C_BRAND, 12));
        card.setVisibility(View.VISIBLE);
    }

    /** Remet la carte dans son style « valeurs » (lecteur, mesure Wi-Fi, clavier). */
    private void resetCardStyle() {
        bigView.setTextSize(TypedValue.COMPLEX_UNIT_SP, 48);
        bigView.setTextColor(C_BRAND_TEXT);
        detailView.setTypeface(Typeface.MONOSPACE);
        detailView.setTextColor(C_INK);
        card.setBackground(rounded(C_SURFACE, C_LINE, 12));
    }

    /** Le technicien tranche sur le terminal : le test en cours s'arrête, le PC reçoit le résultat. */
    private void sendVerdict(String status) {
        String id = activeTestId;
        if (id == null) return;
        stopCurrent();
        Out.verdict(id, status);
        verdictRow.setVisibility(View.GONE);
        activeTestId = null;
        setMessage("skip".equals(status) ? "Test passé / Test skipped" : "Résultat envoyé à ADB Tools / Result sent to ADB Tools");
    }

    /** Bouton dans le style d'ADB Tools : orange pour l'action principale, sinon bloc sombre bordé. */
    private Button button(String label, boolean primary) {
        Button b = new Button(this);
        b.setText(label);
        b.setAllCaps(false);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        b.setTypeface(Typeface.DEFAULT_BOLD);
        b.setTextColor(primary ? C_ON_ACTION : C_INK);
        b.setBackground(rounded(primary ? C_ACTION : C_SURFACE2, primary ? 0 : C_LINE, 8));
        if (Build.VERSION.SDK_INT >= 21) b.setStateListAnimator(null);
        return b;
    }

    private TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private int dp(int v) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, getResources().getDisplayMetrics());
    }
}

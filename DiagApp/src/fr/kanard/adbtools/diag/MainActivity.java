package fr.kanard.adbtools.diag;

import android.app.Activity;
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
import android.widget.EditText;
import android.widget.FrameLayout;
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

    private final Handler handler = new Handler(Looper.getMainLooper());
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
        frame.setBackgroundColor(Color.rgb(0x0B, 0x31, 0x25));
        preview = new TextureView(this);
        preview.setVisibility(View.GONE);
        frame.addView(preview, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT));

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(20);
        root.setPadding(pad, pad, pad, pad);
        titleView = text("ADB Tools — Diagnostic", 22, Color.WHITE, true);
        messageView = text("Piloté depuis ADB Tools sur le PC.\nControlled from ADB Tools on the PC.", 15, Color.rgb(0xB8, 0xD3, 0xC6), false);
        messageView.setPadding(0, dp(6), 0, dp(20));
        bigView = text("", 56, Color.rgb(0xF2, 0x8C, 0x28), true);
        bigView.setGravity(Gravity.CENTER_HORIZONTAL);
        detailView = text("", 16, Color.WHITE, false);
        detailView.setTypeface(Typeface.MONOSPACE);
        detailView.setPadding(0, dp(16), 0, dp(12));

        input = new EditText(this);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        input.setTextColor(Color.WHITE);
        input.setHint("…");
        input.setHintTextColor(Color.rgb(0x6F, 0x8F, 0x80));
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

        root.addView(titleView);
        root.addView(messageView);
        root.addView(bigView);
        root.addView(detailView);
        root.addView(input, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        frame.addView(root, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT));
        setContentView(frame);

        Log.i(Out.TAG, "READY\t" + Build.MANUFACTURER + "\t" + Build.MODEL + "\t" + Build.VERSION.RELEASE);
        pendingCmd = getIntent() == null ? null : getIntent().getStringExtra("cmd");
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        String cmd = intent == null ? null : intent.getStringExtra("cmd");
        if (cmd == null) return;
        if (resumed) handle(cmd); else pendingCmd = cmd;
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        if (scanMode) enableScanOutputs();
        if (pendingCmd != null) { String c = pendingCmd; pendingCmd = null; handle(c); }
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
        bigView.setText("");
        detailView.setText("");
        switch (cmd) {
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
                return;
        }
        DiagTest t = create(cmd);
        if (t == null) { Out.result(cmd, "na", Out.obj("reason", "unknown")); return; }
        current = t;
        Out.state(t.id(), "started");
        titleView.setText(label(cmd));
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
        titleView.setText("ADB Tools — Diagnostic");
    }

    /** Appelé par un test terminé (sur le fil principal). */
    void testFinished(DiagTest t) {
        if (current != t) return;
        current = null;
        try { t.stop(); } catch (Exception ignored) { }
        Out.state(t.id(), "stopped");
        showPreview(false);
        titleView.setText("ADB Tools — Diagnostic");
        setMessage("Test terminé : voir ADB Tools / Test finished: see ADB Tools");
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
        runLater(new Runnable() { @Override public void run() { preview.setVisibility(on ? View.VISIBLE : View.GONE); } }, 0);
    }

    // ------------------------------------------------------------------ Clavier physique

    private void startKeys() {
        keysMode = true;
        keys.clear();
        titleView.setText("Clavier / Keypad");
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
                bigView.setText(name);
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
            titleView.setText("ADB Tools — Test du lecteur / Scanner test");
            setMessage("Scannez des codes-barres : ils s'affichent dans ADB Tools.\nScan barcodes: they are shown in ADB Tools.");
            bigView.setText("0");
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

package fr.kanard.adbtools.scan;

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
import android.util.Base64;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.WindowManager;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.nio.charset.StandardCharsets;
import java.util.ArrayList;

/**
 * Écran de test du lecteur code-barres pour ADB Tools.
 *
 * Trois façons de recevoir une lecture, pour couvrir toutes les marques :
 *  - mode clavier (« keyboard wedge ») : le lecteur tape le code dans le champ de saisie (toutes marques) ;
 *  - intent DataWedge (Zebra) : profil « ADBToolsScan » créé pour cette appli, avec la symbologie ;
 *  - intent Honeywell : lecteur « réclamé » par l'appli, avec l'identifiant de symbologie.
 *
 * Chaque lecture est écrite dans le journal Android :
 *   ADBToolsScan: SCAN \t heure (ms) \t source \t symbologie \t données en Base64
 * (Base64 pour garder intacts les caractères invisibles : tabulation, retour chariot, séparateur GS1…).
 */
public class MainActivity extends Activity {

    private static final String TAG = "ADBToolsScan";
    private static final String ACTION_DATAWEDGE = "fr.kanard.adbtools.scan.DATAWEDGE";
    private static final String ACTION_HONEYWELL = "fr.kanard.adbtools.scan.HONEYWELL";
    private static final String DW_PROFILE = "ADBToolsScan";
    private static final String HONEYWELL_SERVICE = "com.intermec.datacollectionservice";

    private final Handler handler = new Handler(Looper.getMainLooper());
    private EditText input;
    private TextView countView;
    private TextView lastView;
    private int count;
    private String lastIntentData;
    private long lastIntentAt;
    private boolean receiverRegistered;

    private final Runnable flushKeyboard = new Runnable() {
        @Override public void run() { flushKeyboardInput(); }
    };

    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            String action = intent.getAction();
            if (ACTION_DATAWEDGE.equals(action)) {
                String data = intent.getStringExtra("com.symbol.datawedge.data_string");
                String symbology = intent.getStringExtra("com.symbol.datawedge.label_type");
                onIntentScan(data, "datawedge", symbology);
            } else if (ACTION_HONEYWELL.equals(action)) {
                String data = intent.getStringExtra("data");
                String symbology = intent.getStringExtra("codeId");
                String aim = intent.getStringExtra("aimId");
                if ((symbology == null || symbology.length() == 0) && aim != null) symbology = aim;
                else if (aim != null && aim.length() > 0) symbology = symbology + " / " + aim;
                onIntentScan(data, "honeywell", symbology);
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.rgb(0x0B, 0x31, 0x25));
        int pad = dp(20);
        root.setPadding(pad, pad, pad, pad);

        TextView title = text("ADB Tools — Test du lecteur", 22, Color.WHITE, true);
        TextView hint = text("Scannez des codes-barres : chaque lecture s'affiche dans ADB Tools, sur le PC.\n"
                + "Scan barcodes: each read is shown in ADB Tools, on the PC.", 14, Color.rgb(0xB8, 0xD3, 0xC6), false);
        hint.setPadding(0, dp(6), 0, dp(24));
        countView = text("0", 64, Color.rgb(0xF2, 0x8C, 0x28), true);
        countView.setGravity(Gravity.CENTER_HORIZONTAL);
        TextView countLabel = text("lecture(s) / read(s)", 14, Color.rgb(0xB8, 0xD3, 0xC6), false);
        countLabel.setGravity(Gravity.CENTER_HORIZONTAL);
        lastView = text("", 18, Color.WHITE, false);
        lastView.setTypeface(Typeface.MONOSPACE);
        lastView.setPadding(0, dp(24), 0, dp(12));

        input = new EditText(this);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_MULTI_LINE | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        input.setTextColor(Color.WHITE);
        input.setHint("…");
        input.setHintTextColor(Color.rgb(0x6F, 0x8F, 0x80));
        input.setShowSoftInputOnFocus(false); // pas de clavier virtuel : seul le lecteur écrit ici
        input.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) { }
            @Override
            public void afterTextChanged(Editable s) {
                handler.removeCallbacks(flushKeyboard);
                if (s.length() == 0) return;
                // Fin de lecture : retour à la ligne, ou 250 ms sans nouveau caractère (lecteur réglé sans suffixe).
                if (s.toString().indexOf('\n') >= 0) flushKeyboardInput();
                else handler.postDelayed(flushKeyboard, 250);
            }
        });

        root.addView(title);
        root.addView(hint);
        root.addView(countView);
        root.addView(countLabel);
        root.addView(lastView);
        root.addView(input, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        setContentView(root);

        Log.i(TAG, "READY\t" + Build.MANUFACTURER + "\t" + Build.MODEL + "\t" + Build.VERSION.RELEASE);
    }

    @Override
    protected void onResume() {
        super.onResume();
        IntentFilter filter = new IntentFilter();
        filter.addAction(ACTION_DATAWEDGE);
        filter.addAction(ACTION_HONEYWELL);
        filter.addCategory(Intent.CATEGORY_DEFAULT);
        // Un intent sans catégorie correspond aussi à ce filtre (seules ses propres catégories doivent y figurer).
        registerReceiver(receiver, filter);
        receiverRegistered = true;
        configureDataWedge();
        claimHoneywellScanner();
        input.requestFocus();
        Log.i(TAG, "RESUME");
    }

    @Override
    protected void onPause() {
        handler.removeCallbacks(flushKeyboard);
        flushKeyboardInput();
        if (receiverRegistered) {
            try { unregisterReceiver(receiver); } catch (Exception ignored) { }
            receiverRegistered = false;
        }
        releaseHoneywellScanner();
        Log.i(TAG, "PAUSE");
        super.onPause();
    }

    // Une touche Entrée « physique » (lecteur réglé pour envoyer des événements de touche) termine aussi la lecture.
    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (event.getAction() == KeyEvent.ACTION_UP && (event.getKeyCode() == KeyEvent.KEYCODE_ENTER || event.getKeyCode() == KeyEvent.KEYCODE_NUMPAD_ENTER)) {
            handler.removeCallbacks(flushKeyboard);
            flushKeyboardInput();
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    // ------------------------------------------------------------------ Lectures

    private void flushKeyboardInput() {
        if (input == null) return;
        String text = input.getText().toString();
        if (text.length() == 0) return;
        input.setText("");
        for (String part : text.split("\n")) {
            if (part.length() == 0) continue;
            // Même lecture déjà reçue par intent : le mode clavier n'est qu'un doublon.
            if (lastIntentData != null && part.equals(lastIntentData) && System.currentTimeMillis() - lastIntentAt < 1500) continue;
            emit(part, "keyboard", "");
        }
    }

    private void onIntentScan(String data, String source, String symbology) {
        if (data == null) return;
        lastIntentData = data.replace("\n", "");
        lastIntentAt = System.currentTimeMillis();
        emit(data, source, symbology == null ? "" : symbology);
    }

    private void emit(String data, String source, String symbology) {
        count++;
        countView.setText(String.valueOf(count));
        lastView.setText(data);
        String b64 = Base64.encodeToString(data.getBytes(StandardCharsets.UTF_8), Base64.NO_WRAP);
        Log.i(TAG, "SCAN\t" + System.currentTimeMillis() + "\t" + source + "\t" + symbology.replace('\t', ' ') + "\t" + b64);
    }

    // ------------------------------------------------------------------ Zebra DataWedge

    /** Profil DataWedge « ADBToolsScan » associé à cette appli : sortie par intent (avec symbologie), sans clavier. */
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

            Bundle barcodeParams = new Bundle();
            barcodeParams.putString("scanner_selection", "auto");
            barcodeParams.putString("scanner_input_enabled", "true");
            plugins.add(plugin("BARCODE", barcodeParams, "false"));

            Bundle intentParams = new Bundle();
            intentParams.putString("intent_output_enabled", "true");
            intentParams.putString("intent_action", ACTION_DATAWEDGE);
            intentParams.putString("intent_category", Intent.CATEGORY_DEFAULT);
            intentParams.putString("intent_delivery", "2"); // 2 = diffusion (broadcast)
            plugins.add(plugin("INTENT", intentParams, "true"));

            Bundle keystrokeParams = new Bundle();
            keystrokeParams.putString("keystroke_output_enabled", "false");
            plugins.add(plugin("KEYSTROKE", keystrokeParams, "true"));

            profile.putParcelableArrayList("PLUGIN_CONFIG", plugins);

            Intent config = new Intent("com.symbol.datawedge.api.ACTION");
            config.putExtra("com.symbol.datawedge.api.SET_CONFIG", profile);
            sendBroadcast(config);

            // Le profil s'active quand l'appli est au premier plan ; on le demande aussi explicitement.
            handler.postDelayed(new Runnable() {
                @Override public void run() {
                    Intent sw = new Intent("com.symbol.datawedge.api.ACTION");
                    sw.putExtra("com.symbol.datawedge.api.SWITCH_TO_PROFILE", DW_PROFILE);
                    sendBroadcast(sw);
                }
            }, 400);
        } catch (Exception e) {
            Log.w(TAG, "DataWedge: " + e.getMessage());
        }
    }

    private static Bundle plugin(String name, Bundle params, String reset) {
        Bundle b = new Bundle();
        b.putString("PLUGIN_NAME", name);
        b.putString("RESET_CONFIG", reset);
        b.putBundle("PARAM_LIST", params);
        return b;
    }

    // ------------------------------------------------------------------ Honeywell

    private void claimHoneywellScanner() {
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
            Log.w(TAG, "Honeywell: " + e.getMessage());
        }
    }

    private void releaseHoneywellScanner() {
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

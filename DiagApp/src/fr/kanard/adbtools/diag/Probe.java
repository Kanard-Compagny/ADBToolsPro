package fr.kanard.adbtools.diag;

import android.content.Context;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.hardware.Sensor;
import android.hardware.SensorManager;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraManager;
import android.os.Build;
import android.os.Vibrator;

import org.json.JSONArray;
import org.json.JSONObject;

/** Inventaire du matériel présent : ADB Tools n'affiche que les tests possibles sur ce terminal. */
final class Probe {
    private Probe() { }

    static JSONObject run(Context c) {
        PackageManager pm = c.getPackageManager();
        JSONObject o = new JSONObject();
        Out.put(o, "manufacturer", Build.MANUFACTURER);
        Out.put(o, "model", Build.MODEL);
        Out.put(o, "android", Build.VERSION.RELEASE);
        Out.put(o, "sdk", Build.VERSION.SDK_INT);

        boolean back = false, front = false, flash = false;
        try {
            CameraManager cm = (CameraManager) c.getSystemService(Context.CAMERA_SERVICE);
            for (String id : cm.getCameraIdList()) {
                CameraCharacteristics ch = cm.getCameraCharacteristics(id);
                Integer facing = ch.get(CameraCharacteristics.LENS_FACING);
                Boolean f = ch.get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_BACK) back = true;
                if (facing != null && facing == CameraCharacteristics.LENS_FACING_FRONT) front = true;
                if (f != null && f) flash = true;
            }
        } catch (Exception ignored) { }
        Out.put(o, "camera_back", back);
        Out.put(o, "camera_front", front);
        Out.put(o, "flash", flash);

        Out.put(o, "microphone", pm.hasSystemFeature(PackageManager.FEATURE_MICROPHONE));
        Out.put(o, "speaker", Build.VERSION.SDK_INT < 23 || pm.hasSystemFeature(PackageManager.FEATURE_AUDIO_OUTPUT));
        Out.put(o, "earpiece", pm.hasSystemFeature(PackageManager.FEATURE_TELEPHONY));
        Vibrator v = (Vibrator) c.getSystemService(Context.VIBRATOR_SERVICE);
        Out.put(o, "vibrator", v != null && v.hasVibrator());
        Out.put(o, "nfc", pm.hasSystemFeature(PackageManager.FEATURE_NFC));
        Out.put(o, "gps", pm.hasSystemFeature(PackageManager.FEATURE_LOCATION_GPS));
        Out.put(o, "bluetooth", pm.hasSystemFeature(PackageManager.FEATURE_BLUETOOTH));

        JSONArray sensors = new JSONArray();
        SensorManager sm = (SensorManager) c.getSystemService(Context.SENSOR_SERVICE);
        if (sm != null) {
            for (int type : SensorsTest.TYPES)
                if (sm.getDefaultSensor(type) != null) sensors.put(SensorsTest.key(type));
        }
        Out.put(o, "sensors", sensors);

        Configuration cfg = c.getResources().getConfiguration();
        Out.put(o, "keyboard", cfg.keyboard == Configuration.KEYBOARD_QWERTY || cfg.keyboard == Configuration.KEYBOARD_12KEY);

        // Lecteur code-barres intégré : services connus des principales marques (information seulement).
        String scanner = "";
        String[][] known = {
                {"com.symbol.datawedge", "Zebra DataWedge"},
                {"com.intermec.datacollectionservice", "Honeywell"},
                {"com.honeywell.decode.DecodeService", "Honeywell"},
                {"com.datalogic.decodewedge", "Datalogic"},
                {"com.nlscan.scanner", "Newland"},
                {"com.urovo.scanner", "Urovo"},
                {"com.android.usettings", "Urovo"},
                {"com.cipherlab.barcodebase", "CipherLab"},
                {"com.unitech.scanner", "Unitech"},
                {"com.chainway.scanner", "Chainway"},
        };
        for (String[] k : known) {
            if (installed(pm, k[0])) { scanner = k[1]; break; }
        }
        Out.put(o, "scanner", scanner);
        Out.put(o, "storage", true);
        return o;
    }

    private static boolean installed(PackageManager pm, String pkg) {
        try { pm.getPackageInfo(pkg, 0); return true; } catch (Exception e) { return false; }
    }
}

package fr.kanard.adbtools.diag;

import android.content.Context;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;

import org.json.JSONObject;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Capteurs : pendant 20 s, le technicien bouge et tourne le terminal, couvre le haut de l'écran (proximité)
 * et le capteur de lumière. Chaque capteur présent est « ok » s'il a réagi, « warn » s'il a envoyé des
 * valeurs sans réagir (geste pas fait ?), « bad » s'il n'a rien envoyé du tout.
 */
final class SensorsTest implements DiagTest, SensorEventListener {
    static final int[] TYPES = {
            Sensor.TYPE_ACCELEROMETER, Sensor.TYPE_GYROSCOPE, Sensor.TYPE_MAGNETIC_FIELD,
            Sensor.TYPE_LIGHT, Sensor.TYPE_PROXIMITY, Sensor.TYPE_PRESSURE,
    };
    private static final int DURATION_MS = 20000;

    static String key(int type) {
        switch (type) {
            case Sensor.TYPE_ACCELEROMETER: return "accelerometer";
            case Sensor.TYPE_GYROSCOPE: return "gyroscope";
            case Sensor.TYPE_MAGNETIC_FIELD: return "compass";
            case Sensor.TYPE_LIGHT: return "light";
            case Sensor.TYPE_PROXIMITY: return "proximity";
            case Sensor.TYPE_PRESSURE: return "pressure";
            default: return "sensor" + type;
        }
    }

    private static final class Track {
        int events;
        float min = Float.MAX_VALUE, max = -Float.MAX_VALUE, last;
        void add(float v) { events++; last = v; if (v < min) min = v; if (v > max) max = v; }
        float range() { return events == 0 ? 0 : max - min; }
    }

    private final MainActivity a;
    private SensorManager sm;
    private final Map<Integer, Track> tracks = new LinkedHashMap<Integer, Track>();
    private long lastLive;
    private boolean done;

    SensorsTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "sensors"; }

    @Override
    public void start() {
        sm = (SensorManager) a.getSystemService(Context.SENSOR_SERVICE);
        for (int t : TYPES) {
            Sensor s = sm == null ? null : sm.getDefaultSensor(t);
            if (s == null) continue;
            tracks.put(t, new Track());
            sm.registerListener(this, s, SensorManager.SENSOR_DELAY_UI);
        }
        if (tracks.isEmpty()) { Out.result(id(), "na", Out.obj("reason", "absent")); a.testFinished(this); return; }
        a.setMessage(L.t("Bougez et tournez le terminal, couvrez le haut de l'écran et la lumière (20 s).", "Move and rotate the device, cover the top of the screen and the light (20 s)."));
        a.runLater(new Runnable() { @Override public void run() { finish(); } }, DURATION_MS);
    }

    @Override
    public void onSensorChanged(SensorEvent e) {
        Track t = tracks.get(e.sensor.getType());
        if (t == null) return;
        float v;
        if (e.values.length >= 3 && (e.sensor.getType() == Sensor.TYPE_ACCELEROMETER || e.sensor.getType() == Sensor.TYPE_GYROSCOPE
                || e.sensor.getType() == Sensor.TYPE_MAGNETIC_FIELD)) {
            if (e.sensor.getType() == Sensor.TYPE_GYROSCOPE) {
                v = (float) Math.sqrt(e.values[0] * e.values[0] + e.values[1] * e.values[1] + e.values[2] * e.values[2]);
            } else {
                // Accéléromètre et boussole : on suit l'axe X (varie quand on tourne le terminal).
                v = e.values[0];
            }
        } else {
            v = e.values[0];
        }
        t.add(v);
        long now = System.currentTimeMillis();
        if (now - lastLive > 400) {
            lastLive = now;
            JSONObject live = new JSONObject();
            for (Map.Entry<Integer, Track> en : tracks.entrySet())
                Out.put(live, key(en.getKey()), Out.obj("value", round(en.getValue().last), "reacted", reacted(en.getKey(), en.getValue())));
            Out.live(id(), live);
        }
    }

    @Override public void onAccuracyChanged(Sensor sensor, int accuracy) { }

    private static boolean reacted(int type, Track t) {
        switch (type) {
            case Sensor.TYPE_ACCELEROMETER: return t.range() > 3f;       // m/s²
            case Sensor.TYPE_GYROSCOPE: return t.max > 1f;               // rad/s
            case Sensor.TYPE_MAGNETIC_FIELD: return t.range() > 10f;     // µT
            case Sensor.TYPE_LIGHT: return t.range() > Math.max(10f, t.max * 0.5f); // lux
            case Sensor.TYPE_PROXIMITY: return t.range() > 0f;           // proche / loin
            case Sensor.TYPE_PRESSURE: return t.last > 300f && t.last < 1200f; // hPa plausible
            default: return t.events > 0;
        }
    }

    private static double round(float v) { return Math.round(v * 100) / 100.0; }

    private void finish() {
        if (done) return;
        done = true;
        if (sm != null) sm.unregisterListener(this);
        JSONObject detail = new JSONObject();
        boolean anyMissing = false, anyIdle = false;
        for (Map.Entry<Integer, Track> en : tracks.entrySet()) {
            Track t = en.getValue();
            String s = t.events == 0 ? "bad" : reacted(en.getKey(), t) ? "ok" : "warn";
            if (t.events == 0) anyMissing = true; else if (!"ok".equals(s)) anyIdle = true;
            Out.put(detail, key(en.getKey()), Out.obj("status", s, "min", round(t.min == Float.MAX_VALUE ? 0 : t.min),
                    "max", round(t.max == -Float.MAX_VALUE ? 0 : t.max), "events", t.events));
        }
        Out.result(id(), anyMissing ? "bad" : anyIdle ? "warn" : "ok", detail);
        a.testFinished(this);
    }

    @Override
    public void stop() {
        if (sm != null) sm.unregisterListener(this);
        done = true;
    }
}

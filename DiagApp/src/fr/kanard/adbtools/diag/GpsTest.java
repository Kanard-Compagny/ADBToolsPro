package fr.kanard.adbtools.diag;

import android.content.Context;
import android.location.GnssStatus;
import android.location.Location;
import android.location.LocationListener;
import android.location.LocationManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Looper;

/**
 * GPS : temps pour obtenir une position (2 min au plus), satellites vus et utilisés, meilleur signal.
 * En intérieur, l'absence de position est normale : les satellites vus suffisent à montrer que la puce répond.
 */
final class GpsTest implements DiagTest, LocationListener {
    private static final int TIMEOUT_MS = 120000;
    private final MainActivity a;
    private LocationManager lm;
    private Object gnssCallback;
    private long startedAt;
    private int visible, used;
    private float bestCn0;
    private volatile boolean done;

    GpsTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "gps"; }

    @Override
    @SuppressWarnings("MissingPermission")
    public void start() {
        lm = (LocationManager) a.getSystemService(Context.LOCATION_SERVICE);
        if (lm == null || lm.getProvider(LocationManager.GPS_PROVIDER) == null) {
            Out.result(id(), "na", Out.obj("reason", "absent")); a.testFinished(this); return;
        }
        if (!lm.isProviderEnabled(LocationManager.GPS_PROVIDER)) {
            Out.result(id(), "warn", Out.obj("reason", "disabled")); a.testFinished(this); return;
        }
        a.setMessage(L.t("Placez le terminal près d'une fenêtre ou dehors (2 min au plus).", "Place the device near a window or outside (2 min at most)."));
        startedAt = System.currentTimeMillis();
        try {
            if (Build.VERSION.SDK_INT >= 24) {
                GnssStatus.Callback cb = new GnssStatus.Callback() {
                    @Override
                    public void onSatelliteStatusChanged(GnssStatus status) {
                        int v = status.getSatelliteCount(), u = 0;
                        float best = 0;
                        for (int i = 0; i < v; i++) {
                            if (status.usedInFix(i)) u++;
                            best = Math.max(best, status.getCn0DbHz(i));
                        }
                        visible = v; used = u; bestCn0 = Math.max(bestCn0, best);
                        live();
                    }
                };
                gnssCallback = cb;
                lm.registerGnssStatusCallback(cb);
            }
            lm.requestLocationUpdates(LocationManager.GPS_PROVIDER, 1000, 0, this, Looper.getMainLooper());
        } catch (SecurityException e) {
            Out.result(id(), "bad", Out.obj("reason", "permission")); a.testFinished(this); return;
        }
        a.runLater(new Runnable() {
            @Override public void run() {
                if (done) return;
                done = true;
                Out.result(id(), visible > 0 ? "warn" : "bad", Out.obj("reason", "timeout", "satellites", visible, "best_cn0", Math.round(bestCn0)));
                a.testFinished(GpsTest.this);
            }
        }, TIMEOUT_MS);
    }

    private void live() {
        Out.live(id(), Out.obj("seconds", (System.currentTimeMillis() - startedAt) / 1000, "satellites", visible, "used", used, "best_cn0", Math.round(bestCn0)));
    }

    @Override
    public void onLocationChanged(Location loc) {
        if (done) return;
        done = true;
        double ttff = (System.currentTimeMillis() - startedAt) / 1000.0;
        Out.result(id(), "ok", Out.obj("ttff_s", Math.round(ttff * 10) / 10.0, "satellites", visible, "used", used,
                "best_cn0", Math.round(bestCn0), "accuracy_m", Math.round(loc.getAccuracy())));
        a.runLater(new Runnable() { @Override public void run() { a.testFinished(GpsTest.this); } }, 0);
    }

    @Override public void onStatusChanged(String provider, int status, Bundle extras) { }
    @Override public void onProviderEnabled(String provider) { }
    @Override public void onProviderDisabled(String provider) { }

    @Override
    public void stop() {
        done = true;
        try { if (lm != null) lm.removeUpdates(this); } catch (Exception ignored) { }
        try {
            if (lm != null && gnssCallback != null && Build.VERSION.SDK_INT >= 24)
                lm.unregisterGnssStatusCallback((GnssStatus.Callback) gnssCallback);
        } catch (Exception ignored) { }
    }
}

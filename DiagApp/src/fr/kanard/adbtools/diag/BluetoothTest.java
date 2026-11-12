package fr.kanard.adbtools.diag;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanResult;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;

import java.util.HashMap;
import java.util.Map;

/**
 * Bluetooth : recherche des appareils proches (classique + basse consommation) pendant 15 s ; nombre trouvé
 * et meilleur signal. Si le Bluetooth était éteint, il est allumé pour le test puis éteint à nouveau.
 */
@SuppressWarnings({"deprecation", "MissingPermission"})
final class BluetoothTest implements DiagTest {
    private static final int SCAN_MS = 15000;
    private final MainActivity a;
    private BluetoothAdapter adapter;
    private BluetoothLeScanner le;
    private boolean enabledByUs, receiverOn;
    private final Map<String, Integer> found = new HashMap<String, Integer>();
    private volatile boolean done;

    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        @Override
        public void onReceive(Context context, Intent intent) {
            if (BluetoothDevice.ACTION_FOUND.equals(intent.getAction())) {
                BluetoothDevice d = intent.getParcelableExtra(BluetoothDevice.EXTRA_DEVICE);
                short rssi = intent.getShortExtra(BluetoothDevice.EXTRA_RSSI, Short.MIN_VALUE);
                if (d != null) add(d.getAddress(), rssi);
            }
        }
    };

    private final ScanCallback leCallback = new ScanCallback() {
        @Override public void onScanResult(int callbackType, ScanResult r) {
            if (r != null && r.getDevice() != null) add(r.getDevice().getAddress(), r.getRssi());
        }
    };

    BluetoothTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "bluetooth"; }

    private synchronized void add(String address, int rssi) {
        Integer old = found.get(address);
        if (old == null || rssi > old) found.put(address, rssi);
        Out.live(id(), Out.obj("devices", found.size(), "best_rssi", best()));
    }

    private int best() {
        int b = Integer.MIN_VALUE;
        for (int v : found.values()) if (v > b && v != Short.MIN_VALUE) b = v;
        return b == Integer.MIN_VALUE ? 0 : b;
    }

    @Override
    public void start() {
        adapter = BluetoothAdapter.getDefaultAdapter();
        if (adapter == null) { Out.result(id(), "na", Out.obj("reason", "absent")); a.testFinished(this); return; }
        a.setMessage(L.t("Recherche des appareils Bluetooth proches (15 s)…", "Scanning nearby Bluetooth devices (15 s)…"));
        try {
            if (!adapter.isEnabled()) {
                enabledByUs = adapter.enable();
                waitEnabled(0);
            } else {
                scan();
            }
        } catch (SecurityException e) {
            Out.result(id(), "bad", Out.obj("reason", "permission")); a.testFinished(this);
        }
    }

    private void waitEnabled(final int tries) {
        if (done) return;
        if (adapter.isEnabled()) { scan(); return; }
        if (tries > 20) { Out.result(id(), "bad", Out.obj("reason", "enable")); a.testFinished(this); return; }
        a.runLater(new Runnable() { @Override public void run() { waitEnabled(tries + 1); } }, 500);
    }

    private void scan() {
        try {
            a.registerReceiver(receiver, new IntentFilter(BluetoothDevice.ACTION_FOUND));
            receiverOn = true;
            adapter.startDiscovery();
            le = adapter.getBluetoothLeScanner();
            if (le != null) le.startScan(leCallback);
        } catch (Exception e) {
            Out.result(id(), "bad", Out.obj("reason", "scan", "message", String.valueOf(e.getMessage())));
            a.testFinished(this);
            return;
        }
        a.runLater(new Runnable() {
            @Override public void run() {
                if (done) return;
                done = true;
                int n;
                synchronized (BluetoothTest.this) { n = found.size(); }
                Out.result(id(), n > 0 ? "ok" : "warn", Out.obj("devices", n, "best_rssi", best()));
                a.testFinished(BluetoothTest.this);
            }
        }, SCAN_MS);
    }

    @Override
    public void stop() {
        done = true;
        try { if (adapter != null) adapter.cancelDiscovery(); } catch (Exception ignored) { }
        try { if (le != null) le.stopScan(leCallback); } catch (Exception ignored) { }
        if (receiverOn) { try { a.unregisterReceiver(receiver); } catch (Exception ignored) { } receiverOn = false; }
        try { if (enabledByUs && adapter != null) adapter.disable(); } catch (Exception ignored) { }
    }
}

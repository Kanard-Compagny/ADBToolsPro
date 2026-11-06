package fr.kanard.adbtools.diag;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.net.ConnectivityManager;
import android.net.LinkProperties;
import android.net.Network;
import android.net.RouteInfo;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Mesure Wi-Fi autonome : le terminal est débranché du PC et emporté sur le site ; chaque seconde, le service
 * note le signal (RSSI), la borne (BSSID), le canal, le débit et un ping vers la cible (passerelle par défaut).
 * Changements de borne, coupures et repères posés par le technicien sont aussi notés. Tout est écrit dans
 * files/surveys/survey_AAAAMMJJ_HHMMSS.jsonl (une ligne JSON par mesure) ; ADB Tools récupère le fichier une
 * fois le terminal rebranché. Service au premier plan (notification) pour continuer écran éteint.
 */
public class SurveyService extends Service {

    static final String EXTRA_TARGET = "target";
    static final String EXTRA_INTERVAL = "interval_ms";
    static final String EXTRA_DURATION = "duration_min";
    private static final String CHANNEL = "survey";
    private static final Pattern PING_TIME = Pattern.compile("time[=<]([0-9.]+)\\s*ms");

    /** Instance en cours (une seule mesure à la fois), lue par l'écran pour l'affichage en direct. */
    static volatile SurveyService running;

    volatile int lastRssi = 0, samples, lost, roams, disconnects, marks;
    volatile String lastBssid = "", lastSsid = "", target = "", lastPing = "";
    volatile long startedAt, lastSampleAt;
    int intervalForUi = 1000;
    /** Derniers événements (affichés sur le terminal pendant la mesure). */
    final java.util.ArrayDeque<String> recent = new java.util.ArrayDeque<String>();
    String filePath = "";

    private Writer out;
    private Thread worker;
    private volatile boolean stop;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;
    private int intervalMs = 1000;
    private long endAt;

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (running != null && running != this) running.stopSurvey("restart");
        startForeground(1, notification("Démarrage… / Starting…"));
        if (worker != null) return START_NOT_STICKY;
        running = this;
        target = intent == null ? "" : nz(intent.getStringExtra(EXTRA_TARGET));
        intervalMs = intent == null ? 1000 : Math.max(500, intent.getIntExtra(EXTRA_INTERVAL, 1000));
        intervalForUi = intervalMs;
        int durationMin = intent == null ? 0 : intent.getIntExtra(EXTRA_DURATION, 0);
        startedAt = System.currentTimeMillis();
        endAt = durationMin > 0 ? startedAt + durationMin * 60000L : 0;

        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "adbtools:survey");
        wakeLock.acquire();
        WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        @SuppressWarnings("deprecation")
        int mode = Build.VERSION.SDK_INT >= 29 ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
        wifiLock = wm.createWifiLock(mode, "adbtools:survey");
        wifiLock.acquire();

        try {
            File dir = new File(getExternalFilesDir(null) != null ? getExternalFilesDir(null) : getFilesDir(), "surveys");
            //noinspection ResultOfMethodCallIgnored
            dir.mkdirs();
            File f = new File(dir, "survey_" + new SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US).format(new Date(startedAt)) + ".jsonl");
            filePath = f.getAbsolutePath();
            out = new OutputStreamWriter(new FileOutputStream(f), StandardCharsets.UTF_8);
        } catch (Exception e) {
            Out.result("survey", "bad", Out.obj("reason", "file", "message", String.valueOf(e.getMessage())));
            stopSelf();
            return START_NOT_STICKY;
        }

        if (target.length() == 0) target = gateway();
        write(Out.obj("type", "header", "t", startedAt, "manufacturer", Build.MANUFACTURER, "model", Build.MODEL,
                "android", Build.VERSION.RELEASE, "serial", serial(), "target", target, "interval_ms", intervalMs,
                "duration_min", durationMin, "ip", ip(), "gateway", gateway(), "dns", dns()));
        Out.result("survey", "ok", Out.obj("file", filePath, "target", target));

        worker = new Thread(new Runnable() { @Override public void run() { loop(); } }, "survey");
        worker.start();
        return START_NOT_STICKY;
    }

    private void loop() {
        WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        boolean wasConnected = true;
        while (!stop) {
            long t0 = System.currentTimeMillis();
            if (endAt > 0 && t0 >= endAt) { stopSurvey("duration"); break; }
            try {
                @SuppressWarnings("deprecation")
                WifiInfo info = wm.getConnectionInfo();
                String bssid = info == null ? null : info.getBSSID();
                boolean connected = info != null && info.getNetworkId() != -1 && bssid != null && !"02:00:00:00:00:00".equals(bssid);
                JSONObject s = Out.obj("type", "s", "t", t0, "conn", connected);
                if (connected) {
                    int rssi = info.getRssi();
                    String ssid = nz(info.getSSID()).replace("\"", "");
                    Out.put(s, "rssi", rssi);
                    Out.put(s, "bssid", bssid);
                    Out.put(s, "ssid", ssid);
                    Out.put(s, "freq", info.getFrequency());
                    Out.put(s, "link", info.getLinkSpeed());
                    if (lastBssid.length() > 0 && !lastBssid.equalsIgnoreCase(bssid)) {
                        roams++;
                        write(Out.obj("type", "roam", "t", t0, "from", lastBssid, "to", bssid, "rssi", rssi));
                        event("🔁 " + shortMac(lastBssid) + " → " + shortMac(bssid) + " (" + rssi + " dBm)");
                    }
                    if (!wasConnected) { write(Out.obj("type", "conn", "t", t0, "bssid", bssid)); event("✅ Reconnecté / Reconnected"); }
                    lastBssid = bssid; lastSsid = ssid; lastRssi = rssi;
                } else if (wasConnected) {
                    disconnects++;
                    write(Out.obj("type", "disc", "t", t0, "bssid", lastBssid));
                    event("❌ Coupure Wi-Fi / Wi-Fi drop");
                    buzz(); // le technicien le sent même terminal en poche
                }
                wasConnected = connected;

                Double ms = connected && target.length() > 0 ? ping(target) : null;
                if (ms != null) { Out.put(s, "ping", ms); lastPing = Math.round(ms) + " ms"; }
                else { Out.put(s, "ping", JSONObject.NULL); lost++; lastPing = "—"; }
                samples++;
                lastSampleAt = System.currentTimeMillis();
                write(s);
                if (samples % 5 == 0) updateNotification();
            } catch (Exception e) {
                write(Out.obj("type", "error", "t", t0, "message", String.valueOf(e.getMessage())));
            }
            long wait = intervalMs - (System.currentTimeMillis() - t0);
            if (wait > 0) try { Thread.sleep(wait); } catch (InterruptedException ignored) { }
        }
    }

    /** Ping ICMP (commande système ping, 1 s au plus), sinon test d'accessibilité Java. Null si perdu. */
    private static Double ping(String host) {
        Process p = null;
        try {
            p = Runtime.getRuntime().exec(new String[]{"ping", "-c", "1", "-W", "1", host});
            BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream()));
            String line;
            while ((line = r.readLine()) != null) {
                Matcher m = PING_TIME.matcher(line);
                if (m.find()) return Double.parseDouble(m.group(1));
            }
            return null;
        } catch (Exception e) {
            try {
                long t = System.nanoTime();
                return InetAddress.getByName(host).isReachable(1000) ? (System.nanoTime() - t) / 1e6 : null;
            } catch (Exception e2) { return null; }
        } finally {
            if (p != null) p.destroy();
        }
    }

    /** Repère posé par le technicien (« Quai 3 », « Bureau »…), avec le signal à cet instant. */
    void mark(String label) {
        marks++;
        write(Out.obj("type", "mark", "t", System.currentTimeMillis(), "label", label, "rssi", lastRssi, "bssid", lastBssid));
        event("📍 " + label + " (" + lastRssi + " dBm)");
    }

    void stopSurvey(String reason) {
        if (stop) return;
        stop = true;
        write(Out.obj("type", "end", "t", System.currentTimeMillis(), "reason", reason, "samples", samples));
        try { if (out != null) out.close(); } catch (Exception ignored) { }
        out = null;
        try { if (wakeLock != null && wakeLock.isHeld()) wakeLock.release(); } catch (Exception ignored) { }
        try { if (wifiLock != null && wifiLock.isHeld()) wifiLock.release(); } catch (Exception ignored) { }
        Out.result("survey_stop", "ok", Out.obj("file", filePath, "samples", samples, "reason", reason));
        if (running == this) running = null;
        stopForeground(true);
        stopSelf();
    }

    @Override
    public void onDestroy() {
        stopSurvey("destroyed");
        super.onDestroy();
    }

    private void event(String text) {
        String line = new SimpleDateFormat("HH:mm:ss", Locale.US).format(new Date()) + "  " + text;
        synchronized (recent) {
            recent.addFirst(line);
            while (recent.size() > 6) recent.removeLast();
        }
    }

    String recentText() {
        synchronized (recent) {
            StringBuilder sb = new StringBuilder();
            for (String l : recent) { if (sb.length() > 0) sb.append('\n'); sb.append(l); }
            return sb.toString();
        }
    }

    private static String shortMac(String mac) {
        return mac != null && mac.length() > 8 ? "…" + mac.substring(mac.length() - 8) : String.valueOf(mac);
    }

    @SuppressWarnings("deprecation")
    private void buzz() {
        try {
            android.os.Vibrator v = (android.os.Vibrator) getSystemService(Context.VIBRATOR_SERVICE);
            if (v == null || !v.hasVibrator()) return;
            long[] p = {0, 150, 120, 150};
            if (Build.VERSION.SDK_INT >= 26) v.vibrate(android.os.VibrationEffect.createWaveform(p, -1)); else v.vibrate(p, -1);
        } catch (Exception ignored) { }
    }

    /** Notification à jour (signal, ping, durée) : visible même écran verrouillé. */
    private void updateNotification() {
        try {
            long sec = (System.currentTimeMillis() - startedAt) / 1000;
            String text = String.format(Locale.US, "%s dBm · ping %s · %02d:%02d:%02d · %d mesures",
                    lastRssi != 0 ? String.valueOf(lastRssi) : "—", lastPing, sec / 3600, (sec / 60) % 60, sec % 60, samples);
            ((NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE)).notify(1, notification(text));
        } catch (Exception ignored) { }
    }

    private synchronized void write(JSONObject o) {
        if (out == null) return;
        try { out.write(o.toString()); out.write('\n'); out.flush(); } catch (Exception ignored) { }
    }

    @SuppressWarnings("deprecation")
    private Notification notification(String text) {
        Intent open = new Intent(this, MainActivity.class);
        int piFlags = Build.VERSION.SDK_INT >= 23 ? PendingIntent.FLAG_IMMUTABLE : 0;
        PendingIntent pi = PendingIntent.getActivity(this, 0, open, piFlags);
        Notification.Builder b;
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Mesure Wi-Fi", NotificationManager.IMPORTANCE_LOW));
            b = new Notification.Builder(this, CHANNEL);
        } else {
            b = new Notification.Builder(this);
        }
        int wifiIcon = getResources().getIdentifier("ic_launcher_wifi", "mipmap", getPackageName());
        if (wifiIcon != 0) b.setLargeIcon(android.graphics.BitmapFactory.decodeResource(getResources(), wifiIcon));
        if (Build.VERSION.SDK_INT >= 21) b.setColor(MainActivity.C_BRAND);
        return b.setContentTitle("ADB Tools — Mesure Wi-Fi en cours")
                .setContentText(text)
                .setSubText("Rebranchez au PC pour récupérer / Plug back to retrieve")
                .setSmallIcon(android.R.drawable.stat_sys_upload)
                .setContentIntent(pi)
                .setOngoing(true)
                .build();
    }

    // ------------------------------------------------------------------ Réseau

    private String gateway() {
        try {
            if (Build.VERSION.SDK_INT >= 23) {
                ConnectivityManager cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
                Network n = cm.getActiveNetwork();
                LinkProperties lp = n == null ? null : cm.getLinkProperties(n);
                if (lp != null) for (RouteInfo r : lp.getRoutes())
                    if (r.isDefaultRoute() && r.getGateway() != null) return r.getGateway().getHostAddress();
            }
            @SuppressWarnings("deprecation")
            int g = ((WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE)).getDhcpInfo().gateway;
            if (g != 0) return (g & 0xFF) + "." + ((g >> 8) & 0xFF) + "." + ((g >> 16) & 0xFF) + "." + ((g >> 24) & 0xFF);
        } catch (Exception ignored) { }
        return "";
    }

    private String ip() {
        try {
            if (Build.VERSION.SDK_INT >= 23) {
                ConnectivityManager cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
                Network n = cm.getActiveNetwork();
                LinkProperties lp = n == null ? null : cm.getLinkProperties(n);
                if (lp != null) for (android.net.LinkAddress a : lp.getLinkAddresses())
                    if (a.getAddress() instanceof java.net.Inet4Address) return a.getAddress().getHostAddress();
            }
        } catch (Exception ignored) { }
        return "";
    }

    private String dns() {
        try {
            if (Build.VERSION.SDK_INT >= 23) {
                ConnectivityManager cm = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
                Network n = cm.getActiveNetwork();
                LinkProperties lp = n == null ? null : cm.getLinkProperties(n);
                if (lp != null) {
                    StringBuilder sb = new StringBuilder();
                    for (InetAddress a : lp.getDnsServers()) { if (sb.length() > 0) sb.append(", "); sb.append(a.getHostAddress()); }
                    return sb.toString();
                }
            }
        } catch (Exception ignored) { }
        return "";
    }

    @SuppressWarnings("deprecation")
    private static String serial() {
        try { return Build.VERSION.SDK_INT >= 26 ? "" : Build.SERIAL; } catch (Exception e) { return ""; }
    }

    private static String nz(String s) { return s == null ? "" : s; }
}

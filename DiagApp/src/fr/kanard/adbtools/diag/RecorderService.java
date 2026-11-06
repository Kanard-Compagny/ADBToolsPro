package fr.kanard.adbtools.diag;

import android.app.ActivityManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Environment;
import android.os.IBinder;
import android.os.PowerManager;
import android.os.Process;
import android.os.StatFs;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.Arrays;
import java.util.Date;
import java.util.Locale;

/**
 * Enregistreur de terrain : le terminal est confié à l'utilisateur pendant plusieurs heures, débranché du PC.
 * Le service écrit dans files/recordings/rec_AAAAMMJJ_HHMMSS/ :
 *  - logcat_NNN.txt : le journal Android complet (main, system, crash, events), par tranches de 25 Mo, les plus
 *    anciennes supprimées au-delà de MAX_LOG_FILES (droit READ_LOGS accordé par ADB Tools : pm grant) ;
 *  - events.txt : au format logcat, l'état du terminal toutes les N secondes (batterie, température, mémoire,
 *    stockage, Wi-Fi), les coupures et reconnexions Wi-Fi, l'écran, la charge, et les incidents signalés par
 *    l'utilisateur (bouton « Incident », tag ADBTools-Incident) ;
 *  - snapshot_props.txt (propriétés du terminal) et snapshot_incident_NN.txt (état du système au moment de chaque
 *    incident : batterie, application au premier plan, mémoire de l'appli métier ; droit DUMP accordé par ADB Tools).
 * ADB Tools récupère le dossier une fois le terminal rebranché et l'ouvre dans « Analyse de fichiers ».
 */
public class RecorderService extends Service {

    static final String EXTRA_TARGET = "target";
    static final String EXTRA_DURATION = "duration_min";
    static final String EXTRA_INTERVAL = "interval_s";
    static final String ACTION_INCIDENT = "fr.kanard.adbtools.diag.INCIDENT";
    static final String TAG_REC = "ADBTools-Rec", TAG_WIFI = "ADBTools-Wifi", TAG_INCIDENT = "ADBTools-Incident";
    private static final String CHANNEL = "recorder";
    private static final long LOG_FILE_BYTES = 25L * 1024 * 1024;
    private static final int MAX_LOG_FILES = 16;

    /** Enregistrement en cours (un seul à la fois), lu par l'écran pour l'affichage en direct. */
    static volatile RecorderService running;

    volatile long startedAt, logBytes, endAt;
    volatile int incidents, snapshots, logLines, logFiles;
    volatile boolean logsReadable = true;
    volatile String target = "", dirPath = "", lastWifi = "", lastBattery = "";
    final java.util.ArrayDeque<String> recent = new java.util.ArrayDeque<String>();

    private File dir;
    private Writer events;
    private java.lang.Process logcat;
    private Thread logThread, snapThread;
    private volatile boolean stop;
    private PowerManager.WakeLock wakeLock;
    private int intervalS = 15;
    private boolean receiversOn;
    private final int myPid = Process.myPid();
    private final SimpleDateFormat logTime = new SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US);

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_INCIDENT.equals(intent.getAction())) {
            if (running == this) incident("");      // bouton « Incident » de la notification
            return START_NOT_STICKY;
        }
        if (running != null && running != this) running.stopRecording("restart");
        startForeground(2, notification("Démarrage… / Starting…"));
        if (snapThread != null) return START_NOT_STICKY;
        running = this;
        target = intent == null ? "" : nz(intent.getStringExtra(EXTRA_TARGET));
        intervalS = intent == null ? 15 : Math.max(5, intent.getIntExtra(EXTRA_INTERVAL, 15));
        int durationMin = intent == null ? 0 : intent.getIntExtra(EXTRA_DURATION, 0);
        startedAt = System.currentTimeMillis();
        endAt = durationMin > 0 ? startedAt + durationMin * 60000L : 0;

        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "adbtools:recorder");
        wakeLock.acquire();

        try {
            File base = new File(getExternalFilesDir(null) != null ? getExternalFilesDir(null) : getFilesDir(), "recordings");
            dir = new File(base, "rec_" + new SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US).format(new Date(startedAt)));
            //noinspection ResultOfMethodCallIgnored
            dir.mkdirs();
            dirPath = dir.getAbsolutePath();
            events = new OutputStreamWriter(new FileOutputStream(new File(dir, "events.txt")), StandardCharsets.UTF_8);
        } catch (Exception e) {
            Out.result("record", "bad", Out.obj("reason", "file", "message", String.valueOf(e.getMessage())));
            stopSelf();
            return START_NOT_STICKY;
        }

        log("I", TAG_REC, "Enregistrement démarré : " + Build.MANUFACTURER + " " + Build.MODEL + ", Android " + Build.VERSION.RELEASE
                + (target.length() > 0 ? ", application suivie " + target : "") + ", relevé toutes les " + intervalS + " s"
                + (durationMin > 0 ? ", durée " + durationMin + " min" : ""));
        new Thread(new Runnable() { @Override public void run() { dumpTo("snapshot_props.txt", "getprop"); } }, "rec-props").start();
        registerReceivers();

        logThread = new Thread(new Runnable() { @Override public void run() { logLoop(); } }, "rec-logcat");
        logThread.start();
        snapThread = new Thread(new Runnable() { @Override public void run() { snapLoop(); } }, "rec-snap");
        snapThread.start();
        Out.result("record", "ok", Out.obj("dir", dirPath, "target", target));
        Out.state("record", "started");
        return START_NOT_STICKY;
    }

    // ------------------------------------------------------------------ Journal Android

    private void logLoop() {
        int fileIndex = 0;
        while (!stop) {
            Writer w = null;
            try {
                logcat = Runtime.getRuntime().exec(new String[] { "logcat", "-v", "threadtime", "-b", "main", "-b", "system", "-b", "crash", "-b", "events" });
                BufferedReader r = new BufferedReader(new InputStreamReader(logcat.getInputStream(), StandardCharsets.UTF_8));
                File f = new File(dir, String.format(Locale.US, "logcat_%03d.txt", fileIndex));
                w = new OutputStreamWriter(new FileOutputStream(f), StandardCharsets.UTF_8);
                long fileBytes = 0;
                int foreign = 0, seen = 0;
                logFiles = fileIndex + 1;
                String line;
                while (!stop && (line = r.readLine()) != null) {
                    w.write(line); w.write('\n');
                    int n = line.length() + 1;
                    fileBytes += n; logBytes += n; logLines++;
                    // Droit READ_LOGS absent : on ne voit que nos propres lignes.
                    if (seen < 400) { seen++; if (!line.contains(" " + myPid + " ")) foreign++; if (seen == 400) logsReadable = foreign > 20; }
                    if (fileBytes >= LOG_FILE_BYTES) {
                        w.close();
                        fileIndex++;
                        purgeOldLogs();
                        f = new File(dir, String.format(Locale.US, "logcat_%03d.txt", fileIndex));
                        w = new OutputStreamWriter(new FileOutputStream(f), StandardCharsets.UTF_8);
                        fileBytes = 0;
                        logFiles = fileIndex + 1;
                    }
                }
            } catch (Exception e) {
                if (!stop) event("⚠ Journal : " + e.getMessage());
            } finally {
                try { if (w != null) w.close(); } catch (Exception ignored) { }
                try { if (logcat != null) logcat.destroy(); } catch (Exception ignored) { }
            }
            if (!stop) { fileIndex++; sleep(2000); } // logcat interrompu : on repart sur un nouveau fichier
        }
    }

    private void purgeOldLogs() {
        File[] files = dir.listFiles();
        if (files == null) return;
        java.util.List<File> logs = new java.util.ArrayList<File>();
        for (File f : files) if (f.getName().startsWith("logcat_")) logs.add(f);
        if (logs.size() <= MAX_LOG_FILES) return;
        File[] sorted = logs.toArray(new File[0]);
        Arrays.sort(sorted);
        for (int i = 0; i < sorted.length - MAX_LOG_FILES; i++) {
            //noinspection ResultOfMethodCallIgnored
            sorted[i].delete();
        }
    }

    // ------------------------------------------------------------------ Relevés périodiques

    private void snapLoop() {
        sleep(1500);
        while (!stop) {
            long t0 = System.currentTimeMillis();
            if (endAt > 0 && t0 >= endAt) { stopRecording("duration"); break; }
            try { snapshot(); } catch (Exception e) { event("⚠ " + e.getMessage()); }
            updateNotification();
            long wait = intervalS * 1000L - (System.currentTimeMillis() - t0);
            sleep(Math.max(1000, wait));
        }
    }

    private void snapshot() {
        snapshots++;
        StringBuilder sb = new StringBuilder();
        Intent bat = registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        if (bat != null) {
            int level = bat.getIntExtra(BatteryManager.EXTRA_LEVEL, -1), scale = bat.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
            int temp = bat.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0), plugged = bat.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0);
            int pct = scale > 0 && level >= 0 ? level * 100 / scale : -1;
            lastBattery = pct + " % · " + String.format(Locale.US, "%.1f", temp / 10.0) + " °C" + (plugged != 0 ? " · en charge" : "");
            sb.append("Batterie ").append(lastBattery);
        }
        ActivityManager am = (ActivityManager) getSystemService(Context.ACTIVITY_SERVICE);
        ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
        am.getMemoryInfo(mi);
        sb.append(" | Mémoire libre ").append(mi.availMem / (1024 * 1024)).append(" Mo / ").append(mi.totalMem / (1024 * 1024)).append(" Mo")
          .append(mi.lowMemory ? " (SATURÉE)" : "");
        try {
            StatFs fs = new StatFs(Environment.getDataDirectory().getPath());
            sb.append(" | Stockage libre ").append(fs.getAvailableBytes() / (1024 * 1024)).append(" Mo");
        } catch (Exception ignored) { }
        sb.append(" | ").append(wifiText());
        lastWifi = wifiText();
        log("I", TAG_REC, sb.toString());
    }

    @SuppressWarnings("deprecation")
    private String wifiText() {
        try {
            WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm == null || !wm.isWifiEnabled()) return "Wi-Fi désactivé";
            WifiInfo wi = wm.getConnectionInfo();
            if (wi == null || wi.getNetworkId() == -1 || wi.getBSSID() == null || "00:00:00:00:00:00".equals(wi.getBSSID())) return "Wi-Fi non connecté";
            String ssid = nz(wi.getSSID()).replace("\"", "");
            return "Wi-Fi " + wi.getRssi() + " dBm " + ssid + " " + wi.getBSSID() + " " + wi.getLinkSpeed() + " Mbps " + wi.getFrequency() + " MHz";
        } catch (Exception e) { return "Wi-Fi ?"; }
    }

    // ------------------------------------------------------------------ Événements système

    private final BroadcastReceiver receiver = new BroadcastReceiver() {
        private Boolean wifiUp;
        @Override
        public void onReceive(Context context, Intent intent) {
            String a = intent.getAction();
            if (a == null) return;
            if (WifiManager.NETWORK_STATE_CHANGED_ACTION.equals(a)) {
                android.net.NetworkInfo ni = intent.getParcelableExtra(WifiManager.EXTRA_NETWORK_INFO);
                boolean up = ni != null && ni.isConnected();
                if (wifiUp != null && wifiUp == up) return;
                wifiUp = up;
                if (up) { log("I", TAG_WIFI, "Wi-Fi connecté : " + wifiText()); event("📶 Wi-Fi connecté"); }
                else { log("W", TAG_WIFI, "Wi-Fi coupé"); event("❌ Wi-Fi coupé"); }
            } else if (Intent.ACTION_SCREEN_ON.equals(a)) log("I", TAG_REC, "Écran allumé");
            else if (Intent.ACTION_SCREEN_OFF.equals(a)) log("I", TAG_REC, "Écran éteint");
            else if (Intent.ACTION_POWER_CONNECTED.equals(a)) { log("I", TAG_REC, "Chargeur branché"); event("🔌 Chargeur branché"); }
            else if (Intent.ACTION_POWER_DISCONNECTED.equals(a)) { log("I", TAG_REC, "Chargeur débranché"); event("🔋 Chargeur débranché"); }
            else if (Intent.ACTION_BATTERY_LOW.equals(a)) { log("W", TAG_REC, "Batterie faible"); event("🪫 Batterie faible"); }
        }
    };

    private void registerReceivers() {
        IntentFilter f = new IntentFilter();
        f.addAction(WifiManager.NETWORK_STATE_CHANGED_ACTION);
        f.addAction(Intent.ACTION_SCREEN_ON);
        f.addAction(Intent.ACTION_SCREEN_OFF);
        f.addAction(Intent.ACTION_POWER_CONNECTED);
        f.addAction(Intent.ACTION_POWER_DISCONNECTED);
        f.addAction(Intent.ACTION_BATTERY_LOW);
        registerReceiver(receiver, f);
        receiversOn = true;
    }

    // ------------------------------------------------------------------ Incidents

    /** Incident signalé par l'utilisateur : noté dans le journal et état du système capturé à cet instant. */
    void incident(String note) {
        incidents++;
        final int n = incidents;
        String text = note == null || note.trim().length() == 0 ? "(sans précision)" : note.trim();
        log("W", TAG_INCIDENT, "Incident n°" + n + " signalé sur le terminal : " + text + " | " + lastWifi + " | Batterie " + lastBattery);
        event("⚠️ Incident n°" + n + " : " + text);
        Out.state("record_incident", String.valueOf(n));
        updateNotification();
        new Thread(new Runnable() {
            @Override public void run() {
                StringBuilder cmd = new StringBuilder();
                cmd.append("echo '=== Incident n°").append(n).append(" — ").append(new SimpleDateFormat("dd/MM/yyyy HH:mm:ss", Locale.FRANCE).format(new Date())).append("'; ");
                cmd.append("echo; echo '=== Application au premier plan'; dumpsys window 2>/dev/null | grep -E 'mCurrentFocus|mFocusedApp'; ");
                cmd.append("echo; echo '=== Batterie'; dumpsys battery 2>/dev/null; ");
                cmd.append("echo; echo '=== Mémoire'; cat /proc/meminfo 2>/dev/null | head -8; ");
                if (target.length() > 0) cmd.append("echo; echo '=== Mémoire de ").append(target).append("'; dumpsys meminfo ").append(target).append(" 2>/dev/null | head -60; ");
                cmd.append("echo; echo '=== Wi-Fi'; dumpsys wifi 2>/dev/null | grep -m 5 -E 'mWifiInfo|Supplicant state|mNetworkInfo'; ");
                cmd.append("echo; echo '=== Processus les plus gourmands'; top -b -n 1 -m 15 2>/dev/null || top -n 1 -m 15 2>/dev/null");
                dumpTo(String.format(Locale.US, "snapshot_incident_%02d.txt", n), cmd.toString());
            }
        }, "rec-incident").start();
    }

    /** Sortie d'une commande shell dans un fichier du dossier d'enregistrement. */
    private void dumpTo(String name, String shellCommand) {
        Writer w = null;
        try {
            java.lang.Process p = Runtime.getRuntime().exec(new String[] { "sh", "-c", shellCommand });
            BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream(), StandardCharsets.UTF_8));
            w = new OutputStreamWriter(new FileOutputStream(new File(dir, name)), StandardCharsets.UTF_8);
            String line; int count = 0;
            while ((line = r.readLine()) != null && count++ < 20000) { w.write(line); w.write('\n'); }
            p.destroy();
        } catch (Exception e) {
            event("⚠ " + name + " : " + e.getMessage());
        } finally {
            try { if (w != null) w.close(); } catch (Exception ignored) { }
        }
    }

    // ------------------------------------------------------------------ Arrêt

    void stopRecording(String reason) {
        if (stop) return;
        stop = true;
        log("I", TAG_REC, "Enregistrement arrêté (" + reason + ") : " + incidents + " incident(s), " + logLines + " lignes de journal");
        try { if (logcat != null) logcat.destroy(); } catch (Exception ignored) { }
        try { if (receiversOn) unregisterReceiver(receiver); } catch (Exception ignored) { }
        receiversOn = false;
        synchronized (this) {
            try { if (events != null) events.close(); } catch (Exception ignored) { }
            events = null;
        }
        try { if (wakeLock != null && wakeLock.isHeld()) wakeLock.release(); } catch (Exception ignored) { }
        Out.result("record_stop", "ok", Out.obj("dir", dirPath, "incidents", incidents, "reason", reason));
        if (running == this) running = null;
        stopForeground(true);
        stopSelf();
    }

    @Override
    public void onDestroy() {
        stopRecording("destroyed");
        super.onDestroy();
    }

    // ------------------------------------------------------------------ Outils

    /** Ligne au format logcat « threadtime » : ADB Tools la fusionne avec le reste du journal. */
    private void log(String level, String tag, String message) {
        String line = logTime.format(new Date()) + " " + String.format(Locale.US, "%5d %5d", myPid, myPid) + " " + level + " " + tag + ": " + message.replace('\n', ' ');
        synchronized (this) {
            try { if (events != null) { events.write(line); events.write('\n'); events.flush(); } } catch (Exception ignored) { }
        }
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

    private void updateNotification() {
        long sec = (System.currentTimeMillis() - startedAt) / 1000;
        String text = String.format(Locale.US, "⏺ %02d:%02d:%02d · %d incident(s) · %d Mo", sec / 3600, (sec / 60) % 60, sec % 60, incidents, logBytes / (1024 * 1024));
        try { ((NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE)).notify(2, notification(text)); } catch (Exception ignored) { }
    }

    @SuppressWarnings("deprecation")
    private Notification notification(String text) {
        Intent open = new Intent(this, MainActivity.class);
        int piFlags = Build.VERSION.SDK_INT >= 23 ? PendingIntent.FLAG_IMMUTABLE : 0;
        PendingIntent pi = PendingIntent.getActivity(this, 0, open, piFlags);
        PendingIntent inc = PendingIntent.getService(this, 1, new Intent(this, RecorderService.class).setAction(ACTION_INCIDENT), piFlags);
        Notification.Builder b;
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Enregistreur", NotificationManager.IMPORTANCE_LOW));
            b = new Notification.Builder(this, CHANNEL);
        } else b = new Notification.Builder(this);
        b.setContentTitle("ADB Tools — Enregistreur / Recorder")
         .setContentText(text)
         .setSmallIcon(android.R.drawable.ic_menu_save)
         .setContentIntent(pi)
         .setOngoing(true);
        if (Build.VERSION.SDK_INT >= 20)
            b.addAction(new Notification.Action.Builder(android.R.drawable.ic_dialog_alert, "⚠️ Incident", inc).build());
        return b.build();
    }

    private static void sleep(long ms) { try { Thread.sleep(ms); } catch (InterruptedException ignored) { } }
    private static String nz(String s) { return s == null ? "" : s; }
}

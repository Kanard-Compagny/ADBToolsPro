package fr.kanard.adbtools.diag;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;

/**
 * Vitesse de la mémoire interne : écriture de 64 Mo (synchronisée sur le support), puis relecture.
 * Une mémoire fatiguée ou presque pleine écrit nettement plus lentement.
 */
final class StorageTest implements DiagTest {
    private static final int MB = 1024 * 1024;
    private static final int SIZE_MB = 64;
    private final MainActivity a;
    private volatile boolean stopped;

    StorageTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "storage_speed"; }

    @Override
    public void start() {
        a.setMessage(L.t("Mesure de la vitesse de la mémoire…", "Measuring storage speed…"));
        new Thread(new Runnable() { @Override public void run() { measure(); } }, "storage").start();
    }

    private void measure() {
        File f = new File(a.getFilesDir(), "speedtest.bin");
        try {
            byte[] block = new byte[MB];
            for (int i = 0; i < block.length; i++) block[i] = (byte) (i * 31);
            long t0 = System.nanoTime();
            FileOutputStream out = new FileOutputStream(f);
            for (int i = 0; i < SIZE_MB && !stopped; i++) {
                out.write(block);
                if (i % 8 == 7) Out.live(id(), Out.obj("phase", "write", "mb", i + 1, "total", SIZE_MB));
            }
            out.getFD().sync();
            out.close();
            double writeSec = (System.nanoTime() - t0) / 1e9;
            if (stopped) return;

            long t1 = System.nanoTime();
            FileInputStream in = new FileInputStream(f);
            while (in.read(block) > 0) { }
            in.close();
            double readSec = (System.nanoTime() - t1) / 1e9;

            double write = SIZE_MB / Math.max(writeSec, 0.001), read = SIZE_MB / Math.max(readSec, 0.001);
            String status = write >= 15 ? "ok" : write >= 5 ? "warn" : "bad";
            Out.result(id(), status, Out.obj("write_mbs", Math.round(write * 10) / 10.0, "read_mbs", Math.round(read * 10) / 10.0, "size_mb", SIZE_MB));
        } catch (Exception e) {
            Out.result(id(), "bad", Out.obj("reason", "exception", "message", String.valueOf(e.getMessage())));
        } finally {
            //noinspection ResultOfMethodCallIgnored
            f.delete();
            a.runLater(new Runnable() { @Override public void run() { a.testFinished(StorageTest.this); } }, 0);
        }
    }

    @Override public void stop() { stopped = true; }
}

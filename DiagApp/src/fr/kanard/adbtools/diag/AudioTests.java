package fr.kanard.adbtools.diag;

import android.content.Context;
import android.media.AudioDeviceInfo;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.os.Build;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;

/** Tests audio : haut-parleur, écouteur (appel) et micro. */
final class AudioTests {
    private AudioTests() { }

    /** Son de test : bips à 1 kHz (3 × 0,5 s), généré par l'appli, volume réglé puis rétabli. */
    static final class Output implements DiagTest {
        private final MainActivity a;
        private final boolean earpiece;
        private AudioTrack track;
        private int savedVolume = -1, savedMode;
        private int stream;
        private volatile boolean stopped;

        Output(MainActivity a, boolean earpiece) { this.a = a; this.earpiece = earpiece; }

        @Override public String id() { return earpiece ? "earpiece" : "speaker"; }

        @Override
        @SuppressWarnings("deprecation")
        public void start() {
            AudioManager am = (AudioManager) a.getSystemService(Context.AUDIO_SERVICE);
            stream = earpiece ? AudioManager.STREAM_VOICE_CALL : AudioManager.STREAM_MUSIC;
            a.setMessage(earpiece ? L.t("Portez le terminal à l'oreille : vous devez entendre 3 bips.", "Hold the device to your ear: you should hear 3 beeps.")
                                  : L.t("Vous devez entendre 3 bips.", "You should hear 3 beeps."));
            try {
                savedMode = am.getMode();
                savedVolume = am.getStreamVolume(stream);
                int max = am.getStreamMaxVolume(stream);
                am.setStreamVolume(stream, Math.max(1, (int) Math.round(max * 0.8)), 0);
                if (earpiece) {
                    am.setMode(AudioManager.MODE_IN_COMMUNICATION);
                    am.setSpeakerphoneOn(false);
                    if (Build.VERSION.SDK_INT >= 31) {
                        for (AudioDeviceInfo d : am.getAvailableCommunicationDevices())
                            if (d.getType() == AudioDeviceInfo.TYPE_BUILTIN_EARPIECE) { am.setCommunicationDevice(d); break; }
                    }
                }

                int rate = 44100;
                short[] pcm = beeps(rate);
                track = new AudioTrack(stream, rate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT,
                        pcm.length * 2, AudioTrack.MODE_STATIC);
                track.write(pcm, 0, pcm.length);
                track.play();
                final int durationMs = pcm.length * 1000 / rate;
                a.runLater(new Runnable() {
                    @Override public void run() {
                        if (stopped) return;
                        Out.result(id(), "confirm", Out.obj("volume_percent", 80));
                        a.testFinished(Output.this);
                    }
                }, durationMs + 300);
            } catch (Exception e) {
                Out.result(id(), "bad", Out.obj("reason", "exception", "message", String.valueOf(e.getMessage())));
                a.testFinished(this);
            }
        }

        private static short[] beeps(int rate) {
            int beep = rate / 2, gap = rate / 3;
            short[] s = new short[3 * beep + 2 * gap];
            int pos = 0;
            for (int b = 0; b < 3; b++) {
                for (int i = 0; i < beep; i++) {
                    double env = Math.min(1.0, Math.min(i, beep - i) / (rate * 0.01)); // fondu 10 ms : pas de clic
                    s[pos++] = (short) (Math.sin(2 * Math.PI * 1000 * i / rate) * 26000 * env);
                }
                if (b < 2) pos += gap;
            }
            return s;
        }

        @Override
        @SuppressWarnings("deprecation")
        public void stop() {
            stopped = true;
            try { if (track != null) { track.stop(); track.release(); } } catch (Exception ignored) { }
            track = null;
            AudioManager am = (AudioManager) a.getSystemService(Context.AUDIO_SERVICE);
            try {
                if (savedVolume >= 0) am.setStreamVolume(stream, savedVolume, 0);
                if (earpiece) {
                    if (Build.VERSION.SDK_INT >= 31) am.clearCommunicationDevice();
                    am.setMode(savedMode);
                }
            } catch (Exception ignored) { }
        }
    }

    /**
     * Micro : enregistrement de 5 s pendant lequel le technicien parle ou tape des mains ; niveau mesuré en
     * direct. L'enregistrement est gardé (WAV) pour être réécouté sur le PC.
     */
    static final class Microphone implements DiagTest {
        private static final int RATE = 16000, SECONDS = 5;
        private final MainActivity a;
        private volatile boolean stopped;

        Microphone(MainActivity a) { this.a = a; }

        @Override public String id() { return "microphone"; }

        @Override
        public void start() {
            a.setMessage(L.t("Parlez ou tapez des mains près du micro.", "Speak or clap near the microphone."));
            new Thread(new Runnable() { @Override public void run() { record(); } }, "micro").start();
        }

        private void record() {
            AudioRecord rec = null;
            try {
                int min = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
                rec = new AudioRecord(MediaRecorder.AudioSource.MIC, RATE, AudioFormat.CHANNEL_IN_MONO,
                        AudioFormat.ENCODING_PCM_16BIT, Math.max(min, RATE));
                if (rec.getState() != AudioRecord.STATE_INITIALIZED) {
                    Out.result(id(), "bad", Out.obj("reason", "init"));
                    return;
                }
                short[] all = new short[RATE * SECONDS];
                short[] chunk = new short[RATE / 5];
                int pos = 0, peak = 0;
                rec.startRecording();
                while (pos < all.length && !stopped) {
                    int n = rec.read(chunk, 0, Math.min(chunk.length, all.length - pos));
                    if (n <= 0) break;
                    int chunkPeak = 0;
                    for (int i = 0; i < n; i++) {
                        int v = Math.abs((int) chunk[i]);
                        if (v > chunkPeak) chunkPeak = v;
                        all[pos + i] = chunk[i];
                    }
                    pos += n;
                    if (chunkPeak > peak) peak = chunkPeak;
                    Out.live(id(), Out.obj("level_db", db(chunkPeak), "peak_db", db(peak), "seconds", pos / (double) RATE));
                }
                rec.stop();
                if (stopped) return;
                File dir = a.getExternalFilesDir(null);
                if (dir == null) dir = a.getFilesDir();
                File wav = new File(dir, "microphone.wav");
                writeWav(wav, all, pos);
                double peakDb = db(peak);
                String status = peakDb >= -25 ? "ok" : peakDb >= -45 ? "warn" : "bad";
                Out.result(id(), status, Out.obj("peak_db", peakDb, "file", wav.getAbsolutePath()));
            } catch (Exception e) {
                Out.result(id(), "bad", Out.obj("reason", "exception", "message", String.valueOf(e.getMessage())));
            } finally {
                if (rec != null) try { rec.release(); } catch (Exception ignored) { }
                a.runLater(new Runnable() { @Override public void run() { a.testFinished(Microphone.this); } }, 0);
            }
        }

        private static double db(int peak) {
            if (peak <= 0) return -90;
            return Math.round(20 * Math.log10(peak / 32768.0) * 10) / 10.0;
        }

        private static void writeWav(File f, short[] data, int count) throws IOException {
            int bytes = count * 2;
            FileOutputStream o = new FileOutputStream(f);
            byte[] h = new byte[44];
            putStr(h, 0, "RIFF"); putInt(h, 4, 36 + bytes); putStr(h, 8, "WAVE"); putStr(h, 12, "fmt ");
            putInt(h, 16, 16); putShort(h, 20, 1); putShort(h, 22, 1); putInt(h, 24, RATE); putInt(h, 28, RATE * 2);
            putShort(h, 32, 2); putShort(h, 34, 16); putStr(h, 36, "data"); putInt(h, 40, bytes);
            o.write(h);
            byte[] pcm = new byte[bytes];
            for (int i = 0; i < count; i++) { pcm[2 * i] = (byte) data[i]; pcm[2 * i + 1] = (byte) (data[i] >> 8); }
            o.write(pcm);
            o.close();
        }

        private static void putStr(byte[] b, int at, String s) { for (int i = 0; i < 4; i++) b[at + i] = (byte) s.charAt(i); }
        private static void putInt(byte[] b, int at, int v) { for (int i = 0; i < 4; i++) b[at + i] = (byte) (v >> (8 * i)); }
        private static void putShort(byte[] b, int at, int v) { b[at] = (byte) v; b[at + 1] = (byte) (v >> 8); }

        @Override public void stop() { stopped = true; }
    }
}

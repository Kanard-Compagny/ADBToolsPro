package fr.kanard.adbtools.diag;

import android.content.Context;
import android.os.Build;
import android.os.VibrationEffect;
import android.os.Vibrator;

/** Vibreur : 3 vibrations ; le technicien confirme sur le PC. */
final class VibratorTest implements DiagTest {
    private final MainActivity a;
    private Vibrator v;
    private volatile boolean stopped;

    VibratorTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "vibrator"; }

    @Override
    @SuppressWarnings("deprecation")
    public void start() {
        v = (Vibrator) a.getSystemService(Context.VIBRATOR_SERVICE);
        if (v == null || !v.hasVibrator()) { Out.result(id(), "na", Out.obj("reason", "absent")); a.testFinished(this); return; }
        a.setMessage("Vibreur : 3 vibrations / Vibrator: 3 pulses");
        long[] pattern = {0, 600, 400, 600, 400, 600};
        if (Build.VERSION.SDK_INT >= 26) v.vibrate(VibrationEffect.createWaveform(pattern, -1));
        else v.vibrate(pattern, -1);
        a.runLater(new Runnable() {
            @Override public void run() {
                if (stopped) return; // résultat déjà choisi par le technicien : pas de « à confirmer » après coup
                Out.result(id(), "confirm", Out.obj("pulses", 3));
                a.testFinished(VibratorTest.this);
            }
        }, 3000);
    }

    @Override
    public void stop() {
        stopped = true;
        try { if (v != null) v.cancel(); } catch (Exception ignored) { }
    }
}

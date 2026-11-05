package fr.kanard.adbtools.diag;

import android.content.Context;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraManager;
import android.os.Build;

/** Flash (torche) : allumé 3 fois 1 s ; le technicien confirme sur le PC qu'il s'est bien allumé. */
final class TorchTest implements DiagTest {
    private final MainActivity a;
    private String cameraId;
    private boolean stopped;
    private int step;

    TorchTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "flash"; }

    @Override
    public void start() {
        if (Build.VERSION.SDK_INT < 23) { Out.result(id(), "na", Out.obj("reason", "android")); a.testFinished(this); return; }
        try {
            CameraManager cm = (CameraManager) a.getSystemService(Context.CAMERA_SERVICE);
            for (String id : cm.getCameraIdList()) {
                Boolean f = cm.getCameraCharacteristics(id).get(CameraCharacteristics.FLASH_INFO_AVAILABLE);
                if (f != null && f) { cameraId = id; break; }
            }
        } catch (Exception ignored) { }
        if (cameraId == null) { Out.result(id(), "na", Out.obj("reason", "absent")); a.testFinished(this); return; }
        a.setMessage("Flash : il doit clignoter 3 fois / Flash: it should blink 3 times");
        blink();
    }

    private void blink() {
        if (stopped) return;
        boolean on = step % 2 == 0;
        if (!torch(on)) { Out.result(id(), "bad", Out.obj("reason", "torch")); a.testFinished(this); return; }
        step++;
        if (step >= 6) { torch(false); Out.result(id(), "confirm", Out.obj("blinks", 3)); a.testFinished(this); return; }
        a.runLater(new Runnable() { @Override public void run() { blink(); } }, on ? 1000 : 500);
    }

    private boolean torch(boolean on) {
        try {
            CameraManager cm = (CameraManager) a.getSystemService(Context.CAMERA_SERVICE);
            cm.setTorchMode(cameraId, on);
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    @Override
    public void stop() {
        stopped = true;
        if (cameraId != null && Build.VERSION.SDK_INT >= 23) torch(false);
    }
}

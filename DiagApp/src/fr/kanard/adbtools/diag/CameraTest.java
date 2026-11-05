package fr.kanard.adbtools.diag;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.ImageFormat;
import android.graphics.SurfaceTexture;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Size;
import android.view.Surface;
import android.view.TextureView;

import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.nio.ByteBuffer;
import java.util.Arrays;

/**
 * Caméra arrière ou avant : aperçu à l'écran (le technicien voit ce que filme le terminal), mise au point et
 * exposition pendant 2,5 s, puis photo JPEG enregistrée dans le dossier de l'appli (récupérée par ADB Tools).
 * Résultat : ok si l'image a du contenu (luminosité et contraste plausibles), warn si elle est noire,
 * blanche ou uniforme (objectif masqué, capteur défaillant), bad si la caméra ne s'ouvre pas.
 */
final class CameraTest implements DiagTest {
    private final MainActivity a;
    private final boolean front;
    private HandlerThread thread;
    private Handler handler;
    private CameraDevice device;
    private CameraCaptureSession session;
    private ImageReader reader;
    private Surface previewSurface;
    private boolean finished;
    private int sensorOrientation;

    CameraTest(MainActivity a, boolean front) {
        this.a = a;
        this.front = front;
    }

    @Override public String id() { return front ? "camera_front" : "camera_back"; }

    @Override
    public void start() {
        thread = new HandlerThread("camera");
        thread.start();
        handler = new Handler(thread.getLooper());
        a.setMessage(front ? "Caméra avant : visez un objet / Front camera: aim at an object"
                           : "Caméra arrière : visez un objet / Back camera: aim at an object");
        a.showPreview(true);
        TextureView tv = a.preview();
        if (tv.isAvailable()) open(tv.getSurfaceTexture());
        else tv.setSurfaceTextureListener(new TextureView.SurfaceTextureListener() {
            @Override public void onSurfaceTextureAvailable(SurfaceTexture st, int w, int h) { open(st); }
            @Override public void onSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) { }
            @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture st) { return true; }
            @Override public void onSurfaceTextureUpdated(SurfaceTexture st) { }
        });
    }

    private void open(final SurfaceTexture texture) {
        try {
            CameraManager cm = (CameraManager) a.getSystemService(Context.CAMERA_SERVICE);
            String chosen = null;
            for (String id : cm.getCameraIdList()) {
                Integer facing = cm.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING);
                int wanted = front ? CameraCharacteristics.LENS_FACING_FRONT : CameraCharacteristics.LENS_FACING_BACK;
                if (facing != null && facing == wanted) { chosen = id; break; }
            }
            if (chosen == null) { finish("na", Out.obj("reason", "absent")); return; }

            CameraCharacteristics ch = cm.getCameraCharacteristics(chosen);
            Integer so = ch.get(CameraCharacteristics.SENSOR_ORIENTATION);
            sensorOrientation = so == null ? 0 : so;
            StreamConfigurationMap map = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            Size jpeg = pick(map.getOutputSizes(ImageFormat.JPEG), 2048);
            Size prev = pick(map.getOutputSizes(SurfaceTexture.class), 1280);
            texture.setDefaultBufferSize(prev.getWidth(), prev.getHeight());
            previewSurface = new Surface(texture);
            reader = ImageReader.newInstance(jpeg.getWidth(), jpeg.getHeight(), ImageFormat.JPEG, 2);
            reader.setOnImageAvailableListener(new ImageReader.OnImageAvailableListener() {
                @Override public void onImageAvailable(ImageReader r) { save(r); }
            }, handler);

            cm.openCamera(chosen, new CameraDevice.StateCallback() {
                @Override public void onOpened(CameraDevice d) { device = d; startPreview(); }
                @Override public void onDisconnected(CameraDevice d) { d.close(); }
                @Override public void onError(CameraDevice d, int error) {
                    d.close();
                    finish("bad", Out.obj("reason", "error", "code", error));
                }
            }, handler);
        } catch (SecurityException e) {
            finish("bad", Out.obj("reason", "permission"));
        } catch (Exception e) {
            finish("bad", Out.obj("reason", "exception", "message", String.valueOf(e.getMessage())));
        }
    }

    /** Plus grande taille ne dépassant pas maxWidth (ou la plus petite disponible). */
    private static Size pick(Size[] sizes, int maxWidth) {
        Size best = null, smallest = null;
        for (Size s : sizes) {
            if (smallest == null || s.getWidth() < smallest.getWidth()) smallest = s;
            if (s.getWidth() <= maxWidth && (best == null || s.getWidth() * s.getHeight() > best.getWidth() * best.getHeight())) best = s;
        }
        return best != null ? best : smallest;
    }

    @SuppressWarnings("deprecation")
    private void startPreview() {
        try {
            device.createCaptureSession(Arrays.asList(previewSurface, reader.getSurface()), new CameraCaptureSession.StateCallback() {
                @Override
                public void onConfigured(CameraCaptureSession s) {
                    session = s;
                    try {
                        CaptureRequest.Builder b = device.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                        b.addTarget(previewSurface);
                        b.set(CaptureRequest.CONTROL_MODE, CaptureRequest.CONTROL_MODE_AUTO);
                        b.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
                        s.setRepeatingRequest(b.build(), null, handler);
                        handler.postDelayed(new Runnable() { @Override public void run() { capture(); } }, 2500);
                    } catch (Exception e) {
                        finish("bad", Out.obj("reason", "exception", "message", String.valueOf(e.getMessage())));
                    }
                }
                @Override
                public void onConfigureFailed(CameraCaptureSession s) {
                    finish("bad", Out.obj("reason", "session"));
                }
            }, handler);
        } catch (CameraAccessException e) {
            finish("bad", Out.obj("reason", "access"));
        }
    }

    private void capture() {
        if (finished || session == null) return;
        try {
            CaptureRequest.Builder b = device.createCaptureRequest(CameraDevice.TEMPLATE_STILL_CAPTURE);
            b.addTarget(reader.getSurface());
            b.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
            b.set(CaptureRequest.JPEG_ORIENTATION, sensorOrientation);
            session.capture(b.build(), null, handler);
        } catch (Exception e) {
            finish("bad", Out.obj("reason", "capture"));
        }
    }

    private void save(ImageReader r) {
        Image img = null;
        try {
            img = r.acquireLatestImage();
            if (img == null || finished) return;
            ByteBuffer buf = img.getPlanes()[0].getBuffer();
            byte[] bytes = new byte[buf.remaining()];
            buf.get(bytes);
            File dir = a.getExternalFilesDir(null);
            if (dir == null) dir = a.getFilesDir();
            File f = new File(dir, id() + ".jpg");
            FileOutputStream fo = new FileOutputStream(f);
            fo.write(bytes);
            fo.close();

            // Contenu de l'image : luminosité moyenne et écart-type sur une version réduite.
            BitmapFactory.Options opt = new BitmapFactory.Options();
            opt.inSampleSize = 16;
            Bitmap bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.length, opt);
            double mean = 0, sd = 0;
            if (bmp != null) {
                int w = bmp.getWidth(), h = bmp.getHeight(), n = w * h;
                int[] px = new int[n];
                bmp.getPixels(px, 0, w, 0, 0, w, h);
                double sum = 0, sum2 = 0;
                for (int p : px) {
                    double l = 0.299 * ((p >> 16) & 0xFF) + 0.587 * ((p >> 8) & 0xFF) + 0.114 * (p & 0xFF);
                    sum += l; sum2 += l * l;
                }
                mean = sum / n;
                sd = Math.sqrt(Math.max(0, sum2 / n - mean * mean));
                bmp.recycle();
            }
            String status = (mean > 12 && mean < 245 && sd > 8) ? "ok" : "warn";
            finish(status, Out.obj("file", f.getAbsolutePath(), "width", img.getWidth(), "height", img.getHeight(),
                    "brightness", Math.round(mean), "contrast", Math.round(sd)));
        } catch (Exception e) {
            finish("bad", Out.obj("reason", "save", "message", String.valueOf(e.getMessage())));
        } finally {
            if (img != null) img.close();
        }
    }

    private void finish(String status, JSONObject detail) {
        if (finished) return;
        finished = true;
        Out.result(id(), status, detail);
        // On laisse l'aperçu 1,5 s pour que le technicien voie la photo prise, puis on libère la caméra.
        a.runLater(new Runnable() { @Override public void run() { a.testFinished(CameraTest.this); } }, 1500);
    }

    @Override
    public void stop() {
        finished = true;
        try { if (session != null) session.close(); } catch (Exception ignored) { }
        try { if (device != null) device.close(); } catch (Exception ignored) { }
        try { if (reader != null) reader.close(); } catch (Exception ignored) { }
        try { if (previewSurface != null) previewSurface.release(); } catch (Exception ignored) { }
        session = null; device = null; reader = null; previewSurface = null;
        if (thread != null) thread.quitSafely();
        a.showPreview(false);
    }
}

package fr.kanard.adbtools.diag;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.view.MotionEvent;
import android.view.View;

/** Dalle tactile : grille de zones à balayer du doigt ; réussi quand toutes les zones ont réagi. */
final class TouchTest implements DiagTest, MainActivity.OverlayTest {
    private static final int COLS = 6;
    private final MainActivity a;
    private Grid grid;
    private boolean done;
    private long lastLive;

    TouchTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "touch"; }

    @Override
    public void start() {
        a.setMessage(L.t("Balayez tout l'écran du doigt : chaque case touchée devient verte. Retour pour arrêter avant la fin.",
                "Swipe your finger over the whole screen: each touched cell turns green. Back to stop before the end."));
        grid = new Grid(a);
        a.showOverlay(grid);
    }

    private void progress(int covered, int total) {
        long now = System.currentTimeMillis();
        if (now - lastLive > 300 || covered == total) {
            lastLive = now;
            Out.live(id(), Out.obj("covered", covered, "total", total));
        }
        if (covered == total) finish();
    }

    private void finish() {
        if (done) return;
        done = true;
        int covered = grid == null ? 0 : grid.covered(), total = grid == null ? 0 : grid.total();
        a.hideOverlay();
        int pct = total == 0 ? 0 : Math.round(100f * covered / total);
        // Carte des zones (1 = touchée), ligne par ligne : ADB Tools la redessine dans sa fiche et le rapport.
        Out.result(id(), covered == total && total > 0 ? "ok" : "warn", Out.obj("covered", covered, "total", total, "percent", pct,
                "cols", COLS, "rows", grid == null ? 0 : grid.rows, "grid", grid == null ? "" : grid.map()));
        a.testFinished(this);
    }

    @Override public void onBack() { finish(); }

    @Override
    public void stop() {
        done = true;
        a.hideOverlay();
    }

    private final class Grid extends View {
        private boolean[] hit;
        private int rows, count;
        private final Paint on = new Paint(), off = new Paint(), line = new Paint();

        Grid(Context c) {
            super(c);
            on.setColor(Color.argb(200, 0x2E, 0x9E, 0x6E));
            off.setColor(L.dark ? Color.rgb(0x1F, 0x24, 0x27) : Color.rgb(0xED, 0xEF, 0xF1));
            line.setColor(L.dark ? Color.rgb(0x3A, 0x42, 0x46) : Color.rgb(0xC3, 0xC8, 0xCD));
            line.setStrokeWidth(2);
        }

        int covered() { return count; }
        String map() {
            StringBuilder sb = new StringBuilder();
            if (hit != null) for (boolean b : hit) sb.append(b ? '1' : '0');
            return sb.toString();
        }
        int total() { return hit == null ? 0 : hit.length; }

        @Override protected void onSizeChanged(int w, int h, int ow, int oh) {
            float cell = w / (float) COLS;
            rows = Math.max(1, Math.round(h / cell));
            hit = new boolean[COLS * rows];
            count = 0;
        }

        @Override protected void onDraw(Canvas c) {
            if (hit == null) return;
            float cw = getWidth() / (float) COLS, ch = getHeight() / (float) rows;
            for (int r = 0; r < rows; r++)
                for (int k = 0; k < COLS; k++)
                    c.drawRect(k * cw, r * ch, (k + 1) * cw, (r + 1) * ch, hit[r * COLS + k] ? on : off);
            for (int k = 1; k < COLS; k++) c.drawLine(k * cw, 0, k * cw, getHeight(), line);
            for (int r = 1; r < rows; r++) c.drawLine(0, r * ch, getWidth(), r * ch, line);
        }

        @Override public boolean onTouchEvent(MotionEvent e) {
            if (hit == null || done) return true;
            for (int p = 0; p < e.getPointerCount(); p++) {
                for (int h = 0; h < e.getHistorySize(); h++) mark(e.getHistoricalX(p, h), e.getHistoricalY(p, h));
                mark(e.getX(p), e.getY(p));
            }
            invalidate();
            progress(count, hit.length);
            return true;
        }

        private void mark(float x, float y) {
            int k = (int) (x / (getWidth() / (float) COLS)), r = (int) (y / (getHeight() / (float) rows));
            if (k < 0 || k >= COLS || r < 0 || r >= rows) return;
            int i = r * COLS + k;
            if (!hit[i]) { hit[i] = true; count++; }
        }
    }
}

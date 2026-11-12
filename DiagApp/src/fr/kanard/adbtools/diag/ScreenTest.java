package fr.kanard.adbtools.diag;

import android.graphics.Color;
import android.view.Gravity;
import android.view.View;
import android.widget.FrameLayout;
import android.widget.TextView;

/** Écran plein en couleurs unies (pixels morts, taches, brûlure) : un appui passe à la couleur suivante. */
final class ScreenTest implements DiagTest, MainActivity.OverlayTest {
    private static final int[] COLORS = { Color.RED, Color.GREEN, Color.BLUE, Color.WHITE, Color.BLACK, Color.rgb(128, 128, 128) };
    private final MainActivity a;
    private int index;
    private boolean done;
    private FrameLayout view;
    private TextView hint;

    ScreenTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "screen"; }

    @Override
    public void start() {
        a.setMessage(L.t("Chaque couleur remplit l'écran : cherchez pixels morts, taches ou traces. Touchez l'écran pour passer à la suivante.",
                "Each colour fills the screen: look for dead pixels, spots or burn-in. Tap the screen for the next one."));
        view = new FrameLayout(a);
        hint = new TextView(a);
        hint.setTextColor(Color.WHITE);
        hint.setShadowLayer(4, 0, 0, Color.BLACK);
        hint.setTextSize(14);
        hint.setGravity(Gravity.CENTER);
        view.addView(hint, new FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM));
        view.setOnClickListener(new View.OnClickListener() { @Override public void onClick(View v) { next(); } });
        index = 0;
        paint();
        a.showOverlay(view);
    }

    private void paint() {
        view.setBackgroundColor(COLORS[index]);
        hint.setText(index == 0 ? (index + 1) + " / " + COLORS.length + L.t("  ·  touchez pour continuer", "  ·  tap to continue") : "");
        hint.setPadding(0, 0, 0, 80);
        Out.live(id(), Out.obj("color", index + 1, "total", COLORS.length));
    }

    private void next() {
        if (done) return;
        index++;
        if (index >= COLORS.length) { finish(); return; }
        paint();
    }

    private void finish() {
        if (done) return;
        done = true;
        a.hideOverlay();
        Out.result(id(), "confirm", Out.obj("colors", COLORS.length));
        a.testFinished(this);
    }

    @Override public void onBack() { finish(); }

    @Override
    public void stop() {
        done = true;
        a.hideOverlay();
    }
}

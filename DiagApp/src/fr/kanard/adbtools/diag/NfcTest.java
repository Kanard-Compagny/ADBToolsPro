package fr.kanard.adbtools.diag;

import android.nfc.NfcAdapter;
import android.nfc.Tag;

import org.json.JSONArray;

/** NFC : lecture d'un badge ou d'une étiquette posé au dos du terminal (30 s). */
final class NfcTest implements DiagTest, NfcAdapter.ReaderCallback {
    private static final int TIMEOUT_MS = 30000;
    private final MainActivity a;
    private NfcAdapter adapter;
    private volatile boolean done;

    NfcTest(MainActivity a) { this.a = a; }

    @Override public String id() { return "nfc"; }

    @Override
    public void start() {
        adapter = NfcAdapter.getDefaultAdapter(a);
        if (adapter == null) { Out.result(id(), "na", Out.obj("reason", "absent")); a.testFinished(this); return; }
        if (!adapter.isEnabled()) { Out.result(id(), "warn", Out.obj("reason", "disabled")); a.testFinished(this); return; }
        a.setMessage("NFC : posez un badge au dos du terminal / NFC: hold a tag against the back");
        int flags = NfcAdapter.FLAG_READER_NFC_A | NfcAdapter.FLAG_READER_NFC_B | NfcAdapter.FLAG_READER_NFC_F
                | NfcAdapter.FLAG_READER_NFC_V | NfcAdapter.FLAG_READER_NFC_BARCODE;
        adapter.enableReaderMode(a, this, flags, null);
        a.runLater(new Runnable() {
            @Override public void run() {
                if (done) return;
                done = true;
                Out.result(id(), "warn", Out.obj("reason", "timeout"));
                a.testFinished(NfcTest.this);
            }
        }, TIMEOUT_MS);
    }

    @Override
    public void onTagDiscovered(Tag tag) {
        if (done) return;
        done = true;
        StringBuilder uid = new StringBuilder();
        byte[] id = tag.getId();
        if (id != null) for (byte b : id) uid.append(String.format("%02X", b));
        JSONArray techs = new JSONArray();
        for (String t : tag.getTechList()) techs.put(t.substring(t.lastIndexOf('.') + 1));
        Out.result(id(), "ok", Out.obj("uid", uid.toString(), "techs", techs));
        a.runLater(new Runnable() { @Override public void run() { a.testFinished(NfcTest.this); } }, 0);
    }

    @Override
    public void stop() {
        done = true;
        try { if (adapter != null) adapter.disableReaderMode(a); } catch (Exception ignored) { }
    }
}

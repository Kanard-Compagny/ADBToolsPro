package fr.kanard.adbtools.helper;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.net.wifi.WifiConfiguration;
import android.net.wifi.WifiManager;
import android.os.Build;

/**
 * Enregistre un réseau Wi-Fi demandé par ADB Tools :
 *   am broadcast -f 32 -n fr.kanard.adbtools.helper/.AddNetwork --es ssid NOM --es type wpa2|wpa3|open --es pass MOTDEPASSE --ez hidden false
 * Résultat (affiché par « am broadcast ») : result=1 et « OK id » si le réseau est enregistré, sinon result=2/3 et « ERR raison ».
 */
public class AddNetwork extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        try {
            WifiManager wm = (WifiManager) context.getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm == null) { done(3, "ERR no wifi"); return; }
            String ssid = intent.getStringExtra("ssid");
            String type = intent.getStringExtra("type");
            String pass = intent.getStringExtra("pass");
            boolean hidden = intent.getBooleanExtra("hidden", false);
            if (ssid == null || ssid.length() == 0) { done(3, "ERR ssid"); return; }
            if (!wm.isWifiEnabled()) wm.setWifiEnabled(true);

            WifiConfiguration c = new WifiConfiguration();
            c.SSID = "\"" + ssid + "\"";
            c.hiddenSSID = hidden;
            if ("open".equals(type)) {
                c.allowedKeyManagement.set(WifiConfiguration.KeyMgmt.NONE);
            } else if ("wpa3".equals(type) && Build.VERSION.SDK_INT >= 30) {
                c.setSecurityParams(WifiConfiguration.SECURITY_TYPE_SAE);
                c.preSharedKey = "\"" + pass + "\"";
            } else {
                c.allowedKeyManagement.set(WifiConfiguration.KeyMgmt.WPA_PSK);
                c.preSharedKey = "\"" + pass + "\"";
            }
            int id = wm.addNetwork(c);
            if (id < 0) { done(2, "ERR refused"); return; }
            wm.enableNetwork(id, false);
            wm.saveConfiguration();
            done(1, "OK " + id);
        } catch (Throwable t) {
            done(3, "ERR " + t);
        }
    }

    private void done(int code, String data) {
        setResultCode(code);
        setResultData(data);
    }
}

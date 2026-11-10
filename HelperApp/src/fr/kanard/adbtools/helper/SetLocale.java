package fr.kanard.adbtools.helper;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.res.Configuration;
import android.os.Build;
import android.os.LocaleList;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.Locale;

/**
 * Change la langue du système, comme le ferait Paramètres > Langues :
 *   am broadcast -f 32 -n fr.kanard.adbtools.helper/.SetLocale --es locale fr-FR
 * Demande l'autorisation CHANGE_CONFIGURATION (pm grant fr.kanard.adbtools.helper android.permission.CHANGE_CONFIGURATION).
 * Résultat : result=1 « OK fr-FR », sinon result=3 « ERR raison ».
 */
public class SetLocale extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String tags = intent.getStringExtra("locale");
        if (tags == null || tags.length() == 0) { done(3, "ERR locale"); return; }
        try {
            if (Build.VERSION.SDK_INT >= 24) {
                // Méthode des Paramètres d'Android (com.android.internal.app.LocalePicker).
                try {
                    Class<?> picker = Class.forName("com.android.internal.app.LocalePicker");
                    Method update = picker.getMethod("updateLocales", LocaleList.class);
                    update.invoke(null, LocaleList.forLanguageTags(tags));
                    done(1, "OK " + tags);
                    return;
                } catch (Throwable ignored) { }
            }
            // Repli : mise à jour directe de la configuration persistante.
            Object am;
            try {
                am = Class.forName("android.app.ActivityManager").getMethod("getService").invoke(null);
            } catch (Throwable t) {
                am = Class.forName("android.app.ActivityManagerNative").getMethod("getDefault").invoke(null);
            }
            Configuration config = (Configuration) am.getClass().getMethod("getConfiguration").invoke(am);
            if (Build.VERSION.SDK_INT >= 24) config.setLocales(LocaleList.forLanguageTags(tags));
            else config.locale = Locale.forLanguageTag(tags.split(",")[0]);
            try {
                Field f = Configuration.class.getField("userSetLocale");
                f.setBoolean(config, true);
            } catch (Throwable ignored) { }
            am.getClass().getMethod("updatePersistentConfiguration", Configuration.class).invoke(am, config);
            done(1, "OK " + tags);
        } catch (Throwable t) {
            Throwable c = t.getCause() != null ? t.getCause() : t;
            done(3, "ERR " + c);
        }
    }

    private void done(int code, String data) {
        setResultCode(code);
        setResultData(data);
    }
}

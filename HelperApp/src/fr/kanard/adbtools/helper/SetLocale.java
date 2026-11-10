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
 * Trois méthodes essayées dans l'ordre (selon la version d'Android, certaines sont fermées aux applis) ; résultat :
 * result=1 « OK méthode », sinon result=3 « ERR » avec la raison de chaque échec.
 */
public class SetLocale extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String tags = intent.getStringExtra("locale");
        if (tags == null || tags.length() == 0) { done(3, "ERR locale"); return; }
        StringBuilder errors = new StringBuilder();
        if (context.checkCallingOrSelfPermission("android.permission.CHANGE_CONFIGURATION") != 0)
            errors.append("[permission CHANGE_CONFIGURATION absente] ");

        // 1. Android 13+ : LocaleManager.setSystemLocales (API système).
        if (Build.VERSION.SDK_INT >= 33) {
            try {
                Object lm = context.getSystemService("locale");
                lm.getClass().getMethod("setSystemLocales", LocaleList.class).invoke(lm, LocaleList.forLanguageTags(tags));
                done(1, "OK LocaleManager");
                return;
            } catch (Throwable t) { errors.append("LocaleManager: ").append(reason(t)).append(" | "); }
        }
        // 2. Méthode des Paramètres d'Android (com.android.internal.app.LocalePicker).
        if (Build.VERSION.SDK_INT >= 24) {
            try {
                Class<?> picker = Class.forName("com.android.internal.app.LocalePicker");
                picker.getMethod("updateLocales", LocaleList.class).invoke(null, LocaleList.forLanguageTags(tags));
                done(1, "OK LocalePicker");
                return;
            } catch (Throwable t) { errors.append("LocalePicker: ").append(reason(t)).append(" | "); }
        }
        // 3. Configuration persistante du gestionnaire d'activités.
        try {
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
            Method update = am.getClass().getMethod("updatePersistentConfiguration", Configuration.class);
            update.invoke(am, config);
            done(1, "OK ActivityManager");
        } catch (Throwable t) {
            errors.append("ActivityManager: ").append(reason(t));
            done(3, "ERR " + errors);
        }
    }

    private static String reason(Throwable t) {
        Throwable c = t;
        while (c.getCause() != null) c = c.getCause();
        String s = c.getClass().getSimpleName() + (c.getMessage() != null ? " " + c.getMessage() : "");
        return s.length() > 160 ? s.substring(0, 160) : s;
    }

    private void done(int code, String data) {
        setResultCode(code);
        setResultData(data);
    }
}

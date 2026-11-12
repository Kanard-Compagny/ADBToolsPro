package fr.kanard.adbtools.helper;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Base64;

import java.lang.reflect.InvocationHandler;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;

/**
 * Applique une étape MX (Zebra) envoyée par le Master d'ADB Tools, par l'interface officielle des applis (EMDK, présente
 * sur les terminaux Zebra) :
 *   am broadcast -f 32 -n fr.kanard.adbtools.helper/.ApplyMx --es name ADBTools --es xml64 BASE64
 * « xml64 » = une ou plusieurs « characteristic » MX (UTF-8, Base64), sans l'enveloppe « wap-provisioningdoc ».
 * Résultat : result=1 si le terminal l'accepte, 2 s'il la refuse (détail dans la réponse), 3 si EMDK est absent ou en
 * erreur ; data = « CODE|réponse du terminal en Base64 ».
 * EMDK est chargé par réflexion : l'appli s'installe sur toutes les marques, la fonction n'existe que sur Zebra.
 */
public class ApplyMx extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        final PendingResult pending = goAsync();
        final String name = intent.getStringExtra("name") != null ? intent.getStringExtra("name") : "ADBTools";
        final String xml;
        try {
            xml = new String(Base64.decode(intent.getStringExtra("xml64"), Base64.DEFAULT), "UTF-8");
        } catch (Throwable t) {
            finish(pending, 3, "XML", reason(t));
            return;
        }
        final String profile = "<characteristic type=\"Profile\"><parm name=\"ProfileName\" value=\"" + name + "\"/>" + xml + "</characteristic>";
        try {
            final Class<?> managerClass = Class.forName("com.symbol.emdk.EMDKManager");
            final Class<?> listenerClass = Class.forName("com.symbol.emdk.EMDKManager$EMDKListener");
            InvocationHandler handler = new InvocationHandler() {
                @Override
                public Object invoke(Object proxy, Method method, Object[] args) {
                    if ("onOpened".equals(method.getName())) {
                        final Object manager = args[0];
                        new Thread(new Runnable() {
                            @Override public void run() { process(pending, managerClass, manager, name, profile); }
                        }).start();
                    } else if ("onClosed".equals(method.getName())) {
                        finish(pending, 3, "CLOSED", "EMDK fermé");
                    } else if ("hashCode".equals(method.getName())) {
                        return System.identityHashCode(proxy);
                    } else if ("equals".equals(method.getName())) {
                        return proxy == args[0];
                    } else if ("toString".equals(method.getName())) {
                        return "ApplyMxListener";
                    }
                    return null;
                }
            };
            Object listener = Proxy.newProxyInstance(listenerClass.getClassLoader(), new Class<?>[] { listenerClass }, handler);
            Object res = managerClass.getMethod("getEMDKManager", Context.class, listenerClass).invoke(null, context.getApplicationContext(), listener);
            String code = status(res);
            if (!"SUCCESS".equals(code)) finish(pending, 3, code, "EMDK indisponible");
        } catch (ClassNotFoundException e) {
            finish(pending, 3, "NO_EMDK", "EMDK absent : terminal non Zebra ou EMDK désactivé");
        } catch (Throwable t) {
            finish(pending, 3, "ERROR", reason(t));
        }
    }

    private static void process(PendingResult pending, Class<?> managerClass, Object manager, String name, String profile) {
        try {
            Class<?> featureClass = Class.forName("com.symbol.emdk.EMDKManager$FEATURE_TYPE");
            Object profileFeature = enumValue(featureClass, "PROFILE");
            Object pm = managerClass.getMethod("getInstance", featureClass).invoke(manager, profileFeature);
            Class<?> flagClass = Class.forName("com.symbol.emdk.ProfileManager$PROFILE_FLAG");
            Object set = enumValue(flagClass, "SET");
            Object res = pm.getClass().getMethod("processProfile", String.class, flagClass, String[].class)
                    .invoke(pm, name, set, new String[] { profile });
            String code = status(res);
            String detail = "";
            try { detail = String.valueOf(res.getClass().getMethod("getStatusString").invoke(res)); } catch (Throwable ignored) { }
            boolean ok = "SUCCESS".equals(code) && !detail.contains("characteristic-error") && !detail.contains("parm-error");
            finish(pending, ok ? 1 : 2, code, detail);
        } catch (Throwable t) {
            finish(pending, 3, "ERROR", reason(t));
        } finally {
            try { managerClass.getMethod("release").invoke(manager); } catch (Throwable ignored) { }
        }
    }

    @SuppressWarnings({ "unchecked", "rawtypes" })
    private static Object enumValue(Class<?> c, String n) { return Enum.valueOf((Class) c, n); }

    private static String status(Object results) {
        try { return String.valueOf(results.getClass().getField("statusCode").get(results)); } catch (Throwable t) { return "UNKNOWN"; }
    }

    private static String reason(Throwable t) {
        Throwable c = t;
        while (c.getCause() != null) c = c.getCause();
        String s = c.getClass().getSimpleName() + (c.getMessage() != null ? " " + c.getMessage() : "");
        return s.length() > 300 ? s.substring(0, 300) : s;
    }

    private static synchronized void finish(PendingResult pending, int code, String status, String detail) {
        if (pending == null) return;
        try {
            String b64 = Base64.encodeToString((detail == null ? "" : detail).getBytes("UTF-8"), Base64.NO_WRAP);
            pending.setResultCode(code);
            pending.setResultData(status + "|" + b64);
            pending.finish();
        } catch (Throwable ignored) { }
    }
}

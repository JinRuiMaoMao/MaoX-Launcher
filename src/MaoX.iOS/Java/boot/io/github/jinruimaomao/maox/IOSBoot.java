package io.github.jinruimaomao.maox;

import java.beans.Beans;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.Arrays;

/**
 * Runs before Minecraft on iOS (based on PojavLauncher / Amethyst-iOS, GPL-3.0).
 * Usage: IOSBoot &lt;main class&gt; [game args...]
 */
public final class IOSBoot {
    private IOSBoot() {
    }

    public static void main(String[] args) throws Throwable {
        // Skip com.apple.eawt.Application.nativeInitializeApplicationDelegate()
        Beans.setDesignTime(true);
        try {
            // macOS-only code paths are unavailable on iOS, make them use the Linux ones instead
            Class<?> app = Class.forName("com.apple.eawt.Application");
            app.getMethod("getApplication").invoke(null);
            Field field = app.getDeclaredField("sApplication");
            field.setAccessible(true);
            field.set(null, null);
            Class<?> fontUtilities = Class.forName("sun.font.FontUtilities");
            Field isLinux = fontUtilities.getDeclaredField("isLinux");
            isLinux.setAccessible(true);
            isLinux.setBoolean(null, true);
            System.setProperty("java.util.prefs.PreferencesFactory", "java.util.prefs.FileSystemPreferencesFactory");
        } catch (Throwable th) {
            // Only possible on Java 8
        }

        Thread.currentThread().setUncaughtExceptionHandler((t, th) -> {
            th.printStackTrace();
            System.exit(1);
        });

        try {
            Class.forName("com.github.caciocavallosilano.cacio.ctc.CTCPreloadClassLoader");
        } catch (ClassNotFoundException e) {
            // Java 8 uses the old Caciocavallo
        }

        System.setProperty("glfw.windowSize", System.getProperty("cacio.managed.screensize"));
        System.setProperty("org.lwjgl.vulkan.libname", "libMoltenVK.dylib");
        disableForgeSplash();

        if (args.length == 0) {
            throw new IllegalArgumentException("Missing main class");
        }
        String mainClass = args[0];
        String[] gameArgs = Arrays.copyOfRange(args, 1, args.length);
        System.out.println("[MaoX] Starting " + mainClass);

        Class<?> clazz = Class.forName(mainClass, true, ClassLoader.getSystemClassLoader());
        Method main = clazz.getMethod("main", String[].class);
        try {
            main.invoke(null, (Object) gameArgs);
        } catch (InvocationTargetException e) {
            throw e.getCause();
        }
    }

    private static void disableForgeSplash() {
        try {
            File file = new File(System.getProperty("user.dir"), "config/splash.properties");
            file.getParentFile().mkdirs();
            String content = file.exists()
                    ? new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8).replace("enabled=true", "enabled=false")
                    : "enabled=false";
            try (FileOutputStream out = new FileOutputStream(file)) {
                out.write(content.getBytes(StandardCharsets.UTF_8));
            }
        } catch (Throwable th) {
            th.printStackTrace();
        }
    }
}

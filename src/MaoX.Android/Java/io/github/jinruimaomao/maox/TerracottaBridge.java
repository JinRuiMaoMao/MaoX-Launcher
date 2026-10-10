package io.github.jinruimaomao.maox;

import android.content.Context;
import android.content.Intent;
import android.net.VpnService;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.util.concurrent.atomic.AtomicLong;

/**
 * 陶瓦联机安卓版（libterracotta.so，https://github.com/burningtnt/Terracotta）的 JNI 接口。
 * 原生库在 JNI_OnLoad 里按系统属性 net.burningtnt.terracotta.native_location 找到这个类并注册下面的 native 方法；
 * EasyTier 需要虚拟网卡时回调 onVpnServiceStateChanged，由 TerracottaVpnService 建立 VPN 后把文件描述符交回去。
 */
public final class TerracottaBridge {
    private static final String TAG = "MaoXTerracotta";
    private static final long FD_PENDING = ((long) Integer.MAX_VALUE) + 1;
    private static final long FD_REJECT = FD_PENDING + 1;

    private static Context sContext;
    private static RandomAccessFile sLog;
    private static String sMetadata;

    private static final AtomicLong sFd = new AtomicLong(FD_PENDING);
    private static volatile InetAddress sAddress;
    private static volatile short sPrefix;
    private static volatile String sRoutes;
    private static ParcelFileDescriptor sConnection;

    private TerracottaBridge() {
    }

    /** 加载原生库并启动后台，返回 "陶瓦版本\0编译时间\0EasyTier 版本"。重复调用直接返回。 */
    public static synchronized String start(Context context, String libraryPath, String dataDir) throws IOException {
        if (sMetadata != null) return sMetadata;
        System.setProperty("net.burningtnt.terracotta.native_location",
                TerracottaBridge.class.getName().replace('.', '/'));
        System.load(libraryPath);
        File root = new File(dataDir);
        File base = new File(root, "rs");
        if (!base.isDirectory() && !base.mkdirs()) throw new IOException("Cannot create " + base);
        sLog = new RandomAccessFile(new File(root, "application.log"), "rw");
        sLog.setLength(0);
        int fd = ParcelFileDescriptor.dup(sLog.getFD()).detachFd();
        int code = start0(base.getAbsolutePath(), fd);
        if (code != 0) throw new IOException("Terracotta start failed: " + code);
        sContext = context.getApplicationContext();
        sMetadata = getMetadata0();
        return sMetadata;
    }

    public static boolean isStarted() {
        return sMetadata != null;
    }

    public static String getState() {
        return getState0();
    }

    public static void setWaiting() {
        setWaiting0();
    }

    public static void setScanning(String player) {
        setScanning0(null, player);
    }

    /** 邀请码无效时返回 false。 */
    public static boolean setGuesting(String room, String player) {
        return setGuesting0(room, player);
    }

    // EasyTier 要建立虚拟网卡时在后台线程回调，必须在 30 秒内返回 VPN 的文件描述符
    @SuppressWarnings("unused")
    private static int onVpnServiceStateChanged(byte ip1, byte ip2, byte ip3, byte ip4, short prefix, String routes)
            throws UnknownHostException {
        sAddress = InetAddress.getByAddress(new byte[]{ip1, ip2, ip3, ip4});
        sPrefix = prefix;
        sRoutes = routes;
        sFd.set(FD_PENDING);
        sContext.startService(new Intent(sContext, TerracottaVpnService.class));
        long start = System.currentTimeMillis();
        while (true) {
            long value = sFd.get();
            if (value == FD_REJECT) throw new IllegalStateException("VPN rejected");
            if (value != FD_PENDING) return (int) value;
            if (System.currentTimeMillis() - start > 30000) {
                Log.e(TAG, "VPN request timed out");
                throw new IllegalStateException("VPN request timed out");
            }
            try {
                Thread.sleep(20);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new IllegalStateException(e);
            }
        }
    }

    /** 由 TerracottaVpnService 调用：按 EasyTier 给的地址和路由建立 VPN。 */
    static synchronized void fulfill(VpnService.Builder builder) {
        if (sAddress == null) {
            sFd.set(FD_REJECT);
            return;
        }
        builder.setSession("MaoX Launcher")
                .addAddress(sAddress, sPrefix)
                .addDnsServer("223.5.5.5")
                .addDnsServer("114.114.114.114");
        String routes = sRoutes;
        if (routes != null && !routes.isEmpty()) {
            for (String part : routes.split("\0")) {
                String[] parts = part.split("/", 3);
                if (parts.length == 2) builder.addRoute(parts[0], Integer.parseInt(parts[1]));
            }
        }
        ParcelFileDescriptor connection = builder.establish();
        if (connection == null) {
            sFd.set(FD_REJECT);
            return;
        }
        // 上一次的虚拟网卡已经不用了（EasyTier 重新申请时会先退出）
        closeConnection();
        sConnection = connection;
        sFd.set(connection.getFd());
    }

    static void reject() {
        sFd.set(FD_REJECT);
    }

    static synchronized void closeConnection() {
        if (sConnection != null) {
            try {
                sConnection.close();
            } catch (IOException ignored) {
            }
            sConnection = null;
        }
    }

    private static native int start0(String baseDir, int loggingFd);

    private static native String getState0();

    private static native void setWaiting0();

    private static native void setScanning0(String room, String player);

    private static native boolean setGuesting0(String room, String player);

    private static native int verifyRoomCode0(String room);

    private static native String getMetadata0();

    private static native long prepareExportLogs0();

    private static native void finishExportLogs0(long pointer);

    private static native void panic0();
}

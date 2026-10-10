package io.github.jinruimaomao.maox;

import android.content.Intent;
import android.net.VpnService;
import android.util.Log;

/** 陶瓦联机（EasyTier）用的虚拟网卡。需要用户先通过 VpnService.prepare 授权。 */
public class TerracottaVpnService extends VpnService {
    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        try {
            TerracottaBridge.fulfill(new Builder());
        } catch (Throwable t) {
            Log.e("MaoXTerracotta", "Cannot establish VPN", t);
            TerracottaBridge.reject();
        }
        return START_NOT_STICKY;
    }

    @Override
    public void onRevoke() {
        TerracottaBridge.closeConnection();
        stopSelf();
        super.onRevoke();
    }
}

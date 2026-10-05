package org.kenjinx.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.Binder
import android.os.IBinder
import androidx.core.app.NotificationCompat
import org.kenjinx.android.MainActivity
import org.kenjinx.android.R
import org.kenjinx.android.KenjinxNative
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.Future
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Foreground service for stable emulation in the background.
 * Manifest: android:foregroundServiceType="mediaPlayback"
 */
class EmulationService : Service() {

    companion object {
        private const val CHANNEL_ID = "kenjinx_emulation"
        private const val NOTIF_ID = 42
        const val ACTION_STOPPED = "org.kenjinx.android.action.EMULATION_SERVICE_STOPPED"
    }

    inner class LocalBinder : Binder() {
        fun startEmulation(runLoopBlock: () -> Unit) = this@EmulationService.startEmulation(runLoopBlock)
        fun stopEmulation(onStop: () -> Unit) = this@EmulationService.stopEmulation(onStop)
        fun shutdownService() = this@EmulationService.shutdownService()
    }

    private val binder = LocalBinder()
    private lateinit var executor: ExecutorService
    private var future: Future<*>? = null
    private val running = AtomicBoolean(false)
    // Only if an emulation was actually running are we allowed to perform a native "hard close"
    private val startedOnce = AtomicBoolean(false)

    override fun onCreate() {
        super.onCreate()
        executor = Executors.newSingleThreadExecutor { r ->
            Thread(r, "Kenjinx-Emu").apply {
                isDaemon = false
                priority = Thread.NORM_PRIORITY + 2
            }
        }
        createNotificationChannel()
        startForeground(NOTIF_ID, buildNotification())
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_NOT_STICKY
    override fun onBind(intent: Intent?): IBinder = binder

    override fun onUnbind(intent: Intent?): Boolean {
        if (!running.get()) {
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
        return super.onUnbind(intent)
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        super.onTaskRemoved(rootIntent)
        try { future?.cancel(true) } catch (_: Throwable) {}
        // Only close if it has been started previously
        hardCloseNativeIfStarted("onTaskRemoved")
        running.set(false)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        try { sendBroadcast(Intent(ACTION_STOPPED).setPackage(packageName)) } catch (_: Throwable) {}
    }

    override fun onDestroy() {
        super.onDestroy()
        try { future?.cancel(true) } catch (_: Throwable) {}
        hardCloseNativeIfStarted("onDestroy")
        running.set(false)
        stopForeground(STOP_FOREGROUND_REMOVE)
        try { sendBroadcast(Intent(ACTION_STOPPED).setPackage(packageName)) } catch (_: Throwable) {}
    }

    // ---- Control via Binder ----

    private fun startEmulation(runLoopBlock: () -> Unit) {
        // Allow only one RunLoop
        if (!running.compareAndSet(false, true)) return

        future = executor.submit {
            try {
                // *** No more pre-flight hard close! *** (crashes on the very first launch)
                startedOnce.set(true)
                runLoopBlock()   // Blocked until emulation ends
            } finally {
                startedOnce.set(false)
                running.set(false)
            }
        }
    }

    private fun stopEmulation(onStop: () -> Unit) {
        executor.execute {
            try {
                try { onStop() } catch (_: Throwable) {}
            } finally {
                try { future?.cancel(true) } catch (_: Throwable) {}
                hardCloseNativeIfStarted("stopEmulation")
                running.set(false)
            }
        }
    }

    private fun shutdownService() {
        try { future?.cancel(true) } catch (_: Throwable) {}
        hardCloseNativeIfStarted("shutdownService")
        running.set(false)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    // ---- Native cleanup only if it has been started ----
    private fun hardCloseNativeIfStarted(reason: String) {
        if (!startedOnce.get()) return
        try { KenjinxNative.detachWindow() } catch (_: Throwable) {}
        try { KenjinxNative.deviceCloseEmulation() } catch (_: Throwable) {}
        // Do NOT call graphicsSetPresentEnabled(false) here – it leads to a NullPointerException in VulkanRenderer.ReleaseSurface() on a cold start
        // android.util.Log.d("EmuService", "hardCloseNativeIfStarted: $reason")
    }

    // ---- Notification ----

    private fun buildNotification(): Notification {
        val openIntent = Intent(this, MainActivity::class.java)
        val flags = PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        val pi = PendingIntent.getActivity(this, 0, openIntent, flags)

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setOngoing(true)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(getString(R.string.app_name))
            .setContentText("Emulation is running…")
            .setContentIntent(pi)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun createNotificationChannel() {
        val mgr = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        val ch = NotificationChannel(
            CHANNEL_ID,
            "Emulation",
            NotificationManager.IMPORTANCE_LOW
        )
        ch.description = "Keeps the Emulation active."
        mgr.createNotificationChannel(ch)
    }
}

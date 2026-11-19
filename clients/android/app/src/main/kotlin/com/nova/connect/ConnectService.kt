package com.nova.connect

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.os.Binder
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper

class ConnectService : Service() {
    private val binder = LocalBinder()
    private val mainHandler = Handler(Looper.getMainLooper())
    private val listeners = mutableSetOf<ConnectListener>()
    private var engine: LocalTransferEngine? = null
    private var currentStatus = "NOVA Connect is available on this network."
    private var lastForegroundUpdate = 0L

    private val engineListener = object : ConnectListener {
        override fun onPeersChanged() = publish { it.onPeersChanged() }

        override fun onIncomingRequest(request: IncomingRequest) {
            showIncomingNotification(request)
            publish { it.onIncomingRequest(request) }
        }

        override fun onTransferChanged(row: TransferRow) {
            if (row.status != "Waiting for approval") {
                (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(row.transferId.hashCode())
            }
            val now = System.currentTimeMillis()
            if (now - lastForegroundUpdate >= NOTIFICATION_UPDATE_INTERVAL_MILLIS || !row.isActive) {
                lastForegroundUpdate = now
                updateForegroundNotification(row)
            }
            publish { it.onTransferChanged(row) }
        }

        override fun onStatusChanged(status: String) {
            currentStatus = status
            updateForegroundNotification(null)
            publish { it.onStatusChanged(status) }
        }
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannels()
        startInForeground(buildForegroundNotification(null))
        try {
            engine = LocalTransferEngine(applicationContext).also {
                it.addListener(engineListener)
                it.start()
                currentStatus = it.status
            }
        } catch (exception: Exception) {
            currentStatus = "Local transfer unavailable: ${exception.message ?: exception.javaClass.simpleName}"
        }
        updateForegroundNotification(null)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_ACCEPT_INCOMING ->
                intent.getStringExtra(EXTRA_TRANSFER_ID)?.let {
                    engine?.respondToIncoming(it, true)
                    cancelIncomingNotification(it)
                }
            ACTION_REJECT_INCOMING ->
                intent.getStringExtra(EXTRA_TRANSFER_ID)?.let {
                    engine?.respondToIncoming(it, false)
                    cancelIncomingNotification(it)
                }
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onDestroy() {
        engine?.removeListener(engineListener)
        engine?.close()
        engine = null
        mainHandler.removeCallbacksAndMessages(null)
        super.onDestroy()
    }

    inner class LocalBinder : Binder() {
        fun service(): ConnectService = this@ConnectService
    }

    fun localEngine(): LocalTransferEngine? = engine
    val statusMessage: String get() = currentStatus

    fun addListener(listener: ConnectListener) {
        listeners.add(listener)
    }

    fun removeListener(listener: ConnectListener) {
        listeners.remove(listener)
    }

    private fun publish(action: (ConnectListener) -> Unit) {
        mainHandler.post {
            listeners.toList().forEach { listener -> runCatching { action(listener) } }
        }
    }

    private fun createNotificationChannels() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_SERVICE, "NOVA Connect", NotificationManager.IMPORTANCE_LOW)
                .apply { description = "Local file transfer status." }
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_INCOMING, "Incoming NOVA transfers", NotificationManager.IMPORTANCE_HIGH)
                .apply { description = "Incoming local file transfer approval requests." }
        )
    }

    private fun startInForeground(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
            startForeground(FOREGROUND_NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        else
            startForeground(FOREGROUND_NOTIFICATION_ID, notification)
    }

    private fun buildForegroundNotification(row: TransferRow?): Notification {
        val openIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val builder = Notification.Builder(this, CHANNEL_SERVICE)
        builder
            .setSmallIcon(android.R.drawable.stat_sys_upload_done)
            .setContentTitle(row?.let { "NOVA · ${it.fileName}" } ?: "NOVA Connect")
            .setContentText(row?.let { transferNotificationText(it) } ?: currentStatus)
            .setContentIntent(openIntent)
            .setOngoing(row?.isActive ?: true)
            .setOnlyAlertOnce(true)
        if (row?.isActive == true && row.totalBytes > 0) {
            builder.setProgress(100, row.progress, false)
        }
        return builder.build()
    }

    private fun updateForegroundNotification(row: TransferRow?) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(FOREGROUND_NOTIFICATION_ID, buildForegroundNotification(row))
    }

    private fun showIncomingNotification(request: IncomingRequest) {
        val transferId = request.offer.transferId
        val open = PendingIntent.getActivity(
            this,
            transferId.hashCode(),
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val accept = acceptPendingIntent(transferId)
        val reject = decisionPendingIntent(transferId)
        val builder = Notification.Builder(this, CHANNEL_INCOMING)
        val notification = builder
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("File from ${request.peerName}")
            .setContentText("${request.offer.fileName} · ${formatBytes(request.offer.sizeBytes)}")
            .setContentIntent(open)
            .setAutoCancel(true)
            .addAction(Notification.Action.Builder(Icon.createWithResource(this, android.R.drawable.checkbox_on_background), "Accept", accept).build())
            .addAction(Notification.Action.Builder(Icon.createWithResource(this, android.R.drawable.ic_menu_close_clear_cancel), "Decline", reject).build())
            .build()
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
            .notify(transferId.hashCode(), notification)
    }

    private fun decisionPendingIntent(transferId: String): PendingIntent {
        val intent = Intent(this, ConnectService::class.java)
            .setAction(ACTION_REJECT_INCOMING)
            .putExtra(EXTRA_TRANSFER_ID, transferId)
        return PendingIntent.getService(
            this,
            transferId.hashCode() xor 0x52,
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
    }

    private fun acceptPendingIntent(transferId: String): PendingIntent {
        val intent = Intent(this, MainActivity::class.java)
            .setAction(ACTION_ACCEPT_INCOMING)
            .putExtra(EXTRA_TRANSFER_ID, transferId)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        return PendingIntent.getActivity(
            this,
            transferId.hashCode() xor 0x41,
            intent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
    }

    private fun cancelIncomingNotification(transferId: String) {
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager).cancel(transferId.hashCode())
    }

    private fun transferNotificationText(row: TransferRow): String =
        "${row.status} · ${row.progress}% · ${formatBytes(row.bytesPerSecond)}/s"

    private fun formatBytes(value: Long): String {
        if (value < 1_000) return "$value B"
        val units = arrayOf("KB", "MB", "GB", "TB")
        var amount = value.toDouble()
        var unit = -1
        while (amount >= 1_000 && unit < units.lastIndex) {
            amount /= 1_000
            unit++
        }
        return "%.1f %s".format(java.util.Locale.ROOT, amount, units[unit])
    }

    companion object {
        const val ACTION_ACCEPT_INCOMING = "com.nova.connect.ACCEPT_INCOMING"
        const val ACTION_REJECT_INCOMING = "com.nova.connect.REJECT_INCOMING"
        const val EXTRA_TRANSFER_ID = "com.nova.connect.TRANSFER_ID"
        private const val CHANNEL_SERVICE = "nova-connect-service"
        private const val CHANNEL_INCOMING = "nova-connect-incoming"
        private const val FOREGROUND_NOTIFICATION_ID = 459_840
        private const val NOTIFICATION_UPDATE_INTERVAL_MILLIS = 750L

        fun start(context: Context) {
            val intent = Intent(context, ConnectService::class.java)
            context.startForegroundService(intent)
        }
    }
}

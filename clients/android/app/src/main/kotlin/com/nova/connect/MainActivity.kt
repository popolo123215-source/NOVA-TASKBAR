package com.nova.connect

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.net.Inet4Address
import java.net.InetAddress
import java.io.IOException

class MainActivity : Activity() {
    private val mainHandler = Handler(Looper.getMainLooper())
    private lateinit var content: LinearLayout
    private lateinit var deviceRows: LinearLayout
    private lateinit var discoveryRefreshButton: Button
    private lateinit var incomingRows: LinearLayout
    private lateinit var transferRows: LinearLayout
    private lateinit var statusText: TextView
    private lateinit var selectedPeerText: TextView
    private lateinit var deviceNameText: TextView
    private lateinit var pairingCodeText: TextView
    private lateinit var endpointText: TextView
    private lateinit var connectionDetailsPanel: LinearLayout
    private lateinit var manualConnectionPanel: LinearLayout
    private lateinit var manualConnectionToggle: Button
    private lateinit var manualAddress: EditText
    private lateinit var manualPort: EditText
    private lateinit var manualPairingCode: EditText
    private var service: ConnectService? = null
    private var engine: LocalTransferEngine? = null
    private var selectedPeerId: String? = null
    private var pendingFiles: List<Uri> = emptyList()
    private var pendingApprovalAfterFolder: String? = null
    private val shownIncomingDialogs = HashSet<String>()
    private var bound = false
    private var resumed = false
    private var notificationPermissionDenied = false

    private val serviceConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            val connectedService = (binder as? ConnectService.LocalBinder)?.service() ?: return
            service = connectedService
            engine = connectedService.localEngine()
            connectedService.addListener(connectListener)
            renderDynamic()
            handleIncomingAction(intent)
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            service?.removeListener(connectListener)
            service = null
            engine = null
            statusText.text = "NOVA Connect service disconnected."
        }
    }

    private val connectListener = object : ConnectListener {
        override fun onPeersChanged() {
            mainHandler.post { renderDynamic() }
        }

        override fun onIncomingRequest(request: IncomingRequest) {
            mainHandler.post { showIncomingDialog(request) }
        }

        override fun onTransferChanged(row: TransferRow) {
            if (row.status != "Waiting for approval") shownIncomingDialogs.remove(row.transferId)
            mainHandler.removeCallbacks(renderRunnable)
            mainHandler.postDelayed(renderRunnable, 250)
        }

        override fun onStatusChanged(status: String) {
            mainHandler.post {
                statusText.text = status
                renderDynamic()
            }
        }
    }

    private val renderRunnable = Runnable { renderDynamic() }
    private val pollRunnable = object : Runnable {
        override fun run() {
            renderDynamic()
            mainHandler.postDelayed(this, UI_REFRESH_INTERVAL_MILLIS)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        buildLayout()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQUEST_NOTIFICATIONS)
        }
        ConnectService.start(this)
        handleIncomingAction(intent)
    }

    override fun onStart() {
        super.onStart()
        bound = bindService(Intent(this, ConnectService::class.java), serviceConnection, Context.BIND_AUTO_CREATE)
        mainHandler.post(pollRunnable)
    }

    override fun onResume() {
        super.onResume()
        resumed = true
    }

    override fun onPause() {
        resumed = false
        super.onPause()
    }

    @Deprecated("Permission result is handled for the notification permission only.")
    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQUEST_NOTIFICATIONS &&
            grantResults.firstOrNull() != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermissionDenied = true
            statusText.text = notificationPermissionWarning()
        } else if (requestCode == REQUEST_NOTIFICATIONS) {
            notificationPermissionDenied = false
            renderDynamic()
        }
    }

    override fun onStop() {
        mainHandler.removeCallbacks(pollRunnable)
        mainHandler.removeCallbacks(renderRunnable)
        if (bound) {
            service?.removeListener(connectListener)
            unbindService(serviceConnection)
            bound = false
        }
        super.onStop()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIncomingAction(intent)
    }

    @SuppressLint("WrongConstant")
    @Deprecated("Activity result API kept dependency-free for this native-only client.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQUEST_FILES) {
            if (resultCode != RESULT_OK || data == null) return
            val selected = mutableListOf<Uri>()
            data.clipData?.let { clip ->
                for (index in 0 until clip.itemCount) selected.add(clip.getItemAt(index).uri)
            }
            data.data?.let { if (it !in selected) selected.add(it) }
            if (selected.isEmpty()) {
                showMessage("No files were selected.")
                return
            }
            val flags = data.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            selected.forEach { uri ->
                runCatching { contentResolver.takePersistableUriPermission(uri, flags) }
            }
            sendSelectedFiles(selected)
        } else if (requestCode == REQUEST_DESTINATION) {
            val id = pendingApprovalAfterFolder
            pendingApprovalAfterFolder = null
            if (resultCode != RESULT_OK || data?.data == null) {
                id?.let { engine?.respondToIncoming(it, false) }
                showMessage("Download folder selection was cancelled.")
                return
            }
            val uri = data.data!!
            val flags = data.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
            runCatching { contentResolver.takePersistableUriPermission(uri, flags) }
                .onFailure {
                    id?.let { transferId -> engine?.respondToIncoming(transferId, false) }
                    showMessage("Could not keep access to that folder: ${it.message}")
                    return
                }
            engine?.setDestinationTree(uri)
            if (id != null) {
                engine?.respondToIncoming(id, true)
            }
            renderDynamic()
        }
    }

    private fun buildLayout() {
        val scroll = ScrollView(this).apply {
            setBackgroundColor(COLOR_BACKGROUND)
            isFillViewport = true
        }
        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(18), dp(18), dp(28))
        }
        scroll.addView(content)
        setContentView(scroll)

        content.addView(text("NOVA Connect", 26, COLOR_TEXT, bold = true))
        content.addView(text("Direct file transfer · same local Wi-Fi/LAN", 13, COLOR_MUTED).apply {
            setPadding(0, dp(4), 0, dp(12))
        })
        statusText = text("Starting secure local transfer…", 12, COLOR_ACCENT)
        content.addView(statusText)

        addSectionTitle("This Android device")
        val identityBox = card()
        content.addView(identityBox)
        deviceNameText = text("Searching for nearby NOVA devices…", 12, COLOR_MUTED)
        identityBox.addView(deviceNameText)
        val detailsToggle = button("Pairing and network details") {
            val expanded = connectionDetailsPanel.visibility != View.VISIBLE
            connectionDetailsPanel.visibility = if (expanded) View.VISIBLE else View.GONE
        }
        identityBox.addView(detailsToggle)
        connectionDetailsPanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
        }
        identityBox.addView(connectionDetailsPanel)
        connectionDetailsPanel.addView(text("Pairing code — compare over a trusted channel", 12, COLOR_MUTED))
        pairingCodeText = text("Starting…", 11, COLOR_TEXT).apply {
            setTextIsSelectable(true)
            typeface = Typeface.MONOSPACE
            setPadding(0, dp(6), 0, dp(6))
        }
        connectionDetailsPanel.addView(pairingCodeText)
        connectionDetailsPanel.addView(button("Copy pairing code") {
            engine?.let {
                val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
                clipboard.setPrimaryClip(android.content.ClipData.newPlainText("NOVA pairing code", it.pairingCode))
                showMessage("NOVA pairing code copied.")
            }
        })
        endpointText = text("", 12, COLOR_MUTED).apply { setPadding(0, dp(8), 0, 0) }
        connectionDetailsPanel.addView(endpointText)

        addSectionTitle("Download destination")
        val destinationBox = card()
        content.addView(destinationBox)
        destinationBox.addView(text("", 12, COLOR_MUTED).also { destinationLabel = it })
        destinationBox.addView(button("Choose download folder") { launchFolderPicker(null) })

        val discoveryHeader = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(18), 0, dp(8))
        }
        discoveryHeader.addView(
            text("Nearby devices · automatic search", 17, COLOR_TEXT, bold = true),
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
        )
        discoveryRefreshButton = button("Refresh") { refreshNearbyDevices() }.apply {
            contentDescription = "Refresh nearby device search"
            setPadding(dp(10), 0, dp(10), 0)
        }
        discoveryHeader.addView(
            discoveryRefreshButton,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(40))
        )
        content.addView(discoveryHeader)
        deviceRows = card()
        content.addView(deviceRows)
        selectedPeerText = text("Select a nearby device. Secure pairing is required once.", 12, COLOR_MUTED).apply {
            setPadding(0, dp(8), 0, dp(8))
        }
        content.addView(selectedPeerText)
        content.addView(button("Send files to selected device") { launchFilePicker() })

        manualConnectionToggle = button("Advanced: connect manually") {
            val expanded = manualConnectionPanel.visibility != View.VISIBLE
            manualConnectionPanel.visibility = if (expanded) View.VISIBLE else View.GONE
            manualConnectionToggle.text = if (expanded) "Hide manual connection" else "Advanced: connect manually"
        }
        content.addView(manualConnectionToggle)
        manualConnectionPanel = card().apply { visibility = View.GONE }
        content.addView(manualConnectionPanel)
        manualConnectionPanel.addView(text("Only use this if automatic search cannot find the Windows PC.", 12, COLOR_MUTED))
        manualAddress = edit("Windows IPv4 address", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI)
        manualPort = edit("TCP port", InputType.TYPE_CLASS_NUMBER)
        manualPairingCode = edit("NOVA1 pairing code", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE)
        manualConnectionPanel.addView(manualAddress)
        manualConnectionPanel.addView(manualPort)
        manualConnectionPanel.addView(manualPairingCode)
        manualConnectionPanel.addView(button("Add and pair device") { addManualPeer() })

        addSectionTitle("Incoming transfers · approve each file")
        incomingRows = card()
        content.addView(incomingRows)

        addSectionTitle("Progress and transfer history")
        transferRows = card()
        content.addView(transferRows)

        destinationLabel.text = destinationDescription()
    }

    private lateinit var destinationLabel: TextView

    private fun renderDynamic() {
        if (!::deviceRows.isInitialized || isFinishing || isDestroyed) return
        val core = engine
        if (core == null) {
            statusText.text = service?.statusMessage ?: "Local transfer service is not ready."
            return
        }
        statusText.text = if (notificationPermissionDenied) notificationPermissionWarning() else core.status
        deviceNameText.text = "Visible to nearby devices as: ${core.identity.displayName}"
        pairingCodeText.text = core.pairingCode
        endpointText.text = "Local endpoint: ${core.localEndpoint}"
        destinationLabel.text = destinationDescription()
        renderPeers(core)
        renderIncoming(core)
        renderTransfers(core)
    }

    private fun refreshNearbyDevices() {
        val core = engine
        if (core == null) {
            showMessage("NOVA Connect service is not ready yet.")
            return
        }
        discoveryRefreshButton.isEnabled = false
        statusText.text = "Refreshing nearby device search…"
        Thread({
            try {
                core.refreshDiscovery()
                mainHandler.post { finishDiscoveryRefresh(null) }
            } catch (exception: IOException) {
                mainHandler.post { finishDiscoveryRefresh(exception.message ?: "Network discovery failed.") }
            } catch (exception: IllegalStateException) {
                mainHandler.post { finishDiscoveryRefresh(exception.message ?: "Network discovery is unavailable.") }
            } catch (exception: SecurityException) {
                mainHandler.post { finishDiscoveryRefresh(exception.message ?: "Network discovery permission was denied.") }
            }
        }, "NOVA-discovery-refresh").apply {
            isDaemon = true
            start()
        }
    }

    private fun finishDiscoveryRefresh(error: String?) {
        if (isFinishing || isDestroyed) return
        discoveryRefreshButton.isEnabled = true
        if (error == null) {
            renderDynamic()
            showMessage("Nearby device search refreshed.")
        } else {
            statusText.text = "Could not refresh nearby device search: $error"
            showMessage(statusText.text.toString())
        }
    }

    private fun renderPeers(core: LocalTransferEngine) {
        deviceRows.removeAllViews()
        val peers = core.peers()
        if (peers.isEmpty()) {
            deviceRows.addView(text("Searching automatically. Keep NOVA running on the Windows PC and connect both devices to the same Wi-Fi/LAN.", 13, COLOR_MUTED))
            selectedPeerId = null
            selectedPeerText.text = "When a device appears, select it to send files."
            return
        }

        var selected: NovaPeer? = null
        peers.forEach { peer ->
            val isSelected = selectedPeerId == peer.deviceId
            if (isSelected) selected = peer
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(dp(8), dp(8), dp(8), dp(8))
                background = rounded(if (isSelected) COLOR_SELECTED else COLOR_ROW, 10)
            }
            row.addView(text(peer.displayName, 15, COLOR_TEXT, bold = true))
            val state = when {
                peer.identityChanged -> "Identity changed · remove old pairing before trusting"
                peer.online && peer.trusted -> "Online · paired"
                peer.online -> "Online · not paired"
                peer.trusted -> "Offline · paired"
                else -> "Saved device · manual connection required"
            }
            row.addView(text(state, 11, if (peer.identityChanged) COLOR_ERROR else COLOR_MUTED).apply {
                setPadding(0, dp(2), 0, dp(2))
            })
            val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            actions.addView(button(if (isSelected) "Selected" else "Select") {
                selectedPeerId = peer.deviceId
                renderDynamic()
            }, weight = 1f)
            if (!peer.trusted) {
                actions.addView(button("Pair") { confirmTrust(peer) }, weight = 1f)
            } else {
                actions.addView(button("Remove trust") {
                    AlertDialog.Builder(this@MainActivity)
                        .setTitle("Remove paired device?")
                        .setMessage("NOVA will no longer accept this device's file transfers.")
                        .setNegativeButton("Keep", null)
                        .setPositiveButton("Remove") { _, _ ->
                            core.removeTrustedPeer(peer.deviceId)
                            renderDynamic()
                        }
                        .show()
                }, weight = 1f)
            }
            row.addView(actions)
            deviceRows.addView(row, verticalSpace(8))
        }

        val current = selected ?: peers.firstOrNull { it.deviceId == selectedPeerId }
        if (current != null) {
            selectedPeerId = current.deviceId
            selectedPeerText.text = "Selected: ${current.displayName} · ${if (current.trusted && !current.identityChanged) "ready to send" else "pair once before sending"}"
        } else if (selectedPeerId != null) {
            selectedPeerId = null
            selectedPeerText.text = "Select a nearby device to send files."
        }
    }

    private fun renderIncoming(core: LocalTransferEngine) {
        incomingRows.removeAllViews()
        val requests = core.pendingRequests()
        if (requests.isEmpty()) {
            incomingRows.addView(text("No incoming requests.", 12, COLOR_MUTED))
            return
        }
        requests.forEach { request ->
            val offer = request.offer
            val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            box.addView(text("${request.peerName} wants to send", 13, COLOR_TEXT, bold = true))
            box.addView(text("${offer.fileName} · ${formatBytes(offer.sizeBytes)}", 12, COLOR_MUTED))
            val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            actions.addView(button("Accept") { acceptIncoming(request) }, weight = 1f)
            actions.addView(button("Decline") {
                core.respondToIncoming(offer.transferId, false)
                shownIncomingDialogs.remove(offer.transferId)
            }, weight = 1f)
            box.addView(actions)
            incomingRows.addView(box, verticalSpace(8))
        }
        requests.forEach { showIncomingDialog(it) }
    }

    private fun renderTransfers(core: LocalTransferEngine) {
        transferRows.removeAllViews()
        val rows = core.transferRows().take(40)
        if (rows.isEmpty()) {
            transferRows.addView(text("No transfers yet.", 12, COLOR_MUTED))
            return
        }
        rows.forEach { transfer ->
            val item = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            item.addView(text("${transfer.direction} · ${transfer.fileName}", 13, COLOR_TEXT, bold = true))
            item.addView(text("${transfer.deviceName} · ${transfer.status}", 11, COLOR_MUTED))
            if (transfer.totalBytes > 0) {
                item.addView(ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
                    max = 100
                    progress = transfer.progress
                    progressTintList = android.content.res.ColorStateList.valueOf(COLOR_ACCENT)
                }, verticalSpace(4))
                item.addView(text(
                    "${formatBytes(transfer.bytesTransferred)} / ${formatBytes(transfer.totalBytes)} · " +
                        "${transfer.progress}% · ${formatBytes(transfer.bytesPerSecond)}/s",
                    10,
                    COLOR_MUTED
                ))
            }
            transfer.error?.let { item.addView(text(it, 11, COLOR_ERROR)) }
            if (transfer.isActive) {
                item.addView(button("Cancel transfer") { core.cancelTransfer(transfer.transferId) })
            }
            transferRows.addView(item, verticalSpace(8))
        }
    }

    private fun confirmTrust(peer: NovaPeer) {
        if (peer.identityChanged) {
            showMessage("The device ID announced a different certificate. Remove the old pairing and compare the new fingerprint before trusting it.")
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Pair ${peer.displayName}?")
            .setMessage(
                "Compare this complete SHA-256 code with the Windows NOVA screen before trusting the device:\n\n" +
                    formatFingerprint(peer.fingerprint) +
                    "\n\nTrust only if every group matches."
            )
            .setNegativeButton("Cancel", null)
            .setPositiveButton("Trust device") { _, _ ->
                runCatching { engine?.trustPeer(peer) }
                .onSuccess {
                    val queuedFiles = pendingFiles
                    pendingFiles = emptyList()
                    if (queuedFiles.isNotEmpty()) sendSelectedFiles(queuedFiles) else renderDynamic()
                }
                    .onFailure { showMessage("Could not pair device: ${it.message}") }
            }
            .show()
    }

    private fun addManualPeer() {
        val ipText = manualAddress.text.toString().trim()
        val port = manualPort.text.toString().trim().toIntOrNull()
        try {
            require(ipText.matches(Regex("""(?:\d{1,3}\.){3}\d{1,3}"""))) {
                "Enter a numeric IPv4 address."
            }
            val address = InetAddress.getByName(ipText)
            require(address is Inet4Address && address.hostAddress == ipText) { "Enter a valid IPv4 address." }
            require(port != null && port in 1..65535) { "Enter the TCP port displayed by NOVA on Windows." }
            val code = PairingCode.decode(manualPairingCode.text.toString())
            val peer = NovaPeer(code.deviceId, code.displayName, address, port, code.fingerprint,
                System.currentTimeMillis(), true)
            AlertDialog.Builder(this)
                .setTitle("Pair ${code.displayName}?")
                .setMessage("Verify this complete SHA-256 fingerprint on the Windows PC:\n\n${formatFingerprint(code.fingerprint)}")
                .setNegativeButton("Cancel", null)
                .setPositiveButton("Trust and add") { _, _ ->
                    runCatching { engine?.addManualPeer(peer) }
                        .onSuccess {
                            selectedPeerId = peer.deviceId
                            manualPairingCode.text.clear()
                            manualConnectionPanel.visibility = View.GONE
                            manualConnectionToggle.text = "Advanced: connect manually"
                            renderDynamic()
                        }
                        .onFailure { showMessage("Could not add device: ${it.message}") }
                }
                .show()
        } catch (exception: Exception) {
            showMessage(exception.message ?: "Invalid manual connection details.")
        }
    }

    private fun launchFilePicker() {
        val core = engine ?: return showMessage("Local transfer service is not ready.")
        val peer = core.peers().firstOrNull { it.deviceId == selectedPeerId }
            ?: return showMessage("Select a nearby or manually added Windows device first.")
        if (peer.identityChanged) return showMessage("The selected device identity changed. Remove and pair it again.")
        if (!peer.trusted) return confirmTrust(peer)
        if (!peer.online && peer.address.isAnyLocalAddress)
            return showMessage("The saved device has no current LAN address. Use Manual connection to add its current IP and port.")

        val intent = Intent(Intent.ACTION_OPEN_DOCUMENT).apply {
            addCategory(Intent.CATEGORY_OPENABLE)
            type = "*/*"
            putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
        }
        try {
            startActivityForResult(intent, REQUEST_FILES)
        } catch (exception: Exception) {
            showMessage("Could not open the file picker: ${exception.message}")
        }
    }

    private fun sendSelectedFiles(uris: List<Uri>) {
        val core = engine ?: return showMessage("Local transfer service is not ready.")
        val peer = core.peers().firstOrNull { it.deviceId == selectedPeerId }
            ?: return showMessage("Select a device before sending files.")
        if (!peer.trusted || peer.identityChanged) {
            pendingFiles = uris
            confirmTrust(peer)
            return
        }
        runCatching { core.sendFiles(peer, uris) }
            .onSuccess {
                showMessage("${it.size} file(s) queued.")
                renderDynamic()
            }
            .onFailure { showMessage("Could not queue files: ${it.message}") }
    }

    private fun acceptIncoming(request: IncomingRequest) {
        val core = engine ?: return
        if (core.destinationTree() == null) {
            AlertDialog.Builder(this)
                .setTitle("Choose download folder")
                .setMessage("Select a destination folder before approving ${request.offer.fileName}.")
                .setNegativeButton("Decline") { _, _ ->
                    core.respondToIncoming(request.offer.transferId, false)
                }
                .setPositiveButton("Choose folder") { _, _ ->
                    pendingApprovalAfterFolder = request.offer.transferId
                    launchFolderPicker(request.offer.transferId)
                }
                .show()
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Accept incoming file?")
            .setMessage(
                "${request.peerName} wants to send:\n\n${request.offer.fileName}\n" +
                    "${formatBytes(request.offer.sizeBytes)}\n\n" +
                    "SHA-256: ${request.offer.sha256}"
            )
            .setNegativeButton("Decline") { _, _ ->
                core.respondToIncoming(request.offer.transferId, false)
            }
            .setPositiveButton("Accept") { _, _ ->
                core.respondToIncoming(request.offer.transferId, true)
            }
            .setOnCancelListener {
                core.respondToIncoming(request.offer.transferId, false)
            }
            .show()
    }

    private fun showIncomingDialog(request: IncomingRequest) {
        if (!resumed || isFinishing || isDestroyed || !shownIncomingDialogs.add(request.offer.transferId)) return
        acceptIncoming(request)
    }

    private fun launchFolderPicker(incomingTransferId: String?) {
        if (incomingTransferId != null) pendingApprovalAfterFolder = incomingTransferId
        try {
            startActivityForResult(
                Intent(Intent.ACTION_OPEN_DOCUMENT_TREE)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION or
                        Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION or Intent.FLAG_GRANT_PREFIX_URI_PERMISSION),
                REQUEST_DESTINATION
            )
        } catch (exception: Exception) {
            pendingApprovalAfterFolder?.let { engine?.respondToIncoming(it, false) }
            pendingApprovalAfterFolder = null
            showMessage("Could not choose a download folder: ${exception.message}")
        }
    }

    private fun handleIncomingAction(intent: Intent?) {
        val transferId = intent?.getStringExtra(ConnectService.EXTRA_TRANSFER_ID) ?: return
        if (intent.action != ConnectService.ACTION_ACCEPT_INCOMING) return
        intent.removeExtra(ConnectService.EXTRA_TRANSFER_ID)
        val core = engine ?: return
        if (core.destinationTree() == null) {
            shownIncomingDialogs.add(transferId)
            pendingApprovalAfterFolder = transferId
            launchFolderPicker(transferId)
        } else {
            shownIncomingDialogs.add(transferId)
            core.respondToIncoming(transferId, true)
        }
    }

    private fun destinationDescription(): String =
        engine?.destinationTree()?.lastPathSegment?.let { "Selected SAF folder: $it" }
            ?: "Choose a folder for incoming files. No broad storage permission is requested."

    private fun notificationPermissionWarning(): String =
        "Enable notifications to approve incoming transfers while NOVA Connect is in the background."

    private fun addSectionTitle(title: String) {
        content.addView(text(title, 17, COLOR_TEXT, bold = true).apply {
            setPadding(0, dp(18), 0, dp(8))
        })
    }

    private fun card(): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(12), dp(12), dp(12), dp(12))
        background = rounded(COLOR_CARD, 14)
    }

    private fun text(value: String, size: Int, color: Int, bold: Boolean = false): TextView =
        TextView(this).apply {
            text = value
            textSize = size.toFloat()
            setTextColor(color)
            if (bold) setTypeface(typeface, Typeface.BOLD)
            gravity = Gravity.CENTER_VERTICAL
        }

    private fun button(label: String, action: () -> Unit): Button =
        Button(this).apply {
            text = label
            textSize = 12f
            setTextColor(COLOR_TEXT)
            background = rounded(COLOR_BUTTON, 10)
            isAllCaps = false
            setOnClickListener { action() }
        }

    private fun edit(hint: String, inputType: Int): EditText =
        EditText(this).apply {
            this.hint = hint
            this.inputType = inputType
            textSize = 13f
            setTextColor(COLOR_TEXT)
            setHintTextColor(COLOR_MUTED)
            setPadding(dp(10), dp(5), dp(10), dp(5))
            background = rounded(COLOR_ROW, 8)
            if (hint == "NOVA1 pairing code") {
                minLines = 2
                maxLines = 3
                isSingleLine = false
            }
        }

    private fun rounded(color: Int, radiusDp: Int): GradientDrawable =
        GradientDrawable().apply {
            setColor(color)
            cornerRadius = dp(radiusDp).toFloat()
        }

    private fun verticalSpace(dp: Int): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            .apply { bottomMargin = this@MainActivity.dp(dp) }

    private fun LinearLayout.addView(view: View, weight: Float) {
        addView(view, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, weight))
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private fun showMessage(message: String) {
        Toast.makeText(this, message, Toast.LENGTH_LONG).show()
    }

    private fun formatFingerprint(value: String): String =
        PeerStore.normalizeFingerprint(value).chunked(4).joinToString("-")

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
        private const val REQUEST_FILES = 101
        private const val REQUEST_DESTINATION = 102
        private const val REQUEST_NOTIFICATIONS = 103
        private const val UI_REFRESH_INTERVAL_MILLIS = 1_000L
        private const val COLOR_BACKGROUND = 0xFF11131A.toInt()
        private const val COLOR_CARD = 0xFF1B1E28.toInt()
        private const val COLOR_ROW = 0xFF242835.toInt()
        private const val COLOR_BUTTON = 0xFF4A4165.toInt()
        private const val COLOR_SELECTED = 0xFF33304A.toInt()
        private const val COLOR_TEXT = 0xFFF5F7FF.toInt()
        private const val COLOR_MUTED = 0xFF9AA4B8.toInt()
        private const val COLOR_ACCENT = 0xFFC0B0FF.toInt()
        private const val COLOR_ERROR = 0xFFFF9A9A.toInt()
    }
}

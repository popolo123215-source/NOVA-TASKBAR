package com.nova.connect

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.io.InterruptedIOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.security.KeyStore
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

interface ConnectListener {
    fun onPeersChanged() {}
    fun onIncomingRequest(request: IncomingRequest) {}
    fun onTransferChanged(row: TransferRow) {}
    fun onStatusChanged(status: String) {}
}

internal fun compatibleTlsProtocols(supportedProtocols: Array<String>): Array<String> {
    require("TLSv1.2" in supportedProtocols) {
        "This Android device does not support TLS 1.2."
    }
    return arrayOf("TLSv1.2")
}

class LocalTransferEngine(context: Context) : AutoCloseable {
    private val appContext = context.applicationContext
    val identity = NovaIdentity(appContext)
    private val peerStore = PeerStore(appContext)
    private val preferences = appContext.getSharedPreferences("nova-connect", Context.MODE_PRIVATE)
    private val listeners = CopyOnWriteArrayList<ConnectListener>()
    private val transfers = ConcurrentHashMap<String, TransferRow>()
    private val handles = ConcurrentHashMap<String, TransferHandle>()
    private val pendingIncoming = ConcurrentHashMap<String, Pair<IncomingRequest, CompletableFuture<Boolean>>>()
    private val workerPool: ExecutorService = Executors.newFixedThreadPool(4) { task ->
        Thread(task, "NOVA-transfer").apply { isDaemon = true }
    }
    private val incomingPool: ExecutorService = ThreadPoolExecutor(
        2,
        2,
        0L,
        TimeUnit.MILLISECONDS,
        ArrayBlockingQueue(16),
        { task -> Thread(task, "NOVA-incoming-transfer").apply { isDaemon = true } },
        ThreadPoolExecutor.AbortPolicy()
    )
    private val tlsContext: SSLContext = createTlsContext()
    private var serverSocket: SSLServerSocket? = null
    private var discovery: LanDiscovery? = null
    private var acceptThread: Thread? = null
    private val fileFinalizeLock = Any()
    private val closed = AtomicBoolean(false)
    @Volatile
    var status: String = "Starting local transfer…"
        private set

    init {
        loadHistory()
    }

    val tcpPort: Int
        get() = serverSocket?.localPort ?: 0

    val pairingCode: String
        get() = identity.pairingCode

    val localEndpoint: String
        get() {
            val port = tcpPort
            return if (port <= 0) "Starting…"
            else localIpv4Addresses().joinToString(", ") { "$it:$port" }.ifBlank { "TCP $port" }
        }

    fun addListener(listener: ConnectListener) {
        listeners.addIfAbsent(listener)
    }

    fun removeListener(listener: ConnectListener) {
        listeners.remove(listener)
    }

    @Synchronized
    fun start() {
        check(!closed.get()) { "NOVA Connect has been stopped." }
        if (serverSocket != null) return
        try {
            val socket = (tlsContext.serverSocketFactory.createServerSocket(0) as SSLServerSocket).apply {
                reuseAddress = true
                needClientAuth = true
                soTimeout = 1_000
                enabledProtocols = compatibleTlsProtocols(enabledProtocols)
            }
            serverSocket = socket
            discovery = LanDiscovery(appContext, identity, socket.localPort, peerStore) { notifyPeersChanged() }.also {
                it.start()
            }
            acceptThread = Thread({ acceptLoop(socket) }, "NOVA-TLS-listener").apply {
                isDaemon = true
                start()
            }
            setStatus(if (discovery?.error == null) "Available on local Wi-Fi/LAN" else "Manual LAN connection available")
        } catch (exception: Exception) {
            serverSocket?.close()
            serverSocket = null
            setStatus("Cannot start LAN transfer: ${exception.message ?: exception.javaClass.simpleName}")
            throw exception
        }
    }

    fun peers(): List<NovaPeer> = discovery?.snapshot() ?: peerStore.allPeers()

    @Synchronized
    fun refreshDiscovery() {
        check(!closed.get()) { "NOVA Connect has been stopped." }
        check(serverSocket?.isClosed == false) { "The local transfer listener is not running." }
        val activeDiscovery = discovery ?: throw IllegalStateException("LAN discovery is unavailable.")
        activeDiscovery.refresh()
        setStatus("Nearby device search refreshed.")
        notifyPeersChanged()
    }

    fun trustPeer(peer: NovaPeer) {
        check(!peer.identityChanged) { "This device announced a different identity. Remove the old trust before pairing again." }
        peerStore.trust(peer)
        discovery?.addManualPeer(peer.copy(trusted = true))
        notifyPeersChanged()
    }

    fun removeTrustedPeer(deviceId: String) {
        peerStore.remove(deviceId)
        notifyPeersChanged()
    }

    fun addManualPeer(peer: NovaPeer) {
        if (peer.deviceId == identity.deviceId || peer.fingerprint == identity.fingerprint)
            throw IllegalArgumentException("This is the current device.")
        if (peer.trusted) peerStore.trust(peer)
        if (discovery == null) peerStore.observe(peer) else discovery?.addManualPeer(peer)
        notifyPeersChanged()
    }

    fun setDestinationTree(uri: Uri) {
        preferences.edit().putString("destinationTree", uri.toString()).apply()
        setStatus("Download folder selected.")
    }

    fun destinationTree(): Uri? =
        preferences.getString("destinationTree", null)?.let(Uri::parse)

    fun transferRows(): List<TransferRow> =
        transfers.values.sortedByDescending { it.updatedAtMillis }

    fun pendingRequests(): List<IncomingRequest> =
        pendingIncoming.values.map { it.first }.sortedBy { it.offer.createdAt }

    fun sendFiles(peer: NovaPeer, uris: List<Uri>): List<String> {
        check(!closed.get()) { "NOVA Connect has been stopped." }
        require(peer.trusted && !peer.identityChanged) { "Pair with the selected device before sending files." }
        require(peer.port in 1..65535) { "The peer does not have a reachable LAN port." }
        require(uris.isNotEmpty()) { "Select at least one file." }
        val files = uris.map { uri -> uri to queryFileInfo(uri) }
        return files.map { (uri, info) ->
            val transferId = NovaWire.newTransferId()
            val row = TransferRow(transferId, info.first, peer.displayName, "Outgoing", "Queued", 0, info.second)
            val handle = TransferHandle(transferId, peer, uri, info.first, info.second)
            handles[transferId] = handle
            transfers[transferId] = row
            notifyTransfer(row)
            handle.worker = workerPool.submit { sendOne(handle) }
            transferId
        }
    }

    fun respondToIncoming(transferId: String, accepted: Boolean) {
        val pending = pendingIncoming[transferId] ?: return
        val allow = accepted && destinationTree() != null
        pending.second.complete(allow)
        if (accepted && !allow) setStatus("Choose a download folder before accepting an incoming file.")
    }

    fun cancelTransfer(transferId: String) {
        val handle = handles[transferId]
        if (handle != null) {
            handle.cancelled.set(true)
            runCatching { handle.socket?.close() }
            if (handle.worker?.cancel(true) == true && handle.workerThread == null)
                handles.remove(transferId, handle)
            updateTransfer(transferId, "Cancelled", error = "Cancelled by user.")
            return
        }
        pendingIncoming[transferId]?.second?.complete(false)
        handles[transferId]?.let { runCatching { it.socket?.close() } }
    }

    private fun sendOne(handle: TransferHandle) {
        handle.workerThread = Thread.currentThread()
        try {
            val uri = handle.uri ?: throw IOException("The outgoing transfer has no document URI.")
            checkCancelled(handle)
            updateTransfer(handle.id, "Preparing", 0)
            val digest = calculateDigest(handle)
            checkCancelled(handle)
            val tcp = Socket()
            handle.socket = tcp
            tcp.connect(InetSocketAddress(handle.peer.address, handle.peer.port), CONNECT_TIMEOUT_MILLIS)
            tcp.soTimeout = APPROVAL_TIMEOUT_MILLIS
            val tls = tlsContext.socketFactory.createSocket(
                tcp,
                handle.peer.address.hostAddress,
                handle.peer.port,
                true
            ) as SSLSocket
            handle.socket = tls
            tls.enabledProtocols = compatibleTlsProtocols(tls.enabledProtocols)
            tls.startHandshake()
            checkCancelled(handle)
            val type = appContext.contentResolver.getType(uri)
            val offer = NovaWire.createOffer(
                handle.id,
                identity.deviceId,
                identity.displayName,
                handle.peer.deviceId,
                handle.peer.displayName,
                handle.fileName,
                handle.sizeBytes,
                type,
                digest
            )
            NovaWire.writeFrame(tls.outputStream, "offer", offer)
            updateTransfer(handle.id, "Waiting for approval", 0)
            val decision = NovaWire.readFrame(tls.inputStream)
            require(decision.type == "decision") { "Unexpected response to the file offer." }
            if (!decision.payload.optBoolean("Accepted", false)) {
                updateTransfer(handle.id, "Rejected", error = decision.payload.optString("Message").takeIf(String::isNotBlank))
                return
            }
            checkCancelled(handle)
            updateTransfer(handle.id, "Transferring", 0)
            val hash = MessageDigest.getInstance("SHA-256")
            var transferred = 0L
            appContext.contentResolver.openInputStream(uri)?.use { input ->
                val buffer = ByteArray(TRANSFER_BUFFER_SIZE)
                while (transferred < handle.sizeBytes) {
                    checkCancelled(handle)
                    val wanted = minOf(buffer.size.toLong(), handle.sizeBytes - transferred).toInt()
                    val read = input.read(buffer, 0, wanted)
                    if (read < 0) throw IOException("The selected file ended before its advertised size.")
                    if (read == 0) continue
                    tls.outputStream.write(buffer, 0, read)
                    hash.update(buffer, 0, read)
                    transferred += read
                    updateProgress(handle, transferred)
                }
                tls.outputStream.flush()
            } ?: throw IOException("The selected file can no longer be opened.")
            require(hash.digest().joinToString("") { "%02X".format(it) } == digest) {
                "The selected file changed while it was being sent."
            }
            val complete = NovaWire.readFrame(tls.inputStream)
            require(complete.type == "complete" && complete.payload.optBoolean("Accepted", false)) {
                complete.payload.optString("Message").ifBlank { "The receiver could not verify the file." }
            }
            updateTransfer(handle.id, "Completed", handle.sizeBytes)
        } catch (exception: Exception) {
            if (handle.cancelled.get() || exception is InterruptedException || exception is InterruptedIOException) {
                updateTransfer(handle.id, "Cancelled", error = "Cancelled by user.")
            } else {
                updateTransfer(handle.id, "Failed", error = exception.message ?: exception.javaClass.simpleName)
            }
        } finally {
            runCatching { handle.socket?.close() }
            handles.remove(handle.id)
            Thread.interrupted()
        }
    }

    private fun calculateDigest(handle: TransferHandle): String {
        val uri = handle.uri ?: throw IOException("The outgoing transfer has no document URI.")
        updateTransfer(handle.id, "Preparing", 0)
        val digest = MessageDigest.getInstance("SHA-256")
        var readTotal = 0L
        appContext.contentResolver.openInputStream(uri)?.use { input ->
            val buffer = ByteArray(TRANSFER_BUFFER_SIZE)
            while (true) {
                checkCancelled(handle)
                val read = input.read(buffer)
                if (read < 0) break
                if (read == 0) continue
                digest.update(buffer, 0, read)
                readTotal += read
            }
        } ?: throw IOException("The selected file can no longer be opened.")
        require(readTotal == handle.sizeBytes) { "The selected file size changed before sending." }
        return digest.digest().joinToString("") { "%02X".format(it) }
    }

    private fun acceptLoop(socket: SSLServerSocket) {
        while (!closed.get() && !socket.isClosed) {
            try {
                val peerSocket = socket.accept() as SSLSocket
                peerSocket.soTimeout = REQUEST_TIMEOUT_MILLIS
                try {
                    incomingPool.submit { receiveOne(peerSocket) }
                } catch (_: java.util.concurrent.RejectedExecutionException) {
                    peerSocket.close()
                }
            } catch (_: java.net.SocketTimeoutException) {
                continue
            } catch (exception: Exception) {
                if (!closed.get()) setStatus("LAN listener error: ${exception.message ?: exception.javaClass.simpleName}")
                break
            }
        }
    }

    private fun receiveOne(socket: SSLSocket) {
        var transferId: String? = null
        var pendingRequestEntry: Pair<IncomingRequest, CompletableFuture<Boolean>>? = null
        var ownsTransferRecord = false
        var handle: TransferHandle? = null
        try {
            socket.use { tls ->
                tls.startHandshake()
                val peerCertificate = tls.session.peerCertificates.firstOrNull() as? X509Certificate
                    ?: throw CertificateException("The peer did not present a NOVA device certificate.")
                val fingerprint = fingerprint(peerCertificate)
                val peer = peerStore.trustedByFingerprint(fingerprint)
                    ?: throw CertificateException("The peer certificate is not paired.")
                require(NovaIdentity.deviceIdFromCertificate(peerCertificate) == peer.deviceId) {
                    "The peer certificate device ID does not match the paired device."
                }
                val frame = NovaWire.readFrame(tls.inputStream)
                require(frame.type == "offer") { "Expected a NOVA file offer." }
                val offer = NovaWire.parseOffer(frame.payload, identity.deviceId, peer.deviceId)
                transferId = offer.transferId
                require(!transfers.containsKey(offer.transferId)) { "This transfer ID has already been used." }
                val request = IncomingRequest(offer, peer.displayName)
                val response = CompletableFuture<Boolean>()
                val requestEntry = request to response
                require(pendingIncoming.putIfAbsent(offer.transferId, requestEntry) == null) {
                    "This transfer is already awaiting approval."
                }
                pendingRequestEntry = requestEntry
                ownsTransferRecord = true
                updateTransfer(offer.transferId, "Waiting for approval", 0, offer.sizeBytes, offer.fileName, peer.displayName, "Incoming")
                listeners.forEach { listener -> runCatching { listener.onIncomingRequest(request) } }

                val accepted = try {
                    response.get(APPROVAL_TIMEOUT_MILLIS.toLong(), TimeUnit.MILLISECONDS)
                } catch (_: TimeoutException) {
                    false
                } finally {
                    pendingIncoming.remove(offer.transferId, requestEntry)
                }

                if (!accepted) {
                    NovaWire.writeFrame(tls.outputStream, "decision", NovaWire.reply(false, "The recipient declined the transfer."))
                    updateTransfer(offer.transferId, "Rejected", 0, offer.sizeBytes, offer.fileName, peer.displayName, "Incoming")
                    return
                }

                handle = TransferHandle(offer.transferId, peer, null, offer.fileName, offer.sizeBytes)
                handle.socket = tls
                handles[offer.transferId] = handle
                tls.soTimeout = APPROVAL_TIMEOUT_MILLIS
                NovaWire.writeFrame(tls.outputStream, "decision", NovaWire.reply(true))
                updateTransfer(offer.transferId, "Transferring", 0, offer.sizeBytes, offer.fileName, peer.displayName, "Incoming")
                receiveFile(tls, offer, peer, handle)
            }
        } catch (exception: Exception) {
            val active = handle
            if (active?.cancelled?.get() == true || exception is InterruptedException) {
                transferId?.let { updateTransfer(it, "Cancelled", error = "Cancelled by user.") }
            } else if (transferId != null && ownsTransferRecord) {
                updateTransfer(transferId, "Failed", error = exception.message ?: exception.javaClass.simpleName)
            } else {
                setStatus("Rejected unauthenticated or invalid connection: ${exception.message ?: exception.javaClass.simpleName}")
            }
        } finally {
            if (transferId != null && handle != null) handles.remove(transferId, handle)
            if (transferId != null && pendingRequestEntry != null)
                pendingIncoming.remove(transferId, pendingRequestEntry)
            runCatching { socket.close() }
        }
    }

    private fun receiveFile(
        socket: SSLSocket,
        offer: TransferOfferData,
        peer: NovaPeer,
        handle: TransferHandle
    ) {
        val tree = destinationTree() ?: throw IOException("Choose a download folder before accepting a file.")
        val rootDocument = DocumentsContract.buildDocumentUriUsingTree(
            tree,
            DocumentsContract.getTreeDocumentId(tree)
        )
        val partialName = ".nova-${offer.transferId}.part"
        val partialUri = DocumentsContract.createDocument(
            appContext.contentResolver,
            rootDocument,
            offer.contentType ?: "application/octet-stream",
            partialName
        ) ?: throw IOException("The selected download folder cannot create files.")

        try {
            val digest = MessageDigest.getInstance("SHA-256")
            var received = 0L
            appContext.contentResolver.openOutputStream(partialUri, "w")?.use { output ->
                val buffer = ByteArray(TRANSFER_BUFFER_SIZE)
                while (received < offer.sizeBytes) {
                    checkCancelled(handle)
                    val wanted = minOf(buffer.size.toLong(), offer.sizeBytes - received).toInt()
                    val read = socket.inputStream.read(buffer, 0, wanted)
                    if (read < 0) throw IOException("The sender disconnected before the file was complete.")
                    if (read == 0) continue
                    output.write(buffer, 0, read)
                    digest.update(buffer, 0, read)
                    received += read
                    updateProgress(handle, received)
                }
                output.flush()
            } ?: throw IOException("The selected download folder cannot write files.")

            val actualDigest = digest.digest().joinToString("") { "%02X".format(it) }
            require(actualDigest == offer.sha256) { "The received file failed SHA-256 integrity verification." }
            synchronized(fileFinalizeLock) {
                val name = uniqueDisplayName(tree, offer.fileName)
                DocumentsContract.renameDocument(appContext.contentResolver, partialUri, name)
                    ?: throw IOException("The selected download folder cannot finalize the file.")
            }
            NovaWire.writeFrame(socket.outputStream, "complete", NovaWire.reply(true))
            updateTransfer(
                handle.id,
                "Completed",
                offer.sizeBytes,
                offer.sizeBytes,
                offer.fileName,
                peer.displayName,
                "Incoming"
            )
        } catch (exception: Exception) {
            runCatching { DocumentsContract.deleteDocument(appContext.contentResolver, partialUri) }
            runCatching {
                NovaWire.writeFrame(socket.outputStream, "complete", NovaWire.reply(false, "The file could not be saved or verified."))
            }
            throw exception
        }
    }

    private fun uniqueDisplayName(tree: Uri, original: String): String {
        val rootId = DocumentsContract.getTreeDocumentId(tree)
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, rootId)
        val existing = HashSet<String>()
        appContext.contentResolver.query(
            children,
            arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME),
            null,
            null,
            null
        )?.use { cursor ->
            val nameColumn = cursor.getColumnIndex(DocumentsContract.Document.COLUMN_DISPLAY_NAME)
            while (cursor.moveToNext() && nameColumn >= 0)
                existing.add(cursor.getString(nameColumn))
        }
        if (original !in existing) return original
        val dot = original.lastIndexOf('.').takeIf { it > 0 } ?: original.length
        val base = original.substring(0, dot)
        val extension = original.substring(dot)
        for (index in 2..999_999) {
            val candidate = "$base ($index)$extension"
            if (candidate !in existing && NovaWire.isSafeFileName(candidate)) return candidate
        }
        throw IOException("Could not generate an unused destination file name.")
    }

    private fun queryFileInfo(uri: Uri): Pair<String, Long> {
        appContext.contentResolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
            null,
            null,
            null
        )?.use { cursor ->
            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                val name = if (nameIndex >= 0) cursor.getString(nameIndex) else null
                val size = if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) cursor.getLong(sizeIndex) else -1
                require(!name.isNullOrBlank() && NovaWire.isSafeFileName(name)) {
                    "The selected document has no safe file name."
                }
                require(size >= 0) {
                    "This document provider does not report file size; choose a file from Downloads or local storage."
                }
                return name to size
            }
        }
        throw IOException("The selected file is no longer available.")
    }

    private fun createTlsContext(): SSLContext {
        val androidKeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val keyManagers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply {
            init(androidKeyStore, null)
        }
        return SSLContext.getInstance("TLS").apply {
            init(keyManagers.keyManagers, arrayOf(PinningTrustManager()), null)
        }
    }

    private inner class PinningTrustManager : X509TrustManager {
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            verifyPinnedPeer(chain)
        }

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            verifyPinnedPeer(chain)
        }

        private fun verifyPinnedPeer(chain: Array<out X509Certificate>?) {
            val certificate = chain?.firstOrNull()
                ?: throw CertificateException("The peer did not present a certificate.")
            certificate.checkValidity()
            val fingerprint = fingerprint(certificate)
            val peer = peerStore.trustedByFingerprint(fingerprint)
                ?: throw CertificateException("Certificate fingerprint has not been approved by the user.")
            require(NovaIdentity.deviceIdFromCertificate(certificate) == peer.deviceId) {
                "The certificate device ID does not match the approved NOVA peer."
            }
        }
    }

    private fun updateProgress(handle: TransferHandle, bytes: Long) {
        val now = android.os.SystemClock.elapsedRealtime()
        val elapsed = now - handle.lastProgressAt
        val speed = if (elapsed >= PROGRESS_INTERVAL_MILLIS) {
            val result = ((bytes - handle.lastProgressBytes) * 1_000 / elapsed).coerceAtLeast(0)
            handle.lastProgressAt = now
            handle.lastProgressBytes = bytes
            result
        } else 0
        val current = transfers[handle.id] ?: return
        val row = current.copy(bytesTransferred = bytes, bytesPerSecond = speed, updatedAtMillis = System.currentTimeMillis())
        transfers[handle.id] = row
        notifyTransfer(row)
    }

    private fun updateTransfer(
        id: String,
        state: String,
        bytes: Long? = null,
        total: Long? = null,
        name: String? = null,
        device: String? = null,
        direction: String? = null,
        error: String? = null
    ) {
        val old = transfers[id]
        val row = TransferRow(
            transferId = id,
            fileName = name ?: old?.fileName.orEmpty(),
            deviceName = device ?: old?.deviceName.orEmpty(),
            direction = direction ?: old?.direction.orEmpty(),
            status = state,
            bytesTransferred = bytes ?: old?.bytesTransferred ?: 0,
            totalBytes = total ?: old?.totalBytes ?: 0,
            updatedAtMillis = System.currentTimeMillis(),
            error = error
        )
        transfers[id] = row
        if (state in TERMINAL_STATES) saveHistory()
        notifyTransfer(row)
    }

    private fun notifyTransfer(row: TransferRow) {
        listeners.forEach { listener -> runCatching { listener.onTransferChanged(row) } }
    }

    private fun notifyPeersChanged() {
        listeners.forEach { listener -> runCatching { listener.onPeersChanged() } }
    }

    private fun setStatus(value: String) {
        status = value
        listeners.forEach { listener -> runCatching { listener.onStatusChanged(value) } }
    }

    private fun loadHistory() {
        val stored = runCatching { JSONArray(preferences.getString("history", "[]")) }.getOrNull() ?: return
        for (index in 0 until stored.length()) {
            val item = stored.optJSONObject(index) ?: continue
            runCatching {
                val row = TransferRow(
                    transferId = item.getString("id"),
                    fileName = item.getString("file"),
                    deviceName = item.getString("device"),
                    direction = item.getString("direction"),
                    status = item.getString("status"),
                    bytesTransferred = item.optLong("bytes"),
                    totalBytes = item.optLong("total"),
                    updatedAtMillis = item.optLong("updated")
                )
                transfers[row.transferId] = row
            }
        }
    }

    private fun saveHistory() {
        val history = transfers.values
            .filter { it.status in TERMINAL_STATES }
            .sortedByDescending { it.updatedAtMillis }
            .take(MAX_HISTORY_ENTRIES)
        val json = JSONArray()
        history.forEach { row ->
            json.put(JSONObject()
                .put("id", row.transferId)
                .put("file", row.fileName)
                .put("device", row.deviceName)
                .put("direction", row.direction)
                .put("status", row.status)
                .put("bytes", row.bytesTransferred)
                .put("total", row.totalBytes)
                .put("updated", row.updatedAtMillis))
        }
        preferences.edit().putString("history", json.toString()).apply()
    }

    private fun localIpv4Addresses(): List<String> =
        runCatching {
            java.net.NetworkInterface.getNetworkInterfaces().toList()
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.inetAddresses.toList() }
                .filterIsInstance<java.net.Inet4Address>()
                .filter { !it.isLoopbackAddress && !it.isLinkLocalAddress }
                .map { it.hostAddress ?: "" }
                .filter(String::isNotBlank)
                .distinct()
        }.getOrDefault(emptyList())

    private fun checkCancelled(handle: TransferHandle) {
        if (handle.cancelled.get() || Thread.currentThread().isInterrupted)
            throw InterruptedIOException("Cancelled by user.")
    }

    @Synchronized
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        discovery?.close()
        discovery = null
        runCatching { serverSocket?.close() }
        serverSocket = null
        handles.values.forEach { handle ->
            handle.cancelled.set(true)
            runCatching { handle.socket?.close() }
        }
        pendingIncoming.values.forEach { it.second.complete(false) }
        acceptThread?.interrupt()
        acceptThread = null
        workerPool.shutdownNow()
        incomingPool.shutdownNow()
        runCatching { workerPool.awaitTermination(2, TimeUnit.SECONDS) }
        runCatching { incomingPool.awaitTermination(2, TimeUnit.SECONDS) }
    }

    private class TransferHandle(
        val id: String,
        val peer: NovaPeer,
        val uri: Uri?,
        val fileName: String,
        val sizeBytes: Long
    ) {
        val cancelled = AtomicBoolean()
        @Volatile var socket: Socket? = null
        @Volatile var worker: java.util.concurrent.Future<*>? = null
        @Volatile var workerThread: Thread? = null
        @Volatile var lastProgressAt: Long = android.os.SystemClock.elapsedRealtime()
        @Volatile var lastProgressBytes: Long = 0
    }

    companion object {
        private const val TRANSFER_BUFFER_SIZE = 64 * 1024
        private const val CONNECT_TIMEOUT_MILLIS = 15_000
        private const val REQUEST_TIMEOUT_MILLIS = 15_000
        private const val APPROVAL_TIMEOUT_MILLIS = 180_000
        private const val PROGRESS_INTERVAL_MILLIS = 300
        private const val MAX_HISTORY_ENTRIES = 100
        private val TERMINAL_STATES = setOf("Completed", "Rejected", "Failed", "Cancelled")

        private fun fingerprint(certificate: X509Certificate): String =
            MessageDigest.getInstance("SHA-256")
                .digest(certificate.encoded)
                .joinToString("") { "%02X".format(it) }
    }
}

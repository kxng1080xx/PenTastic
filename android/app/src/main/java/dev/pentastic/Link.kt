package dev.pentastic

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import java.io.BufferedInputStream
import java.io.Closeable
import java.io.DataInputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

enum class Via(val label: String) { USB("USB"), WIFI("Wi-Fi"), BLUETOOTH("Bluetooth") }

class Link(
    private val pairings: Pairings,
    private val choice: Via,
    private val clientId: ByteArray,
    private val name: String,
    private val bluetooth: () -> BluetoothAdapter?,
    private val onStatus: (text: String, connected: Boolean) -> Unit,
    private val onTarget: (TargetInfo) -> Unit,
    private val onVideo: (VideoChunk) -> Unit,
    private val onPaper: (Int) -> Unit,
    private val onPage: (PageInfo) -> Unit,
) {
    @Volatile private var running = true
    @Volatile private var connected = false
    @Volatile private var current: Closeable? = null
    @Volatile private var lastReceived = 0L
    @Volatile private var usbReady: Conn? = null
    private val queue = LinkedBlockingQueue<ByteArray>()
    private val worker = thread(start = false, name = "link") { loop() }

    @Volatile var via: Via? = null
        private set

    fun start() = worker.start()

    fun stop() {
        running = false
        current?.close()
        usbReady?.close()
        worker.interrupt()
    }

    fun send(msg: Msg) {
        if (connected) queue.offer(Protocol.encode(msg))
    }

    fun send(samples: List<Sample>) {
        if (connected) samples.chunked(255) { queue.offer(Protocol.encode(Samples(it))) }
    }

    private class Rejected(message: String) : IOException(message)
    private class NotFound : IOException()

    private class Route(val host: String, val port: Int, val via: Via)

    private class Conn(val input: InputStream, val output: OutputStream, private val closer: Closeable) : Closeable {
        override fun close() = try { closer.close() } catch (_: IOException) {}
    }

    private fun loop() {
        var backoff = 500L
        while (running) {
            try {
                val pc = pairings.pending?.name ?: pairings.paired?.name ?: "the PC"
                onStatus(if (choice == Via.USB) "Connecting over USB…" else "Looking for $pc over ${choice.label}…", false)
                val ready = usbReady?.also { usbReady = null }
                val (conn, route) = if (ready != null) ready to USB else connectAny() ?: throw NotFound()
                conn.use {
                    session(conn, route) { backoff = 500 }
                }
            } catch (e: Exception) {
                connected = false
                via = null
                if (!running) break
                onStatus(describe(e), false)
                try {
                    Thread.sleep(backoff)
                } catch (_: InterruptedException) {
                    break
                }
                backoff = (backoff * 2).coerceAtMost(5000)
            }
        }
    }

    private fun routes() = sequence {
        yield(USB)
        val pending = pairings.pending
        val paired = pairings.paired
        if (choice == Via.WIFI) {
            pending?.let { p -> for (h in p.hosts) yield(Route(h, p.port, Via.WIFI)) }
            paired?.let { pc ->
                discover(pc.serverId)?.let { yield(it) }
                for (h in pc.hosts) yield(Route(h, pc.port, Via.WIFI))
            }
        }
        if (choice == Via.BLUETOOTH) {
            (if (pending != null) pending.bluetooth else paired?.bluetooth)?.let { yield(Route(it, 0, Via.BLUETOOTH)) }
        }
    }

    private fun connectAny(): Pair<Conn, Route>? {
        for (r in routes()) {
            if (!running) return null
            try {
                return (if (r.via == Via.BLUETOOTH) connectBluetooth(r.host) ?: continue else connectTcp(r)) to r
            } catch (_: IOException) {
            } catch (_: SecurityException) {
            }
        }
        return null
    }

    private fun connectTcp(r: Route, probe: Boolean = false): Conn {
        val s = Socket()
        if (!probe) current = s
        try {
            s.tcpNoDelay = true
            s.connect(InetSocketAddress(r.host, r.port), if (r.via == Via.USB) 1000 else 1500)
            return Conn(s.getInputStream(), s.getOutputStream(), s)
        } catch (e: IOException) {
            s.close()
            throw e
        }
    }

    @SuppressLint("MissingPermission")
    private fun connectBluetooth(mac: String): Conn? {
        val adapter = bluetooth() ?: return null
        val s = adapter.getRemoteDevice(mac).createInsecureRfcommSocketToServiceRecord(SERVICE)
        current = s
        try {
            s.connect()
            return Conn(s.inputStream, s.outputStream, s)
        } catch (e: IOException) {
            s.close()
            throw e
        }
    }

    private fun session(conn: Conn, route: Route, onConnected: () -> Unit) {
        val input = DataInputStream(BufferedInputStream(conn.input))
        val out = conn.output
        lastReceived = System.currentTimeMillis()
        val watchdog = thread(name = "link-watchdog") {
            try {
                while (true) {
                    Thread.sleep(500)
                    if (System.currentTimeMillis() - lastReceived > 3000) {
                        conn.close()
                        break
                    }
                }
            } catch (_: InterruptedException) {}
        }
        fun receive(): ByteArray = Protocol.readFrame(input).also { lastReceived = System.currentTimeMillis() }

        try {
            val hello = Protocol.decode(receive()) as? ServerHello ?: throw IOException("unexpected first message")
            val pending = pairings.pending?.takeIf { it.serverId.contentEquals(hello.serverId) }
            val paired = pairings.paired?.takeIf { it.serverId.contentEquals(hello.serverId) }
            val usb = route.via == Via.USB
            if (!usb && pending == null && paired == null) throw IOException("a different PC answered at ${route.host}")
            val auth = when {
                pending != null -> Protocol.AUTH_TOKEN
                usb -> Protocol.AUTH_LOOPBACK
                else -> Protocol.AUTH_PROOF
            }
            val key = pending?.token ?: paired?.secret
            out.write(Protocol.encode(ClientHello(Protocol.VERSION, clientId, auth, name, key?.let { Pairings.proof(it, hello.nonce, clientId) })))

            val secret = pending?.let { Pairings.secret(it.token, clientId) } ?: paired?.secret
            when (val reply = Protocol.decode(receive())) {
                is Welcome -> {
                    val here = if (route.via == Via.WIFI) listOf(route.host) else emptyList()
                    if (pending != null) {
                        pairings.paired = PairedPc(hello.serverId, secret!!, hello.name, pending.port, (here + pending.hosts).distinct(), pending.bluetooth)
                        pairings.pending = null
                    } else if (paired != null && here.isNotEmpty() && paired.hosts.firstOrNull() != route.host) {
                        pairings.paired = PairedPc(paired.serverId, paired.secret, paired.name, route.port,
                            (here + paired.hosts).distinct().take(8), paired.bluetooth)
                    }
                    onTarget(reply.target)
                }
                is Reject -> {
                    if (reply.reason == 2) pairings.pending = null
                    if (reply.reason == 3 && auth == Protocol.AUTH_PROOF) pairings.paired = null
                    throw Rejected(reply.message)
                }
                else -> throw IOException("unexpected reply")
            }

            val cipher = if (usb) null else FrameCipher(secret!!, hello.nonce, clientId)
            queue.clear()
            via = route.via
            connected = true
            onConnected()
            onStatus("Connected to ${hello.name} (${route.via.label})", true)
            val writer = thread(name = "link-writer") { write(conn, cipher) }
            val usbWatch = if (route.via == Via.USB) null else thread(name = "link-usb-watch") {
                try {
                    while (usbReady == null) {
                        Thread.sleep(2000)
                        usbReady = try {
                            connectTcp(USB, probe = true)
                        } catch (_: IOException) {
                            null
                        }
                    }
                    conn.close()
                } catch (_: InterruptedException) {}
            }
            try {
                while (running) {
                    val body = receive()
                    when (val msg = Protocol.decode(cipher?.open(body) ?: body)) {
                        is VideoChunk -> onVideo(msg)
                        is PaperChoice -> onPaper(msg.paper)
                        is PageInfo -> onPage(msg)
                        is TargetUpdate -> onTarget(msg.target)
                        else -> {}
                    }
                }
            } finally {
                connected = false
                via = null
                writer.interrupt()
                usbWatch?.interrupt()
            }
        } finally {
            watchdog.interrupt()
        }
    }

    private fun discover(serverId: ByteArray): Route? = try {
        DatagramSocket().use { u ->
            u.broadcast = true
            u.soTimeout = 200
            val request = Protocol.discoveryRequest()
            for (to in broadcastAddresses()) {
                try {
                    u.send(DatagramPacket(request, request.size, to, Protocol.DISCOVERY_PORT))
                } catch (_: IOException) {}
            }
            val buf = ByteArray(512)
            val until = System.currentTimeMillis() + 800
            var found: Route? = null
            while (found == null && System.currentTimeMillis() < until) {
                val p = DatagramPacket(buf, buf.size)
                try {
                    u.receive(p)
                } catch (_: SocketTimeoutException) {
                    continue
                }
                val reply = Protocol.decodeDiscoveryReply(buf.copyOf(p.length)) ?: continue
                if (reply.serverId.contentEquals(serverId)) found = Route(p.address.hostAddress!!, reply.port, Via.WIFI)
            }
            found
        }
    } catch (_: IOException) {
        null
    }

    private fun broadcastAddresses(): List<InetAddress> =
        NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback }
            .flatMap { n -> n.interfaceAddresses.mapNotNull { it.broadcast } } + InetAddress.getByName("255.255.255.255")

    private fun write(conn: Conn, cipher: FrameCipher?) {
        val heartbeat = Protocol.encode(Heartbeat)
        try {
            while (true) {
                val frame = queue.poll(1, TimeUnit.SECONDS) ?: heartbeat
                conn.output.write(cipher?.seal(frame) ?: frame)
            }
        } catch (_: Exception) {
            conn.close()
        }
    }

    private fun describe(e: Exception): String {
        val pc = pairings.pending?.name ?: pairings.paired?.name ?: "the PC"
        return when (e) {
            is Rejected -> "The PC refused the connection: ${e.message}"
            is NotFound -> when (choice) {
                Via.USB -> "Plug in the USB cable. USB debugging must be on, and PenTastic running on the PC."
                Via.WIFI -> "Can't find $pc on Wi-Fi. Put the phone on the same Wi-Fi or hotspot, or plug in USB."
                Via.BLUETOOTH -> if (bluetooth() == null) "Turn on Bluetooth on the phone, and allow PenTastic to use it."
                else "Can't reach $pc over Bluetooth. Is its Bluetooth on and PenTastic running? Or plug in USB."
            }
            is SocketTimeoutException -> "The PC stopped responding. Reconnecting…"
            else -> "Connection lost (${e.message}). Reconnecting…"
        }
    }

    private companion object {
        val USB = Route("127.0.0.1", Protocol.PORT, Via.USB)

        val SERVICE: UUID = UUID.fromString("5d3f0a7e-8c41-4b6e-9f2a-50e4e5435453")
    }
}

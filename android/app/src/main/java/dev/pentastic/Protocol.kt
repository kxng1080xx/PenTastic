package dev.pentastic

import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.nio.BufferUnderflowException
import java.nio.ByteBuffer
import java.nio.ByteOrder

object Kind {
    const val HOVER = 0
    const val DOWN = 1
    const val MOVE = 2
    const val UP = 3
    const val HOVER_EXIT = 4
    const val CANCEL = 5
}

object Flag {
    const val BARREL = 1
    const val ERASER = 2
    const val TILT_VALID = 4
}

data class Sample(
    val kind: Int, val flags: Int, val x: Int, val y: Int, val pressure: Int,
    val tiltX: Int, val tiltY: Int, val timeMs: Long,
)

data class MonitorInfo(val x: Int, val y: Int, val width: Int, val height: Int, val flags: Int)
data class TargetInfo(val width: Int, val height: Int, val selected: Int, val monitors: List<MonitorInfo>)

sealed interface Msg
class ServerHello(val version: Int, val serverId: ByteArray, val nonce: ByteArray, val name: String) : Msg
class ClientHello(val version: Int, val clientId: ByteArray, val auth: Int, val name: String, val proof: ByteArray? = null) : Msg
class Welcome(val secret: ByteArray, val target: TargetInfo) : Msg
class Reject(val reason: Int, val message: String) : Msg
class TargetUpdate(val target: TargetInfo) : Msg
class Samples(val items: List<Sample>) : Msg
data object Heartbeat : Msg

class DrawState(val mode: Int, val brush: Int, val color: Int, val width: Int) : Msg
class InkAction(val command: Int) : Msg
class PaperChoice(val paper: Int) : Msg
class PageInfo(val index: Int, val count: Int) : Msg

class MirrorRequest(val on: Boolean, val maxWidth: Int, val maxHeight: Int) : Msg

class VideoChunk(val frameId: Long, val flags: Int, val width: Int, val height: Int, val data: ByteArray) : Msg
class VideoAck(val frameId: Long) : Msg
class KeyPress(val action: Int, val mods: Int, val vk: Int) : Msg

class ViewRect(val x: Int, val y: Int, val size: Int) : Msg

object VideoFlag {
    const val START = 1
    const val END = 2
    const val KEY = 4
    const val NEW_STREAM = 8
}

class FrameAssembler {
    class Frame(val id: Long, val flags: Int, val width: Int, val height: Int, val data: ByteArray)

    private val buffer = java.io.ByteArrayOutputStream()
    private var id = -1L
    private var flags = 0

    fun add(c: VideoChunk): Frame? {
        if (c.flags and VideoFlag.START != 0) {
            buffer.reset()
            id = c.frameId
            flags = c.flags
        } else if (c.frameId != id) return null
        buffer.write(c.data)
        if (c.flags and VideoFlag.END == 0) return null
        id = -1
        return Frame(c.frameId, flags or VideoFlag.END, c.width, c.height, buffer.toByteArray())
    }
}

object Protocol {
    const val VERSION = 1
    const val PORT = 47810
    const val DISCOVERY_PORT = 47811
    const val AUTH_LOOPBACK = 0
    const val AUTH_TOKEN = 1
    const val AUTH_PROOF = 2
    const val MODE_CONTROL = 0
    const val MODE_DRAW = 1
    const val INK_UNDO = 1
    const val INK_CLEAR = 2
    const val INK_REDO = 3
    const val PAPER_WHITE = 1
    const val PAPER_BLACK = 2

    private val MAGIC = byteArrayOf(0x50, 0x4E, 0x54, 0x43)
    private const val T_SERVER_HELLO: Byte = 0x01
    private const val T_CLIENT_HELLO: Byte = 0x02
    private const val T_WELCOME: Byte = 0x03
    private const val T_REJECT: Byte = 0x04
    private const val T_TARGET: Byte = 0x05
    private const val T_SAMPLES: Byte = 0x10
    private const val T_DRAW_STATE: Byte = 0x40
    private const val T_INK_ACTION: Byte = 0x41
    private const val T_PAPER: Byte = 0x42
    private const val T_PAGE: Byte = 0x43
    private const val T_MIRROR: Byte = 0x20
    private const val T_VIDEO: Byte = 0x21
    private const val T_VIDEO_ACK: Byte = 0x22
    private const val T_KEY: Byte = 0x50
    private const val T_VIEW: Byte = 0x23
    private const val T_HEARTBEAT: Byte = 0x7F

    fun encode(msg: Msg): ByteArray {
        val b = ByteBuffer.allocate(when (msg) { is Samples -> 4 + 14 * msg.items.size; is VideoChunk -> 13 + msg.data.size; else -> 8192 }).order(ByteOrder.LITTLE_ENDIAN)
        b.putShort(0)
        when (msg) {
            is ServerHello -> {
                b.put(T_SERVER_HELLO).put(MAGIC).putShort(msg.version.toShort())
                b.fixed(msg.serverId, 16).fixed(msg.nonce, 16).str(msg.name)
            }
            is ClientHello -> {
                b.put(T_CLIENT_HELLO).put(MAGIC).putShort(msg.version.toShort())
                b.fixed(msg.clientId, 16).put(msg.auth.toByte())
                if (msg.auth != AUTH_LOOPBACK) b.fixed(msg.proof!!, 32)
                b.str(msg.name)
            }
            is Welcome -> b.put(T_WELCOME).fixed(msg.secret, 32).target(msg.target)
            is Reject -> b.put(T_REJECT).put(msg.reason.toByte()).str(msg.message)
            is TargetUpdate -> b.put(T_TARGET).target(msg.target)
            is Samples -> {
                require(msg.items.size <= 255) { "at most 255 samples per message" }
                b.put(T_SAMPLES).put(msg.items.size.toByte())
                for (s in msg.items) {
                    b.put(s.kind.toByte()).put(s.flags.toByte())
                    b.putShort(s.x.toShort()).putShort(s.y.toShort()).putShort(s.pressure.toShort())
                    b.put(s.tiltX.toByte()).put(s.tiltY.toByte()).putInt(s.timeMs.toInt())
                }
            }
            is DrawState -> b.put(T_DRAW_STATE).put(msg.mode.toByte()).put(msg.brush.toByte())
                .putInt(msg.color).putShort(msg.width.toShort())
            is InkAction -> b.put(T_INK_ACTION).put(msg.command.toByte())
            is PaperChoice -> b.put(T_PAPER).put(msg.paper.toByte())
            is PageInfo -> b.put(T_PAGE).put(msg.index.toByte()).put(msg.count.toByte())
            is MirrorRequest -> b.put(T_MIRROR).put((if (msg.on) 1 else 0).toByte()).putShort(msg.maxWidth.toShort()).putShort(msg.maxHeight.toShort())
            is VideoChunk -> b.put(T_VIDEO).putInt(msg.frameId.toInt()).put(msg.flags.toByte())
                .putShort(msg.width.toShort()).putShort(msg.height.toShort()).put(msg.data)
            is VideoAck -> b.put(T_VIDEO_ACK).putInt(msg.frameId.toInt())
            is KeyPress -> b.put(T_KEY).put(msg.action.toByte()).put(msg.mods.toByte()).putShort(msg.vk.toShort())
            is ViewRect -> b.put(T_VIEW).putShort(msg.x.toShort()).putShort(msg.y.toShort()).putShort(msg.size.toShort())
            Heartbeat -> b.put(T_HEARTBEAT)
        }
        val n = b.position()
        b.putShort(0, (n - 2).toShort())
        return b.array().copyOf(n)
    }

    fun discoveryRequest(): ByteArray = MAGIC + byteArrayOf(VERSION.toByte(), (VERSION shr 8).toByte(), 0x30)

    class DiscoveryReply(val serverId: ByteArray, val port: Int, val name: String)

    fun decodeDiscoveryReply(d: ByteArray): DiscoveryReply? = try {
        val b = ByteBuffer.wrap(d).order(ByteOrder.LITTLE_ENDIAN)
        if (b.magicVersion() != VERSION || b.get() != 0x31.toByte()) null
        else DiscoveryReply(b.fixed(16), b.u16(), b.str())
    } catch (_: Exception) {
        null
    }

    fun readFrame(input: DataInputStream): ByteArray {
        val n = input.readUnsignedByte() or (input.readUnsignedByte() shl 8)
        if (n == 0) throw IOException("empty frame")
        return ByteArray(n).also { input.readFully(it) }
    }

    fun decode(body: ByteArray): Msg? {
        val b = ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN)
        try {
            val msg = when (b.get()) {
                T_SERVER_HELLO -> ServerHello(b.magicVersion(), b.fixed(16), b.fixed(16), b.str())
                T_CLIENT_HELLO -> {
                    val version = b.magicVersion()
                    val id = b.fixed(16)
                    when (val auth = b.u8()) {
                        AUTH_LOOPBACK -> ClientHello(version, id, auth, b.str())
                        AUTH_TOKEN, AUTH_PROOF -> { val proof = b.fixed(32); ClientHello(version, id, auth, b.str(), proof) }
                        else -> throw IOException("bad auth kind $auth")
                    }
                }
                T_WELCOME -> Welcome(b.fixed(32), b.target())
                T_REJECT -> Reject(b.u8(), b.str())
                T_TARGET -> TargetUpdate(b.target())
                T_SAMPLES -> Samples(List(b.u8()) { b.sample() })
                T_DRAW_STATE -> DrawState(b.u8(0..1), b.u8(0..3), b.int, b.u16())
                T_INK_ACTION -> InkAction(b.u8(1..3))
                T_PAPER -> PaperChoice(b.u8(0..2))
                T_PAGE -> PageInfo(b.u8(), b.u8())
                T_MIRROR -> MirrorRequest(b.u8(0..1) == 1, b.u16(), b.u16())
                T_VIDEO -> VideoChunk(b.int.toLong() and 0xFFFFFFFFL, b.u8(), b.u16(), b.u16(), ByteArray(b.remaining()).also { b.get(it) })
                T_VIDEO_ACK -> VideoAck(b.int.toLong() and 0xFFFFFFFFL)
                T_KEY -> KeyPress(b.u8(0..2), b.u8() and 7, b.u16())
                T_VIEW -> ViewRect(b.u16(), b.u16(), b.u16())
                T_HEARTBEAT -> Heartbeat
                else -> return null
            }
            if (b.hasRemaining()) throw IOException("trailing bytes")
            return msg
        } catch (e: BufferUnderflowException) {
            throw EOFException("truncated message")
        }
    }

    private fun ByteBuffer.u8() = get().toInt() and 0xFF
    private fun ByteBuffer.u16() = short.toInt() and 0xFFFF
    private fun ByteBuffer.u8(valid: IntRange) = u8().also { if (it !in valid) throw IOException("bad value $it") }

    private fun ByteBuffer.fixed(bytes: ByteArray, n: Int): ByteBuffer {
        require(bytes.size == n) { "expected $n bytes, got ${bytes.size}" }
        return put(bytes)
    }

    private fun ByteBuffer.fixed(n: Int) = ByteArray(n).also { get(it) }

    private fun ByteBuffer.str(s: String): ByteBuffer {
        val bytes = s.toByteArray(Charsets.UTF_8).let { if (it.size > 255) it.copyOf(255) else it }
        return put(bytes.size.toByte()).put(bytes)
    }

    private fun ByteBuffer.str() = String(fixed(u8()), Charsets.UTF_8)

    private fun ByteBuffer.magicVersion(): Int {
        if (!fixed(4).contentEquals(MAGIC)) throw IOException("bad magic")
        return u16()
    }

    private fun ByteBuffer.target(t: TargetInfo): ByteBuffer {
        putInt(t.width).putInt(t.height).put(t.selected.toByte()).put(t.monitors.size.toByte())
        for (m in t.monitors) putInt(m.x).putInt(m.y).putInt(m.width).putInt(m.height).put(m.flags.toByte())
        return this
    }

    private fun ByteBuffer.target(): TargetInfo {
        val w = int
        val h = int
        val selected = u8()
        return TargetInfo(w, h, selected, List(u8()) { MonitorInfo(int, int, int, int, u8()) })
    }

    private fun ByteBuffer.sample(): Sample {
        val kind = u8()
        if (kind > Kind.CANCEL) throw IOException("bad sample kind $kind")
        return Sample(kind, u8(), u16(), u16(), u16(), get().toInt(), get().toInt(), int.toLong() and 0xFFFFFFFFL)
    }
}

package dev.pentastic

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException

class ProtocolTest {
    private fun hex(s: String) = s.split(' ', '\n').filter { it.isNotEmpty() }.map { it.toInt(16).toByte() }.toByteArray()
    private fun seq(from: Int) = ByteArray(16) { (from + it).toByte() }
    private fun body(hex: String) = hex(hex).copyOfRange(2, hex(hex).size)
    private fun roundTrip(hex: String) = Protocol.encode(Protocol.decode(body(hex))!!)

    private val heartbeat = "01 00 7F"
    private val samples = "1E 00 10 02  01 01 00 80 00 40 FF FF F6 14 78 56 34 12  02 06 FF FF 00 00 00 04 00 A6 01 00 00 00"
    private val clientHello = "1C 00 02 50 4E 54 43 01 00 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 00 03 53 32 33"
    private val clientHelloToken = "3C 00 02 50 4E 54 43 01 00 00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 01 30 31 32 33 34 35 36 37 38 39 3A 3B 3C 3D 3E 3F 40 41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E 4F 03 53 32 33"
    private val serverHello = "2A 00 01 50 4E 54 43 01 00 10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F " +
        "20 21 22 23 24 25 26 27 28 29 2A 2B 2C 2D 2E 2F 02 50 43"
    private val welcome = "3C 00 03 " + "00 ".repeat(32) +
        "80 07 00 00 38 04 00 00 00 01  00 00 00 00 00 00 00 00 80 07 00 00 38 04 00 00 01"
    private val target = "2D 00 05 00 0F 00 00 38 04 00 00 FF 02  00 00 00 00 00 00 00 00 80 07 00 00 38 04 00 00 01  " +
        "80 F8 FF FF 00 00 00 00 80 07 00 00 38 04 00 00 00"
    private val reject = "04 00 04 01 01 76"

    private val primary = MonitorInfo(0, 0, 1920, 1080, 1)
    private val sampleList = listOf(
        Sample(Kind.DOWN, Flag.BARREL, 0x8000, 0x4000, 0xFFFF, -10, 20, 0x12345678),
        Sample(Kind.MOVE, Flag.ERASER or Flag.TILT_VALID, 0xFFFF, 0, 0x0400, 0, -90, 1),
    )

    @Test fun encodesVectors() {
        assertArrayEquals(hex(heartbeat), Protocol.encode(Heartbeat))
        assertArrayEquals(hex(samples), Protocol.encode(Samples(sampleList)))
        assertArrayEquals(hex(clientHello), Protocol.encode(ClientHello(1, seq(0), Protocol.AUTH_LOOPBACK, "S23")))
        assertArrayEquals(hex(clientHelloToken), Protocol.encode(ClientHello(1, seq(0), Protocol.AUTH_TOKEN, "S23", seq(0x30) + seq(0x40))))
        assertArrayEquals(hex(serverHello), Protocol.encode(ServerHello(1, seq(0x10), seq(0x20), "PC")))
        assertArrayEquals(hex(welcome), Protocol.encode(Welcome(ByteArray(32), TargetInfo(1920, 1080, 0, listOf(primary)))))
        assertArrayEquals(hex(target), Protocol.encode(TargetUpdate(TargetInfo(3840, 1080, 0xFF,
            listOf(primary, MonitorInfo(-1920, 0, 1920, 1080, 0))))))
        assertArrayEquals(hex(reject), Protocol.encode(Reject(1, "v")))
    }

    @Test fun decodesVectors() {
        for (v in listOf(heartbeat, samples, clientHello, clientHelloToken, serverHello, welcome, target, reject))
            assertArrayEquals(v, hex(v), roundTrip(v))
        assertEquals(sampleList, (Protocol.decode(body(samples)) as Samples).items)
        val t = (Protocol.decode(body(target)) as TargetUpdate).target
        assertEquals(-1920, t.monitors[1].x)
        assertEquals(0xFF, t.selected)
        assertArrayEquals(seq(0x30) + seq(0x40), (Protocol.decode(body(clientHelloToken)) as ClientHello).proof)
    }

    private val drawState = "09 00 40 01 00 66 D1 FF FF 3B 04"
    private val inkUndo = "02 00 41 01"

    @Test fun drawModeMessages() {
        assertArrayEquals(hex(drawState), Protocol.encode(DrawState(Protocol.MODE_DRAW, Brush.PEN.ordinal, 0xFFFFD166.toInt(), 1083)))
        assertArrayEquals(hex(inkUndo), Protocol.encode(InkAction(Protocol.INK_UNDO)))
        val d = Protocol.decode(body(drawState)) as DrawState
        assertEquals(0xFFFFD166.toInt(), d.color)
        assertEquals(1083, d.width)
        assertArrayEquals(hex(inkUndo), roundTrip(inkUndo))
        assertArrayEquals(hex("02 00 42 01"), Protocol.encode(PaperChoice(1)))
        assertEquals(1, (Protocol.decode(hex("42 01")) as PaperChoice).paper)
        assertArrayEquals(hex("03 00 43 02 05"), Protocol.encode(PageInfo(2, 5)))
        (Protocol.decode(hex("43 01 03")) as PageInfo).let { assertEquals(1, it.index); assertEquals(3, it.count) }
    }

    @Test fun mirrorMessages() {
        assertArrayEquals(hex("06 00 20 01 80 07 38 04"), Protocol.encode(MirrorRequest(true, 1920, 1080)))
        assertArrayEquals(hex("0E 00 21 07 00 00 00 0F 80 07 38 04 00 00 00 01"),
            Protocol.encode(VideoChunk(7, 0x0F, 1920, 1080, byteArrayOf(0, 0, 0, 1))))
        assertArrayEquals(hex("05 00 22 07 00 00 00"), Protocol.encode(VideoAck(7)))
        assertArrayEquals(hex("07 00 23 00 40 00 20 00 80"), Protocol.encode(ViewRect(0x4000, 0x2000, 0x8000)))
        (Protocol.decode(hex("23 00 40 00 20 00 80")) as ViewRect).let { assertEquals(listOf(0x4000, 0x2000, 0x8000), listOf(it.x, it.y, it.size)) }
        val c = Protocol.decode(hex("21 07 00 00 00 0F 80 07 38 04 00 00 00 01")) as VideoChunk
        assertEquals(7L, c.frameId)
        assertEquals(1080, c.height)
        assertArrayEquals(byteArrayOf(0, 0, 0, 1), c.data)
    }

    @Test fun assemblerJoinsChunksAndDropsHeadlessFrames() {
        val a = FrameAssembler()
        assertNull(a.add(VideoChunk(1, VideoFlag.START or VideoFlag.KEY, 4, 2, byteArrayOf(1, 2))))
        val f = a.add(VideoChunk(1, VideoFlag.END, 4, 2, byteArrayOf(3)))!!
        assertArrayEquals(byteArrayOf(1, 2, 3), f.data)
        assertEquals(VideoFlag.START or VideoFlag.KEY or VideoFlag.END, f.flags)
        assertNull(a.add(VideoChunk(2, VideoFlag.END, 4, 2, byteArrayOf(9))))
        val g = a.add(VideoChunk(3, VideoFlag.START or VideoFlag.END, 4, 2, byteArrayOf(7)))!!
        assertEquals(3L, g.id)
    }

    @Test(expected = IOException::class)
    fun badBrushThrows() {
        Protocol.decode(hex("40 01 04 66 D1 FF FF 3B 04"))
    }

    @Test fun readsBackToBackFrames() {
        val input = DataInputStream(ByteArrayInputStream(hex("$heartbeat $samples")))
        assertEquals(Heartbeat, Protocol.decode(Protocol.readFrame(input)))
        assertTrue(Protocol.decode(Protocol.readFrame(input)) is Samples)
    }

    @Test fun unknownTypeIsIgnored() = assertNull(Protocol.decode(byteArrayOf(0x6E, 1, 2, 3)))

    @Test(expected = EOFException::class)
    fun truncatedSampleThrows() {
        val b = body(samples)
        Protocol.decode(b.copyOf(b.size - 1))
    }

    @Test(expected = IOException::class)
    fun trailingBytesThrow() {
        Protocol.decode(byteArrayOf(0x7F, 0))
    }

    @Test(expected = IOException::class)
    fun badMagicThrows() {
        Protocol.decode(body(clientHello).also { it[1] = 'X'.code.toByte() })
    }

    @Test(expected = IOException::class)
    fun badSampleKindThrows() {
        Protocol.decode(body(samples).also { it[2] = 9 })
    }

    @Test(expected = EOFException::class)
    fun truncatedFrameOnStreamThrows() {
        Protocol.readFrame(DataInputStream(ByteArrayInputStream(hex(samples).copyOf(10))))
    }
}

package dev.pentastic

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.io.IOException

class PairingTest {
    private fun seq(from: Int, n: Int = 16) = ByteArray(n) { (from + it).toByte() }
    private val phone = seq(0)
    private val nonce = seq(0x20)

    @Test fun parsesTheQrLink() {
        val link = PairLink.parse("pentastic://pair?id=000102030405060708090A0B0C0D0E0F&t=202122232425262728292A2B2C2D2E2F&p=47810&h=192.168.1.5,10.0.0.2&n=My%20PC&bt=later")!!
        assertArrayEquals(phone, link.serverId)
        assertArrayEquals(nonce, link.token)
        assertEquals(47810, link.port)
        assertEquals(listOf("192.168.1.5", "10.0.0.2"), link.hosts)
        assertEquals("My PC", link.name)
        assertNull(link.bluetooth)
        assertEquals("00:11:22:AA:BB:CC", PairLink.parse("pentastic://pair?id=000102030405060708090A0B0C0D0E0F&t=202122232425262728292A2B2C2D2E2F&bt=001122aabbcc")!!.bluetooth)
    }

    @Test fun rejectsOtherLinks() {
        assertNull(PairLink.parse("https://example.com/?id=00"))
        assertNull(PairLink.parse("pentastic://pair?id=0001&t=202122232425262728292A2B2C2D2E2F"))
        assertNull(PairLink.parse("pentastic://pair?id=000102030405060708090A0B0C0D0E0F"))
        assertNull(PairLink.parse("pentastic://pair?id=000102030405060708090A0B0C0D0EZZ&t=202122232425262728292A2B2C2D2E2F"))
    }

    @Test fun cryptoMatchesVectors() {
        assertEquals("FDB7A2DA754E32570C236091654532D0CA1C09C3D8C4C1864BA82E0BE1ADE74F", Pairings.proof(seq(0, 32), nonce, phone).hex())
        assertEquals("B0463C1A44672CDE8074987C52268C10CAA717607C45F33FC0FB21DB356A7620", Pairings.secret(seq(0x30), phone).hex())
    }

    @Test fun opensWhatThePcSealed() {
        val fromPc = "1100788745C31C6CF33C750398A46ABC8641BD".unhex()!!
        val cipher = FrameCipher(seq(0, 32), nonce, phone)
        assertArrayEquals(byteArrayOf(0x7F), cipher.open(fromPc.copyOfRange(2, fromPc.size)))
    }

    @Test fun sealsWhatThePcOpens() =
        assertEquals(PHONE_HEARTBEAT, FrameCipher(seq(0, 32), nonce, phone).seal(Protocol.encode(Heartbeat)).hex())

    @Test(expected = IOException::class)
    fun tamperedFrameThrows() {
        val fromPc = "1100788745C31C6CF33C750398A46ABC8641BD".unhex()!!
        fromPc[2] = (fromPc[2] + 1).toByte()
        FrameCipher(seq(0, 32), nonce, phone).open(fromPc.copyOfRange(2, fromPc.size))
    }

    @Test fun decodesDiscoveryReply() {
        val r = Protocol.decodeDiscoveryReply(("504E5443010031" + "101112131415161718191A1B1C1D1E1F" + "C2BA" + "025043" + "80070000" + "38040000" + "01").unhex()!!)!!
        assertArrayEquals(seq(0x10), r.serverId)
        assertEquals(47810, r.port)
        assertEquals("PC", r.name)
        assertEquals("504E5443010030", Protocol.discoveryRequest().hex())
        assertNull(Protocol.decodeDiscoveryReply(byteArrayOf(1, 2, 3)))
    }

    private companion object {
        const val PHONE_HEARTBEAT = "1100E678B58B51A9B2CB9E5FDFBFB3A03AF57B"
    }
}

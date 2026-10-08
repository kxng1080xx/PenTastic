package dev.pentastic

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ShortcutsTest {
    private fun hex(s: String) = s.split(" ").map { it.toInt(16).toByte() }.toByteArray()

    @Test fun keyMessageMatchesThePc() {
        val undo = Shortcuts.byLabel("Undo")!!
        assertArrayEquals(hex("05 00 50 00 01 5A 00"), Protocol.encode(Shortcuts.press(undo, Shortcuts.TAP)))
        val pan = Protocol.decode(hex("50 01 00 20 00")) as KeyPress
        assertEquals(Shortcuts.PRESS, pan.action)
        assertEquals(0x20, pan.vk)
    }

    @Test fun defaultsExistAndReadWell() {
        Shortcuts.DEFAULT.forEach { assertNotNull(it, Shortcuts.byLabel(it)) }
        assertEquals("Ctrl+Z", Shortcuts.byLabel("Undo")!!.keys)
        assertEquals("Space", Shortcuts.byLabel("Pan")!!.keys)
        assertTrue(Shortcuts.byLabel("Pan")!!.hold)
        assertEquals(Shortcuts.ALL.size, Shortcuts.ALL.map { it.label }.toSet().size)
    }
}

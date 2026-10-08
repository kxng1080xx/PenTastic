package dev.pentastic

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.hypot

class PenToolsTest {
    @Test fun pressureFeel() {
        assertEquals(0.5f, PenTools.pressure(0.5f, PressureFeel.NORMAL), 1e-6f)
        assertTrue(PenTools.pressure(0.5f, PressureFeel.SOFT) > 0.5f)
        assertTrue(PenTools.pressure(0.5f, PressureFeel.FIRM) < 0.5f)
        assertEquals(1f, PenTools.pressure(1.3f, PressureFeel.FIRM), 1e-6f)
    }

    @Test fun stabilizerFollowsPartWayAndRestarts() {
        val s = PenTools.Stabilizer().apply { follow = 0.5f }
        assertEquals(0f to 0f, s.next(0f, 0f))
        assertEquals(0.5f to 0.25f, s.next(1f, 0.5f))
        s.reset()
        assertEquals(1f to 1f, s.next(1f, 1f))
        s.follow = 1f
        assertEquals(0f to 0f, s.next(0f, 0f))
    }

    @Test fun rectangleIsClosed() {
        val r = PenTools.shape(Shape.RECT, 0.1f, 0.2f, 0.5f, 0.6f, 16f / 9).single()
        assertEquals(5, r.size)
        assertEquals(r.first(), r.last())
        assertEquals(0.5f to 0.2f, r[1])
    }

    @Test fun ellipseIsClosedAndFillsTheBox() {
        val e = PenTools.shape(Shape.ELLIPSE, 0.2f, 0.2f, 0.6f, 0.4f, 1f).single()
        assertEquals(e.first().first, e.last().first, 1e-5f)
        assertEquals(0.6f, e.maxOf { it.first }, 1e-5f)
        assertEquals(0.2f, e.minOf { it.second }, 1e-3f)
    }

    @Test fun arrowHasShaftAndSymmetricHead() {
        val (shaft, head) = PenTools.shape(Shape.ARROW, 0.1f, 0.5f, 0.9f, 0.5f, 1f)
        assertEquals(listOf(0.1f to 0.5f, 0.9f to 0.5f), shaft)
        assertEquals(0.9f to 0.5f, head[1])
        assertTrue(head[0].first < 0.9f && head[2].first < 0.9f)
        assertEquals(0.5f - head[0].second, head[2].second - 0.5f, 1e-5f)
    }

    @Test fun densifyKeepsEndsAndLimitsSteps() {
        val d = PenTools.densify(listOf(0f to 0f, 1f to 0f), 0.1f)
        assertEquals(0f to 0f, d.first())
        assertEquals(1f to 0f, d.last())
        assertTrue(d.zipWithNext().all { (a, b) -> hypot(b.first - a.first, b.second - a.second) <= 0.1f + 1e-6f })
    }
}

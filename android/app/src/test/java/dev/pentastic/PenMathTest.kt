package dev.pentastic

import org.junit.Assert.assertEquals
import org.junit.Test
import kotlin.math.PI

class PenMathTest {
    @Test fun letterboxesWideTargetIntoTallerView() {
        val b = PenMath.letterbox(3088f, 1440f, 1920, 1200, 0f)
        assertEquals(PenMath.Box(392f, 0f, 2304f, 1440f), b)
    }

    @Test fun letterboxesUltrawideTarget() {
        val b = PenMath.letterbox(3088f, 1440f, 5120, 1440, 0f)
        assertEquals(3088f, b.width, 0.01f)
        assertEquals(868.5f, b.height, 0.01f)
        assertEquals((1440f - 868.5f) / 2, b.top, 0.01f)
    }

    @Test fun marginShrinksBox() {
        val b = PenMath.letterbox(1000f, 500f, 2, 1, 10f)
        assertEquals(PenMath.Box(20f, 10f, 960f, 480f), b)
    }

    @Test fun mapsPadToTargetAndClamps() {
        val whole = PenMath.Zoom()
        assertEquals(0.5f, whole.targetX(0.5f), 1e-6f)
        assertEquals(0f, whole.targetX(-0.1f), 1e-6f)
        assertEquals(1f, whole.targetY(1.3f), 1e-6f)
        val zoomed = PenMath.Zoom(0.25f, 0.5f, 4f)
        assertEquals(0.25f, zoomed.targetX(0f), 1e-6f)
        assertEquals(0.5f, zoomed.targetX(1f), 1e-6f)
        assertEquals(0.625f, zoomed.targetY(0.5f), 1e-6f)
        assertEquals(65535, PenMath.unit(1.2f))
    }

    @Test fun pinchKeepsThePointUnderTheFingers() {
        val z = PenMath.pinch(PenMath.Zoom(), 0.5f, 0.5f, 100f, 0.5f, 0.5f, 200f)
        assertEquals(PenMath.Zoom(0.25f, 0.25f, 2f), z)
        assertEquals(PenMath.Zoom(0.125f, 0.25f, 2f), PenMath.pinch(z, 0.5f, 0.5f, 100f, 0.75f, 0.5f, 100f))
    }

    @Test fun pinchStaysInsideTheTarget() {
        assertEquals(PenMath.Zoom(0f, 0f, 3f), PenMath.pinch(PenMath.Zoom(), 0f, 0f, 100f, 0f, 0f, 300f))
        assertEquals(PenMath.MAX_ZOOM, PenMath.pinch(PenMath.Zoom(), 0.5f, 0.5f, 1f, 0.5f, 0.5f, 100f).scale)
        assertEquals(PenMath.Zoom(), PenMath.pinch(PenMath.Zoom(0.3f, 0.4f, 2f), 0.9f, 0.1f, 100f, 0.2f, 0.7f, 10f))
        assertEquals(PenMath.Zoom(0.5f, 0.5f, 2f), PenMath.pinch(PenMath.Zoom(0.25f, 0.25f, 2f), 0.5f, 0.5f, 1f, -5f, -5f, 1f))
    }

    @Test fun tiltConversion() {
        val q = (PI / 4).toFloat()
        assertEquals(0 to 0, PenMath.tilt(0f, 1f))
        assertEquals(45 to 0, PenMath.tilt(q, (PI / 2).toFloat()))
        assertEquals(-45 to 0, PenMath.tilt(q, (-PI / 2).toFloat()))
        assertEquals(0 to -45, PenMath.tilt(q, 0f))
        assertEquals(0 to 45, PenMath.tilt(q, PI.toFloat()))
        assertEquals(60 to 0, PenMath.tilt((PI / 3).toFloat(), (PI / 2).toFloat()))
    }
}

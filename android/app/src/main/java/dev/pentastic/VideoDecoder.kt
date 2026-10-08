package dev.pentastic

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.view.Surface

class VideoDecoder(
    private val surface: Surface,
    private val onShown: (frameId: Long) -> Unit,
    private val onBroken: () -> Unit,
) {
    private val thread = HandlerThread("video").apply { start() }
    private val handler = Handler(thread.looper)
    private var codec: MediaCodec? = null
    private var size = 0 to 0
    private val pending = ArrayDeque<FrameAssembler.Frame>()
    private val freeInputs = ArrayDeque<Int>()
    private var waitingForKey = true

    fun submit(frame: FrameAssembler.Frame) {
        handler.post {
            if (frame.flags and VideoFlag.NEW_STREAM != 0 || size != frame.width to frame.height) restart(frame.width, frame.height)
            if (waitingForKey && frame.flags and VideoFlag.KEY == 0) return@post
            waitingForKey = false
            pending.addLast(frame)
            feed()
        }
    }

    fun release() {
        handler.post {
            codec?.release()
            codec = null
            thread.quitSafely()
        }
    }

    private fun restart(width: Int, height: Int) {
        codec?.release()
        pending.clear()
        freeInputs.clear()
        waitingForKey = true
        size = width to height
        val format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
            setInteger(MediaFormat.KEY_PRIORITY, 0)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 4 * 1024 * 1024)
        }
        codec = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC).apply {
            setCallback(object : MediaCodec.Callback() {
                override fun onInputBufferAvailable(c: MediaCodec, index: Int) {
                    if (c !== codec) return
                    freeInputs.addLast(index)
                    feed()
                }

                override fun onOutputBufferAvailable(c: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
                    if (c !== codec) return
                    c.releaseOutputBuffer(index, true)
                    onShown(info.presentationTimeUs)
                }

                override fun onError(c: MediaCodec, e: MediaCodec.CodecException) {
                    Log.w("PenTastic", "decoder error", e)
                    if (c !== codec) return
                    c.release()
                    codec = null
                    size = 0 to 0
                    onBroken()
                }

                override fun onOutputFormatChanged(c: MediaCodec, format: MediaFormat) {}
            }, handler)
            configure(format, surface, null, 0)
            start()
        }
    }

    private fun feed() {
        val c = codec ?: return
        while (pending.isNotEmpty() && freeInputs.isNotEmpty()) {
            val frame = pending.removeFirst()
            val index = freeInputs.removeFirst()
            c.getInputBuffer(index)!!.apply {
                clear()
                put(frame.data)
            }
            c.queueInputBuffer(index, 0, frame.data.size, frame.id, 0)
        }
    }
}

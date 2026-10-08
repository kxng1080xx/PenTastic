package dev.pentastic

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Base64
import android.view.Gravity
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout
import java.security.SecureRandom
import kotlin.math.roundToInt

class PenActivity : Activity() {
    private lateinit var view: PenView
    private lateinit var toolbar: Toolbar
    private lateinit var keys: KeyStrip
    private lateinit var tools: ToolState
    private lateinit var pairings: Pairings
    private var link: Link? = null
    private lateinit var video: SurfaceView
    @Volatile private var decoder: VideoDecoder? = null
    private val assembler = FrameAssembler()
    private val via by lazy { Via.entries.firstOrNull { it.name == intent.getStringExtra(EXTRA_VIA) } ?: Via.USB }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        tools = ToolState(getSharedPreferences("pentastic", MODE_PRIVATE))
        pairings = Pairings(getSharedPreferences("pentastic", MODE_PRIVATE))
        val inks = arrayOf(InkLayer(), InkLayer())
        view = PenView(this, tools, inks, send = { link?.send(it) }, onStrokeEnd = { toolbar.refresh() }, onPenUndo = { toolbar.undo() })
        keys = KeyStrip(this, tools, send = { link?.send(it) })
        toolbar = Toolbar(this, tools, inks,
            onChange = {
                view.invalidate()
                syncDrawState()
                updateVideo()
                showKeys()
            },
            onInk = { command -> if (tools.mode == Protocol.MODE_DRAW) link?.send(InkAction(command)) },
        )
        val dp = resources.displayMetrics.density
        toolbar.addOnLayoutChangeListener { v, _, _, _, _, _, _, _, _ ->
            view.leftInset = v.right + 4 * dp
            syncDrawState()
        }
        keys.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> insetKeys() }
        showKeys()
        video = SurfaceView(this).apply {
            visibility = if (tools.mirror) View.VISIBLE else View.GONE
            holder.addCallback(object : SurfaceHolder.Callback {
                override fun surfaceCreated(holder: SurfaceHolder) {
                    decoder = VideoDecoder(holder.surface,
                        onShown = { id -> link?.send(VideoAck(id)) },
                        onBroken = { runOnUiThread { requestMirror() } })
                    requestMirror()
                }

                override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

                override fun surfaceDestroyed(holder: SurfaceHolder) {
                    decoder?.release()
                    decoder = null
                }
            })
        }
        view.onZoomChanged = { syncDrawState() }
        view.onBoxChanged = { b ->
            video.post {
                video.layoutParams = FrameLayout.LayoutParams(b.width.roundToInt(), b.height.roundToInt()).apply {
                    leftMargin = b.left.roundToInt()
                    topMargin = b.top.roundToInt()
                }
            }
        }
        setContentView(Surface(this, video, view, toolbar, keys))
        window.attributes = window.attributes.apply {
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
        }
    }

    override fun onResume() {
        super.onResume()
        hideSystemBars()
        link = Link(
            pairings, via, clientId(), Build.MODEL, bluetooth = ::bluetoothAdapter,
            onStatus = { text, connected -> runOnUiThread { showStatus(text, connected) } },
            onTarget = { t ->
                runOnUiThread {
                    view.target = t
                    syncDrawState()
                }
            },
            onVideo = { chunk -> assembler.add(chunk)?.let { decoder?.submit(it) } },
            onPaper = { paper -> runOnUiThread { usePaper(paper) } },
            onPage = { p -> runOnUiThread { showPage(p) } },
        ).also { it.start() }
    }

    override fun onPause() {
        toolbar.dismissPopup()
        keys.releaseAll()
        link?.stop()
        link = null
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun syncDrawState() {
        val pad = view.padWidth.takeIf { it > 1 } ?: return
        val zoom = view.zoom
        val width = (tools.sizeDp * resources.displayMetrics.density / pad / zoom.scale * 65535).roundToInt()
        link?.send(DrawState(tools.mode, tools.active.ordinal, tools.inkColor, width.coerceIn(1, 65535)))
        link?.send(PaperChoice(tools.paper))
        link?.send(ViewRect(PenMath.unit(zoom.x), PenMath.unit(zoom.y), PenMath.unit(1 / zoom.scale)))
    }

    private fun showKeys() {
        val shown = if (tools.showKeys) View.VISIBLE else View.GONE
        if (keys.visibility == shown) return
        if (!tools.showKeys) keys.releaseAll()
        keys.visibility = shown
        insetKeys()
    }

    private fun insetKeys() {
        val parent = keys.parent as? View ?: return
        view.rightInset = if (keys.visibility == View.VISIBLE && keys.width > 0) parent.width - keys.left + 4 * resources.displayMetrics.density else 0f
        syncDrawState()
    }

    private fun usePaper(paper: Int) {
        tools.paper = if (paper == Protocol.PAPER_BLACK) Protocol.PAPER_BLACK else Protocol.PAPER_WHITE
        tools.save()
        view.invalidate()
        toolbar.refresh()
    }

    private fun showPage(p: PageInfo) {
        val page = "Page ${p.index} of ${p.count}"
        if (view.page.isNotEmpty() && view.page != page) view.inks[Protocol.MODE_DRAW].reset()
        view.page = page
        view.invalidate()
        toolbar.refresh()
    }

    private fun requestMirror() {
        val on = tools.mirror && view.mirrorAvailable && decoder != null
        link?.send(MirrorRequest(on, view.padWidth.roundToInt(), view.padHeight.roundToInt()))
    }

    private fun updateVideo() {
        val shown = tools.mirror && view.mirrorAvailable
        if ((video.visibility == View.VISIBLE) == shown) return
        if (!shown) requestMirror()
        video.visibility = if (shown) View.VISIBLE else View.GONE
    }

    private fun showStatus(text: String, connected: Boolean) {
        val bluetooth = link?.via == Via.BLUETOOTH
        view.mirrorAvailable = !bluetooth
        updateVideo()
        if (connected) {
            syncDrawState()
            if (tools.mirror) requestMirror()
        }
        view.status = if (bluetooth && tools.mirror) "$text · PC screen needs USB or Wi-Fi" else text
        if (connected) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    private fun hideSystemBars() {
        window.insetsController?.apply {
            hide(WindowInsets.Type.systemBars())
            systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    private fun bluetoothAdapter(): BluetoothAdapter? {
        if (Build.VERSION.SDK_INT >= 31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) return null
        return getSystemService(BluetoothManager::class.java)?.adapter?.takeIf { it.isEnabled }
    }

    companion object {
        const val EXTRA_VIA = "via"
    }

    private fun clientId(): ByteArray {
        val prefs = getSharedPreferences("pentastic", MODE_PRIVATE)
        prefs.getString("client_id", null)?.let { return Base64.decode(it, Base64.NO_WRAP) }
        val id = ByteArray(16).also { SecureRandom().nextBytes(it) }
        prefs.edit().putString("client_id", Base64.encodeToString(id, Base64.NO_WRAP)).apply()
        return id
    }
}

@SuppressLint("ViewConstructor")
private class Surface(context: Context, video: View, private val pad: PenView, private val tools: View, private val keys: View) : FrameLayout(context) {
    private var penNearUntil = 0L
    private var palm = false

    init {
        isMotionEventSplittingEnabled = true
        addView(video, LayoutParams(1, 1))
        addView(pad, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
        val margin = (8 * resources.displayMetrics.density).toInt()
        addView(tools, LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT, Gravity.START or Gravity.CENTER_VERTICAL)
            .apply { marginStart = margin })
        addView(keys, LayoutParams(LayoutParams.WRAP_CONTENT, LayoutParams.WRAP_CONTENT, Gravity.END or Gravity.CENTER_VERTICAL)
            .apply { marginEnd = margin })
    }

    override fun dispatchGenericMotionEvent(e: MotionEvent): Boolean {
        if (hasStylus(e)) penNearUntil = e.eventTime + PALM_WINDOW_MS
        return super.dispatchGenericMotionEvent(e)
    }

    override fun dispatchTouchEvent(e: MotionEvent): Boolean {
        val stylus = hasStylus(e)
        if (e.actionMasked == MotionEvent.ACTION_DOWN) {
            palm = !stylus && e.eventTime < penNearUntil && e.x > tools.right && (keys.visibility != View.VISIBLE || e.x < keys.left)
            pad.fingersArePalm = palm
        }
        if (stylus) penNearUntil = e.eventTime + PALM_WINDOW_MS
        return if (palm) pad.dispatchTouchEvent(e) else super.dispatchTouchEvent(e)
    }

    private fun hasStylus(e: MotionEvent) = (0 until e.pointerCount).any {
        val tool = e.getToolType(it)
        tool == MotionEvent.TOOL_TYPE_STYLUS || tool == MotionEvent.TOOL_TYPE_ERASER
    }

    private companion object {
        const val PALM_WINDOW_MS = 300
    }
}

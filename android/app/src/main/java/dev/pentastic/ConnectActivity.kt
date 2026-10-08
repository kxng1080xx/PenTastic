package dev.pentastic

import android.Manifest
import android.app.Activity
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewTreeObserver
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class ConnectActivity : Activity() {
    private lateinit var prefs: SharedPreferences
    private lateinit var pairings: Pairings
    private val cards = HashMap<Via, Card>()
    private lateinit var info: TextView
    private lateinit var forget: TextView
    private var usbPlugged = false
    private var waitingFor: Via? = null

    private val dp get() = resources.displayMetrics.density
    private fun Number.dp() = (toFloat() * dp).toInt()

    private val power = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            usbPlugged = i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) == BatteryManager.BATTERY_PLUGGED_USB
            refresh()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = getSharedPreferences("pentastic", MODE_PRIVATE)
        pairings = Pairings(prefs)
        takePairLink(intent)
        setContentView(build())
        if (savedInstanceState == null && Build.VERSION.SDK_INT >= 31) holdSplash()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        takePairLink(intent)
        refresh()
    }

    private fun takePairLink(intent: Intent?) {
        PairLink.parse(intent?.dataString ?: return)?.let { pairings.pending = it }
    }

    override fun onResume() {
        super.onResume()
        hideSystemBars()
        registerReceiver(power, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        refresh()
    }

    override fun onPause() {
        unregisterReceiver(power)
        super.onPause()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun hideSystemBars() {
        window.insetsController?.apply {
            hide(WindowInsets.Type.systemBars())
            systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    private fun holdSplash() {
        val content = findViewById<View>(android.R.id.content)
        val until = SystemClock.uptimeMillis() + SPLASH_MS
        content.viewTreeObserver.addOnPreDrawListener(object : ViewTreeObserver.OnPreDrawListener {
            override fun onPreDraw(): Boolean {
                if (SystemClock.uptimeMillis() < until) return false
                content.viewTreeObserver.removeOnPreDrawListener(this)
                return true
            }
        })
    }

    private fun choose(via: Via) {
        if (via != Via.USB && whyNot(via) != null) {
            info.setTextColor(Toolbar.ACCENT)
            return
        }
        val missing = permissionsFor(via).filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isNotEmpty()) {
            waitingFor = via
            requestPermissions(missing.toTypedArray(), 1)
            return
        }
        open(via)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        waitingFor?.let(::open)
        waitingFor = null
    }

    private fun permissionsFor(via: Via) = listOfNotNull(
        Manifest.permission.BLUETOOTH_CONNECT.takeIf { via == Via.BLUETOOTH && Build.VERSION.SDK_INT >= 31 },
        LOCAL_NETWORK.takeIf { via == Via.WIFI && Build.VERSION.SDK_INT >= 37 },
    )

    private fun open(via: Via) {
        prefs.edit().putString("via", via.name).apply()
        startActivity(Intent(this, PenActivity::class.java).putExtra(PenActivity.EXTRA_VIA, via.name))
    }

    private fun whyNot(via: Via): String? {
        val pending = pairings.pending
        val paired = pairings.paired
        val bluetooth = if (pending != null) pending.bluetooth else paired?.bluetooth
        return when {
            pending == null && paired == null -> "Pair first"
            via == Via.BLUETOOTH && bluetooth == null -> "Turn on Bluetooth on the PC, then scan its code again"
            else -> null
        }
    }

    private fun refresh() {
        if (!::info.isInitialized) return
        val pending = pairings.pending
        val paired = pairings.paired
        val pc = pending?.name ?: paired?.name
        val last = prefs.getString("via", null)

        fun ready(via: Via) = whyNot(via) ?: if (pending != null) "Ready to pair with $pc" else "Paired with $pc"
        cards[Via.USB]!!.show(if (usbPlugged) "Cable plugged in" else "Plug in the cable", usbPlugged, last == Via.USB.name)
        cards[Via.WIFI]!!.show(ready(Via.WIFI), whyNot(Via.WIFI) == null, last == Via.WIFI.name)
        val bluetoothOn = getSystemService(BluetoothManager::class.java)?.adapter?.isEnabled == true
        cards[Via.BLUETOOTH]!!.show(
            whyNot(Via.BLUETOOTH) ?: if (!bluetoothOn) "Turn on Bluetooth" else ready(Via.BLUETOOTH),
            whyNot(Via.BLUETOOTH) == null && bluetoothOn, last == Via.BLUETOOTH.name,
        )

        info.setTextColor(Toolbar.TEXT)
        info.text = when {
            pending != null -> "Scanned ${pending.name}'s code. Pick how to connect; pairing finishes once it connects."
            paired != null -> "Paired with ${paired.name}. To pair a different PC, scan its code."
            else -> "To use Wi-Fi or Bluetooth, pair once: on the PC, click the PenTastic tray icon, " +
                "then scan the code with this phone's Camera app."
        }
        forget.visibility = if (paired != null && pending == null) View.VISIBLE else View.GONE
        forget.text = "Forget ${paired?.name ?: "PC"}"
    }

    private fun build(): View {
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(24.dp(), 12.dp(), 24.dp(), 8.dp())
        }
        column.addView(LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL
            addView(ImageView(context).apply { setImageResource(R.mipmap.ic_launcher) }, LinearLayout.LayoutParams(36.dp(), 36.dp()))
            addView(text("PenTastic", 22f, Toolbar.TEXT, bold = true).apply { setPadding(10.dp(), 0, 0, 0) })
        })
        column.addView(text("How do you want to connect?", 16f, Toolbar.TEXT).apply { setPadding(0, 6.dp(), 0, 12.dp()) })

        val row = LinearLayout(this)
        for ((via, icon, about) in listOf(
            Triple(Via.USB, R.drawable.ic_usb, "Cable to the PC. Fastest, and the PC screen at up to 120 fps."),
            Triple(Via.WIFI, R.drawable.ic_wifi, "Same Wi-Fi or hotspot as the PC. Includes the PC screen."),
            Triple(Via.BLUETOOTH, R.drawable.ic_bluetooth, "No network needed. Pen only, no PC screen."),
        )) {
            val card = Card(via, icon, about)
            cards[via] = card
            row.addView(card.view, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f).apply {
                setMargins(6.dp(), 0, 6.dp(), 0)
            })
        }
        column.addView(row, LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT))

        info = text("", 13f, Toolbar.TEXT).apply {
            gravity = Gravity.CENTER
            setPadding(0, 12.dp(), 0, 0)
        }
        column.addView(info)
        column.addView(text("USB always takes over when the cable is plugged in.", 12f, Toolbar.MUTED).apply {
            gravity = Gravity.CENTER
            setPadding(0, 6.dp(), 0, 0)
        })
        forget = text("", 13f, Toolbar.ACCENT).apply {
            setPadding(12.dp(), 6.dp(), 12.dp(), 6.dp())
            background = RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), null, null)
            setOnClickListener {
                pairings.paired = null
                refresh()
            }
        }
        column.addView(forget, LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT))

        return FrameLayout(this).apply {
            setBackgroundColor(BACKGROUND)
            setOnApplyWindowInsetsListener { v, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                insets
            }
            addView(ScrollView(context).apply {
                isFillViewport = true
                addView(column, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT, Gravity.CENTER))
            }, FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT))
        }
    }

    private fun text(s: String, sp: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        setTextColor(color)
        setTextSize(TypedValue.COMPLEX_UNIT_SP, sp)
        if (bold) typeface = Typeface.DEFAULT_BOLD
    }

    private inner class Card(via: Via, icon: Int, about: String) {
        private val status = text("", 13f, Toolbar.ACCENT, bold = true)
        private val shape = GradientDrawable().apply {
            setColor(Toolbar.PANEL)
            cornerRadius = 18.dp().toFloat()
        }
        private val iconView = ImageView(this@ConnectActivity).apply { setImageResource(icon) }

        val view = LinearLayout(this@ConnectActivity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(16.dp(), 14.dp(), 16.dp(), 14.dp())
            background = RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), shape, null)
            isClickable = true
            contentDescription = via.label
            setOnClickListener { choose(via) }
            addView(iconView, LinearLayout.LayoutParams(32.dp(), 32.dp()))
            addView(text(via.label, 18f, Toolbar.TEXT, bold = true).apply { setPadding(0, 6.dp(), 0, 2.dp()) })
            addView(text(about, 13f, Toolbar.TEXT).apply { alpha = 0.8f },
                LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f))
            addView(status.apply { setPadding(0, 8.dp(), 0, 0) })
        }

        fun show(text: String, ready: Boolean, last: Boolean) {
            status.text = text
            status.setTextColor(if (ready) Toolbar.ACCENT else Toolbar.MUTED)
            iconView.imageTintList = ColorStateList.valueOf(if (ready) Toolbar.ACCENT else Toolbar.TEXT)
            shape.setStroke(2.dp(), if (last) Toolbar.ACCENT else Toolbar.LINE)
        }
    }

    private companion object {
        const val SPLASH_MS = 600L
        const val BACKGROUND = 0xFF121A26.toInt()
        const val LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK"
    }
}

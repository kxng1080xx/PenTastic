package dev.pentastic

import android.content.SharedPreferences
import java.net.URLDecoder
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

class PairLink(
    val serverId: ByteArray, val token: ByteArray, val port: Int, val hosts: List<String>, val name: String,
    val bluetooth: String? = null,
) {
    companion object {
        fun parse(link: String): PairLink? {
            if (!link.startsWith("pentastic://pair?")) return null
            val q = link.substringAfter('?').split('&').associate {
                it.substringBefore('=') to URLDecoder.decode(it.substringAfter('=', ""), "UTF-8")
            }
            val id = q["id"]?.unhex()?.takeIf { it.size == 16 } ?: return null
            val token = q["t"]?.unhex()?.takeIf { it.size == 16 } ?: return null
            val port = q["p"]?.toIntOrNull() ?: Protocol.PORT
            val hosts = q["h"].orEmpty().split(',').filter { it.isNotBlank() }
            val bt = q["bt"]?.takeIf { it.length == 12 && it.unhex() != null }?.uppercase()?.chunked(2)?.joinToString(":")
            return PairLink(id, token, port, hosts, q["n"].orEmpty().ifBlank { "PC" }, bt)
        }
    }
}

class PairedPc(
    val serverId: ByteArray, val secret: ByteArray, val name: String, val port: Int, val hosts: List<String>,
    val bluetooth: String? = null,
)

class Pairings(private val prefs: SharedPreferences) {
    var pending: PairLink?
        get() = scanned
        set(value) {
            scanned = value
        }

    var paired: PairedPc?
        get() {
            val id = prefs.getString("pc_id", null)?.unhex() ?: return null
            val secret = prefs.getString("pc_secret", null)?.unhex() ?: return null
            return PairedPc(id, secret, prefs.getString("pc_name", "PC")!!, prefs.getInt("pc_port", Protocol.PORT),
                prefs.getString("pc_hosts", "")!!.split(',').filter { it.isNotBlank() }, prefs.getString("pc_bt", null))
        }
        set(pc) {
            val e = prefs.edit()
            if (pc == null) e.remove("pc_id").remove("pc_secret").remove("pc_name").remove("pc_port").remove("pc_hosts").remove("pc_bt")
            else e.putString("pc_id", pc.serverId.hex()).putString("pc_secret", pc.secret.hex()).putString("pc_name", pc.name)
                .putInt("pc_port", pc.port).putString("pc_hosts", pc.hosts.joinToString(",")).putString("pc_bt", pc.bluetooth)
            e.apply()
        }

    companion object {
        @Volatile private var scanned: PairLink? = null

        fun proof(key: ByteArray, nonce: ByteArray, clientId: ByteArray) = hmac(key, nonce + clientId)

        fun secret(token: ByteArray, clientId: ByteArray) = hmac(token, "PNTC secret".toByteArray() + clientId)

        private fun hmac(key: ByteArray, data: ByteArray): ByteArray = Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            doFinal(data)
        }
    }
}

fun ByteArray.hex() = joinToString("") { "%02X".format(it) }

fun String.unhex(): ByteArray? {
    if (length % 2 != 0) return null
    return ByteArray(length / 2) { (substring(2 * it, 2 * it + 2).toIntOrNull(16) ?: return null).toByte() }
}

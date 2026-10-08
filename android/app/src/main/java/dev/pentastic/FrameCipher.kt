package dev.pentastic

import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

class FrameCipher(secret: ByteArray, nonce: ByteArray, clientId: ByteArray) {
    private val key = SecretKeySpec(key(secret, nonce, clientId), "AES")
    private val cipher = Cipher.getInstance("AES/GCM/NoPadding")
    private val decipher = Cipher.getInstance("AES/GCM/NoPadding")
    private var sent = 0L
    private var received = 0L

    fun seal(frame: ByteArray): ByteArray {
        cipher.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(128, iv(PHONE_TO_PC, sent++)))
        val body = cipher.doFinal(frame, 2, frame.size - 2)
        return ByteArray(2 + body.size).also {
            it[0] = body.size.toByte()
            it[1] = (body.size shr 8).toByte()
            body.copyInto(it, 2)
        }
    }

    fun open(body: ByteArray): ByteArray {
        decipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, iv(PC_TO_PHONE, received++)))
        return try {
            decipher.doFinal(body)
        } catch (e: AEADBadTagException) {
            throw IOException("frame failed authentication")
        }
    }

    private companion object {
        const val PC_TO_PHONE: Byte = 1
        const val PHONE_TO_PC: Byte = 2

        fun key(secret: ByteArray, nonce: ByteArray, clientId: ByteArray): ByteArray = Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(secret, "HmacSHA256"))
            doFinal("PNTC key".toByteArray() + nonce + clientId)
        }

        fun iv(direction: Byte, counter: Long): ByteArray =
            ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN).put(direction).put(ByteArray(3)).putLong(counter).array()
    }
}

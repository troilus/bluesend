package com.bluesend.android.bluetooth

import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.util.UUID

object Protocol {
    val SERVICE_UUID: UUID = UUID.fromString("d4a8c5e0-9c4a-4f4a-9c8a-1a2b3c4d5e6f")
    const val CHUNK_SIZE = 32768
    const val HEADER_SIZE = 5

    enum class PacketType(val value: Byte) {
        Text(0),
        FileStart(1),
        FileChunk(2),
        FileEnd(3);

        companion object {
            private val map = entries.associateBy { it.value }
            fun fromByte(b: Byte) = map[b] ?: Text
        }
    }

    fun writePacket(output: OutputStream, type: PacketType, payload: ByteArray) {
        val header = ByteArray(HEADER_SIZE)
        header[0] = type.value
        val bb = ByteBuffer.allocate(4).putInt(payload.size)
        System.arraycopy(bb.array(), 0, header, 1, 4)
        output.write(header)
        if (payload.isNotEmpty()) output.write(payload)
        output.flush()
    }

    fun readHeader(input: InputStream): Pair<PacketType, Int>? {
        val header = ByteArray(HEADER_SIZE)
        var offset = 0
        while (offset < HEADER_SIZE) {
            val read = input.read(header, offset, HEADER_SIZE - offset)
            if (read == -1) return null
            offset += read
        }
        val type = PacketType.fromByte(header[0])
        val len = ByteBuffer.wrap(header, 1, 4).int
        return type to len
    }

    fun readPayload(input: InputStream, length: Int): ByteArray? {
        if (length == 0) return ByteArray(0)
        val data = ByteArray(length)
        var offset = 0
        while (offset < length) {
            val read = input.read(data, offset, length - offset)
            if (read == -1) return null
            offset += read
        }
        return data
    }

    fun encodeText(text: String): ByteArray = text.toByteArray(Charsets.UTF_8)

    fun decodeText(data: ByteArray): String = String(data, Charsets.UTF_8)

    data class FileStartInfo(val fileName: String, val fileSize: Long)

    fun encodeFileStart(fileName: String, fileSize: Long): ByteArray {
        val nameBytes = fileName.toByteArray(Charsets.UTF_8)
        val bb = ByteBuffer.allocate(4 + nameBytes.size + 8)
        bb.putInt(nameBytes.size)
        bb.put(nameBytes)
        bb.putLong(fileSize)
        return bb.array()
    }

    fun decodeFileStart(data: ByteArray): FileStartInfo {
        val bb = ByteBuffer.wrap(data)
        val nameLen = bb.int
        val nameBytes = ByteArray(nameLen)
        bb.get(nameBytes)
        val fileName = String(nameBytes, Charsets.UTF_8)
        val fileSize = bb.long
        return FileStartInfo(fileName, fileSize)
    }

    data class FileChunkInfo(val seq: Int, val data: ByteArray)

    fun encodeFileChunk(seq: Int, chunkData: ByteArray): ByteArray {
        val bb = ByteBuffer.allocate(4 + 4 + chunkData.size)
        bb.putInt(seq)
        bb.putInt(chunkData.size)
        bb.put(chunkData)
        return bb.array()
    }

    fun decodeFileChunk(data: ByteArray): FileChunkInfo {
        val bb = ByteBuffer.wrap(data)
        val seq = bb.int
        val dataLen = bb.int
        val chunkData = ByteArray(dataLen)
        bb.get(chunkData)
        return FileChunkInfo(seq, chunkData)
    }

    fun encodeFileEnd(lastSeq: Int): ByteArray {
        return ByteBuffer.allocate(4).putInt(lastSeq).array()
    }

    fun decodeFileEnd(data: ByteArray): Int {
        return ByteBuffer.wrap(data).int
    }
}

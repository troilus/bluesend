package com.bluesend.android.bluetooth

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.util.Log
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import java.io.*

sealed class BluetoothEvent {
    data class Connected(val deviceName: String) : BluetoothEvent()
    data class Disconnected(val reason: String?) : BluetoothEvent()
    data class MessageReceived(val text: String) : BluetoothEvent()
    data class FileStarted(val fileName: String, val fileSize: Long) : BluetoothEvent()
    data class FileProgress(val fileName: String, val percentage: Int) : BluetoothEvent()
    data class FileCompleted(val fileName: String) : BluetoothEvent()
    data class Error(val message: String) : BluetoothEvent()
}

class BluetoothClient(private val context: Context) {
    companion object {
        private const val TAG = "BluetoothClient"
    }

    private var socket: BluetoothSocket? = null
    private var inputStream: InputStream? = null
    private var outputStream: OutputStream? = null
    private var job: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    private val _events = MutableSharedFlow<BluetoothEvent>(replay = 1, extraBufferCapacity = 16)
    val events: SharedFlow<BluetoothEvent> = _events.asSharedFlow()

    private val _isConnected = MutableStateFlow(false)
    val isConnected: StateFlow<Boolean> = _isConnected.asStateFlow()

    private val _deviceName = MutableStateFlow("")
    val deviceName: StateFlow<String> = _deviceName.asStateFlow()

    private var receiveFileState: FileReceiveState? = null

    private data class FileReceiveState(
        val fileName: String,
        val fileSize: Long,
        val outputStream: OutputStream,
        var receivedBytes: Long = 0,
        val tempFile: File
    )

    fun connect(device: BluetoothDevice) {
        disconnect()
        job = scope.launch {
            try {
                val sock = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.GINGERBREAD_MR1) {
                    device.createInsecureRfcommSocketToServiceRecord(Protocol.SERVICE_UUID)
                } else {
                    device.createRfcommSocketToServiceRecord(Protocol.SERVICE_UUID)
                }

                BluetoothAdapter.getDefaultAdapter()?.cancelDiscovery()
                sock.connect()

                socket = sock
                inputStream = sock.inputStream
                outputStream = sock.outputStream

                val name = device.name ?: device.address
                _deviceName.value = name
                _isConnected.value = true
                _events.emit(BluetoothEvent.Connected(name))

                readLoop()
            } catch (e: Exception) {
                Log.e(TAG, "Connection failed", e)
                _events.emit(BluetoothEvent.Error("连接失败: ${e.message}"))
                _events.emit(BluetoothEvent.Disconnected(e.message))
                cleanup()
            }
        }
    }

    private suspend fun readLoop() {
        try {
            while (true) {
                val header = Protocol.readHeader(inputStream ?: break) ?: break
                val (type, payloadLen) = header
                val payload = Protocol.readPayload(inputStream ?: break, payloadLen) ?: break

                when (type) {
                    Protocol.PacketType.Text -> {
                        val text = Protocol.decodeText(payload)
                        _events.emit(BluetoothEvent.MessageReceived(text))
                    }
                    Protocol.PacketType.FileStart -> {
                        handleFileStart(payload)
                    }
                    Protocol.PacketType.FileChunk -> {
                        handleFileChunk(payload)
                    }
                    Protocol.PacketType.FileEnd -> {
                        handleFileEnd(payload)
                    }
                }
            }
        } catch (e: IOException) {
            Log.e(TAG, "Read loop error", e)
        } catch (e: CancellationException) {
            // normal cancellation
        } finally {
            if (_isConnected.value) {
                _events.emit(BluetoothEvent.Disconnected(null))
            }
            cleanup()
        }
    }

    private suspend fun handleFileStart(payload: ByteArray) {
        val info = Protocol.decodeFileStart(payload)
        val dir = File(
            context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS),
            "BlueSend"
        )
        dir.mkdirs()
        val tempFile = File(dir, "${info.fileName}.tmp")
        val fileOutput = FileOutputStream(tempFile)

        receiveFileState = FileReceiveState(
            fileName = info.fileName,
            fileSize = info.fileSize,
            outputStream = fileOutput,
            tempFile = tempFile
        )
        _events.emit(BluetoothEvent.FileStarted(info.fileName, info.fileSize))
    }

    private suspend fun handleFileChunk(payload: ByteArray) {
        val state = receiveFileState ?: return
        val chunk = Protocol.decodeFileChunk(payload)
        state.outputStream.write(chunk.data)
        state.receivedBytes += chunk.data.size
        val pct = if (state.fileSize > 0) {
            (state.receivedBytes * 100 / state.fileSize).toInt()
        } else 0
        _events.emit(BluetoothEvent.FileProgress(state.fileName, pct))
    }

    private suspend fun handleFileEnd(payload: ByteArray) {
        val state = receiveFileState ?: return
        receiveFileState = null
        state.outputStream.close()

        val finalFile = File(state.tempFile.parent, state.fileName)
        state.tempFile.renameTo(finalFile)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            try {
                val values = android.content.ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, state.fileName)
                    put(MediaStore.Downloads.MIME_TYPE, "*/*")
                    put(MediaStore.Downloads.IS_PENDING, 0)
                    put(MediaStore.Downloads.RELATIVE_PATH, "Download/BlueSend")
                }
                val uri = context.contentResolver.insert(
                    MediaStore.Downloads.EXTERNAL_CONTENT_URI, values
                )
                uri?.let {
                    context.contentResolver.openOutputStream(it)?.use { os ->
                        finalFile.inputStream().use { ins -> ins.copyTo(os) }
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "MediaStore copy failed, file saved to app dir", e)
            }
        }
        _events.emit(BluetoothEvent.FileCompleted(state.fileName))
    }

    fun sendText(text: String) {
        val os = outputStream ?: return
        val payload = Protocol.encodeText(text)
        try {
            Protocol.writePacket(os, Protocol.PacketType.Text, payload)
        } catch (e: Exception) {
            Log.e(TAG, "Send text failed", e)
            scope.launch { _events.emit(BluetoothEvent.Error("发送失败: ${e.message}")) }
        }
    }

    fun sendFile(fileUri: android.net.Uri) {
        scope.launch {
            try {
                val os = outputStream ?: throw IOException("Not connected")
                val fileName = getFileName(fileUri) ?: "unknown"
                val fileSize = context.contentResolver.openInputStream(fileUri)?.use { ins ->
                    ins.available().toLong()
                } ?: 0L

                val startPayload = Protocol.encodeFileStart(fileName, fileSize)
                Protocol.writePacket(os, Protocol.PacketType.FileStart, startPayload)
                _events.emit(BluetoothEvent.FileStarted(fileName, fileSize))

                val buf = ByteArray(Protocol.CHUNK_SIZE)
                var seq = 0
                var totalRead = 0L

                context.contentResolver.openInputStream(fileUri)?.use { ins ->
                    var read: Int
                    while (ins.read(buf).also { read = it } != -1) {
                        val chunkData = if (read < buf.size) buf.copyOf(read) else buf.copyOf(read)
                        val chunkPayload = Protocol.encodeFileChunk(seq, chunkData)
                        Protocol.writePacket(os, Protocol.PacketType.FileChunk, chunkPayload)
                        totalRead += read
                        val pct = if (fileSize > 0) (totalRead * 100 / fileSize).toInt() else 0
                        _events.emit(BluetoothEvent.FileProgress(fileName, pct))
                        seq++
                    }
                }

                val endPayload = Protocol.encodeFileEnd(seq - 1)
                Protocol.writePacket(os, Protocol.PacketType.FileEnd, endPayload)
                _events.emit(BluetoothEvent.FileCompleted(fileName))
            } catch (e: Exception) {
                Log.e(TAG, "Send file failed", e)
                _events.emit(BluetoothEvent.Error("发送文件失败: ${e.message}"))
            }
        }
    }

    private fun getFileName(uri: android.net.Uri): String? {
        val cursor = context.contentResolver.query(uri, null, null, null, null)
        cursor?.use {
            if (it.moveToFirst()) {
                val idx = it.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) return it.getString(idx)
            }
        }
        return uri.lastPathSegment
    }

    fun disconnect() {
        job?.cancel()
        job = null
        cleanup()
    }

    private fun cleanup() {
        try {
            receiveFileState?.outputStream?.close()
        } catch (_: Exception) {}
        receiveFileState = null
        try { socket?.close() } catch (_: Exception) {}
        socket = null
        inputStream = null
        outputStream = null
        _isConnected.value = false
    }

    fun destroy() {
        disconnect()
        scope.cancel()
    }
}

package com.bluesend.android.viewmodel

import android.app.Application
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.bluesend.android.bluetooth.BluetoothClient
import com.bluesend.android.bluetooth.BluetoothEvent
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch

data class ChatMessage(
    val id: Long,
    val text: String,
    val isFromMe: Boolean,
    val timestamp: Long = System.currentTimeMillis()
)

sealed class FileTransferState {
    data object Idle : FileTransferState()
    data class InProgress(val fileName: String, val percentage: Int) : FileTransferState()
    data class Completed(val fileName: String) : FileTransferState()
}

data class ChatUiState(
    val isConnected: Boolean = false,
    val deviceName: String = "",
    val messages: List<ChatMessage> = emptyList(),
    val connectionError: String? = null,
    val fileTransfer: FileTransferState = FileTransferState.Idle
)

class ChatViewModel(application: Application) : AndroidViewModel(application) {

    private val bluetoothClient = BluetoothClient(application)
    private var messageIdCounter = 0L

    private val _uiState = MutableStateFlow(ChatUiState())
    val uiState: StateFlow<ChatUiState> = _uiState.asStateFlow()

    private val _navigateToChat = MutableSharedFlow<Boolean>(extraBufferCapacity = 1)
    val navigateToChat: SharedFlow<Boolean> = _navigateToChat.asSharedFlow()

    init {
        viewModelScope.launch {
            bluetoothClient.events.collect { event ->
                handleEvent(event)
            }
        }

        viewModelScope.launch {
            bluetoothClient.isConnected.collect { connected ->
                _uiState.update { it.copy(isConnected = connected) }
            }
        }

        viewModelScope.launch {
            bluetoothClient.deviceName.collect { name ->
                _uiState.update { it.copy(deviceName = name) }
            }
        }
    }

    private fun handleEvent(event: BluetoothEvent) {
        when (event) {
            is BluetoothEvent.Connected -> {
                _navigateToChat.tryEmit(true)
            }
            is BluetoothEvent.Disconnected -> {
                addMessage("系统: 连接已断开", isFromMe = false)
            }
            is BluetoothEvent.MessageReceived -> {
                addMessage(event.text, isFromMe = false)
            }
            is BluetoothEvent.FileStarted -> {
                _uiState.update {
                    it.copy(fileTransfer = FileTransferState.InProgress(event.fileName, 0))
                }
            }
            is BluetoothEvent.FileProgress -> {
                _uiState.update {
                    it.copy(fileTransfer = FileTransferState.InProgress(event.fileName, event.percentage))
                }
            }
            is BluetoothEvent.FileCompleted -> {
                _uiState.update {
                    it.copy(fileTransfer = FileTransferState.Completed(event.fileName))
                }
                addMessage("系统: 文件接收完成 - ${event.fileName}", isFromMe = false)
            }
            is BluetoothEvent.Error -> {
                _uiState.update {
                    it.copy(connectionError = event.message)
                }
            }
        }
    }

    private fun addMessage(text: String, isFromMe: Boolean) {
        val msg = ChatMessage(
            id = messageIdCounter++,
            text = text,
            isFromMe = isFromMe
        )
        _uiState.update { it.copy(messages = it.messages + msg) }
    }

    fun connectToDevice(address: String) {
        val device = getBluetoothDevice(address) ?: run {
            _uiState.update { it.copy(connectionError = "未找到设备: $address") }
            return
        }
        _uiState.update { it.copy(connectionError = null) }
        bluetoothClient.connect(device)
    }

    fun sendMessage(text: String) {
        if (text.isBlank()) return
        bluetoothClient.sendText(text)
        addMessage(text, isFromMe = true)
    }

    fun sendFile(uri: Uri) {
        bluetoothClient.sendFile(uri)
    }

    fun dismissError() {
        _uiState.update { it.copy(connectionError = null) }
    }

    fun disconnect() {
        bluetoothClient.disconnect()
    }

    override fun onCleared() {
        super.onCleared()
        bluetoothClient.destroy()
    }
}

private fun getBluetoothDevice(address: String): BluetoothDevice? {
    val adapter = BluetoothAdapter.getDefaultAdapter() ?: return null
    if (BluetoothAdapter.checkBluetoothAddress(address)) {
        return adapter.getRemoteDevice(address)
    }
    return null
}

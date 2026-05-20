package com.bluesend.android.ui.screens

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import com.bluesend.android.viewmodel.ChatViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ConnectScreen(
    viewModel: ChatViewModel,
    onBackClick: () -> Unit
) {
    val context = LocalContext.current
    val uiState by viewModel.uiState.collectAsState()
    var addressInput by remember { mutableStateOf("") }
    var scannedDevices by remember { mutableStateOf<List<BluetoothDevice>>(emptyList()) }
    var isScanning by remember { mutableStateOf(false) }
    var hasPermission by remember { mutableStateOf(checkBluetoothPermissions(context)) }

    val permissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestMultiplePermissions()
    ) { granted ->
        hasPermission = granted.values.all { it }
    }

    val discoveryReceiver = remember {
        object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                when (intent.action) {
                    BluetoothDevice.ACTION_FOUND -> {
                        val device = intent.getParcelableExtra<BluetoothDevice>(
                            BluetoothDevice.EXTRA_DEVICE
                        )
                        if (device != null && device.name != null) {
                            scannedDevices = scannedDevices + device
                        }
                    }
                    BluetoothAdapter.ACTION_DISCOVERY_FINISHED -> {
                        isScanning = false
                    }
                }
            }
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            try {
                context.unregisterReceiver(discoveryReceiver)
            } catch (_: Exception) {}
            BluetoothAdapter.getDefaultAdapter()?.cancelDiscovery()
        }
    }

    LaunchedEffect(uiState.connectionError) {
        uiState.connectionError?.let {
            // error is shown as snackbar
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("连接设备") },
                navigationIcon = {
                    IconButton(onClick = onBackClick) {
                        Icon(Icons.Default.ArrowBack, contentDescription = "返回")
                    }
                }
            )
        }
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(16.dp)
        ) {
            Text(
                text = "输入蓝牙地址",
                fontWeight = FontWeight.Medium,
                fontSize = 14.sp
            )

            Spacer(modifier = Modifier.height(8.dp))

            OutlinedTextField(
                value = addressInput,
                onValueChange = { addressInput = it.uppercase() },
                placeholder = { Text("00:11:22:33:44:55") },
                modifier = Modifier.fillMaxWidth(),
                singleLine = true,
                enabled = !uiState.isConnected
            )

            Spacer(modifier = Modifier.height(12.dp))

            Button(
                onClick = {
                    if (addressInput.isNotBlank()) {
                        viewModel.connectToDevice(addressInput.trim())
                    }
                },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(48.dp),
                enabled = addressInput.isNotBlank() && !uiState.isConnected,
                shape = MaterialTheme.shapes.medium
            ) {
                if (uiState.isConnected) {
                    Text("已连接")
                } else {
                    Text("连接")
                }
            }

            // Error display
            uiState.connectionError?.let { error ->
                Spacer(modifier = Modifier.height(8.dp))
                Card(
                    colors = CardDefaults.cardColors(
                        containerColor = MaterialTheme.colorScheme.errorContainer
                    ),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Row(
                        modifier = Modifier.padding(12.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = error,
                            color = MaterialTheme.colorScheme.onErrorContainer,
                            fontSize = 13.sp,
                            modifier = Modifier.weight(1f)
                        )
                        TextButton(onClick = { viewModel.dismissError() }) {
                            Text("关闭")
                        }
                    }
                }
            }

            Spacer(modifier = Modifier.height(24.dp))

            // Scan section
            if (!hasPermission && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                Button(
                    onClick = {
                        permissionLauncher.launch(
                            arrayOf(
                                Manifest.permission.BLUETOOTH_SCAN,
                                Manifest.permission.BLUETOOTH_CONNECT,
                                Manifest.permission.ACCESS_FINE_LOCATION
                            )
                        )
                    }
                ) {
                    Text("授予蓝牙权限")
                }
            }

            if (hasPermission) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "附近设备",
                        fontWeight = FontWeight.Medium,
                        fontSize = 14.sp
                    )
                    if (isScanning) {
                        LinearProgressIndicator(
                            modifier = Modifier
                                .width(100.dp)
                                .height(4.dp)
                        )
                    }
                    TextButton(onClick = {
                        if (!isScanning) {
                            scannedDevices = emptyList()
                            startDiscovery(context, discoveryReceiver)
                            isScanning = true
                        }
                    }) {
                        Text(if (isScanning) "扫描中..." else "扫描")
                    }
                }

                Spacer(modifier = Modifier.height(8.dp))

                if (scannedDevices.isEmpty() && !isScanning) {
                    Text(
                        text = "点击「扫描」查找附近蓝牙设备",
                        fontSize = 13.sp,
                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.4f)
                    )
                }

                LazyColumn(
                    modifier = Modifier.weight(1f)
                ) {
                    items(scannedDevices) { device ->
                        Card(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 4.dp)
                                .clickable(enabled = !uiState.isConnected) {
                                    viewModel.connectToDevice(device.address)
                                },
                            colors = CardDefaults.cardColors(
                                containerColor = MaterialTheme.colorScheme.surfaceVariant
                            )
                        ) {
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .padding(16.dp),
                                horizontalArrangement = Arrangement.SpaceBetween,
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column {
                                    Text(
                                        text = device.name ?: "未知设备",
                                        fontWeight = FontWeight.Medium
                                    )
                                    Text(
                                        text = device.address,
                                        fontSize = 12.sp,
                                        color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.5f)
                                    )
                                }
                                Text(
                                    text = "连接",
                                    color = MaterialTheme.colorScheme.primary
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

private fun checkBluetoothPermissions(context: Context): Boolean {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
        return ContextCompat.checkSelfPermission(
            context, Manifest.permission.BLUETOOTH_SCAN
        ) == PackageManager.PERMISSION_GRANTED &&
        ContextCompat.checkSelfPermission(
            context, Manifest.permission.BLUETOOTH_CONNECT
        ) == PackageManager.PERMISSION_GRANTED
    }
    return ContextCompat.checkSelfPermission(
        context, Manifest.permission.ACCESS_FINE_LOCATION
    ) == PackageManager.PERMISSION_GRANTED
}

private fun startDiscovery(context: Context, receiver: BroadcastReceiver) {
    val adapter = BluetoothAdapter.getDefaultAdapter() ?: return
    val filter = IntentFilter().apply {
        addAction(BluetoothDevice.ACTION_FOUND)
        addAction(BluetoothAdapter.ACTION_DISCOVERY_FINISHED)
    }
    context.registerReceiver(receiver, filter)
    adapter.startDiscovery()
}

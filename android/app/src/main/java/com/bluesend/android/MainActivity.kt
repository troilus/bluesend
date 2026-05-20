package com.bluesend.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.viewmodel.compose.viewModel
import com.bluesend.android.ui.navigation.BlueSendNavGraph
import com.bluesend.android.ui.theme.BlueSendTheme
import com.bluesend.android.viewmodel.ChatViewModel

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            BlueSendTheme {
                val viewModel: ChatViewModel = viewModel()
                BlueSendNavGraph(viewModel = viewModel)
            }
        }
    }
}

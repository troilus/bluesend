package com.bluesend.android.ui.navigation

import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.navigation.NavHostController
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.bluesend.android.ui.screens.ChatScreen
import com.bluesend.android.ui.screens.ConnectScreen
import com.bluesend.android.ui.screens.HomeScreen
import com.bluesend.android.viewmodel.ChatViewModel

object Routes {
    const val HOME = "home"
    const val CONNECT = "connect"
    const val CHAT = "chat"
}

@Composable
fun BlueSendNavGraph(
    viewModel: ChatViewModel,
    modifier: Modifier = Modifier,
    navController: NavHostController = rememberNavController()
) {
    val uiState by viewModel.uiState.collectAsState()

    LaunchedEffect(Unit) {
        viewModel.navigateToChat.collect {
            navController.navigate(Routes.CHAT) {
                popUpTo(Routes.HOME)
            }
        }
    }

    NavHost(
        navController = navController,
        startDestination = Routes.HOME,
        modifier = modifier
    ) {
        composable(Routes.HOME) {
            HomeScreen(
                onConnectClick = { navController.navigate(Routes.CONNECT) }
            )
        }
        composable(Routes.CONNECT) {
            ConnectScreen(
                viewModel = viewModel,
                onBackClick = { navController.popBackStack() }
            )
        }
        composable(Routes.CHAT) {
            ChatScreen(
                viewModel = viewModel,
                onDisconnect = {
                    viewModel.disconnect()
                    navController.navigate(Routes.HOME) {
                        popUpTo(Routes.HOME) { inclusive = true }
                    }
                }
            )
        }
    }
}

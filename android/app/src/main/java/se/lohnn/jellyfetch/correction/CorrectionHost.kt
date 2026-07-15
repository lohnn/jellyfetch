package se.lohnn.jellyfetch.correction

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import se.lohnn.jellyfetch.R
import se.lohnn.jellyfetch.api.ApiClient
import se.lohnn.jellyfetch.api.LibraryItem

/**
 * Hosts a [CorrectionSheet] for [item]: creates a [CorrectionViewModel] keyed on
 * the item id, wires the production [schedule] (main-thread Handler.postDelayed
 * for the convert rescan poll) and the ApiClient, collects [CorrectionEvent]s and
 * turns them into toasts + the [onApplied]/dismiss/re-open-picker flow.
 *
 * Keyed by [item.id] (via `key(item.id)` at the call site) so re-opening the
 * picker on the freshly-converted item gets a FRESH ViewModel bound to the new
 * (valid) id — never the old dead one (mirrors the classic
 * offerPickerOnNewItem → new CorrectionDialog(newItem)).
 */
@Composable
fun CorrectionHost(
    item: LibraryItem,
    onDismiss: () -> Unit,
    onApplied: (refreshed: LibraryItem?) -> Unit,
) {
    val context = LocalContext.current
    val vm: CorrectionViewModel = viewModel(
        key = "correction-${item.id}",
        factory = correctionFactory(item),
    )
    val state by vm.state.collectAsStateWithLifecycle()

    // A follow-up "open picker on the new item?" prompt after a successful convert.
    var reopenOn by remember(item.id) { mutableStateOf<LibraryItem?>(null) }

    LaunchedEffect(vm) {
        vm.events.collect { event ->
            when (event) {
                is CorrectionEvent.Applied ->
                    toast(context, context.getString(R.string.correction_applied))
                is CorrectionEvent.ConvertFound ->
                    toast(context, context.getString(R.string.correction_convert_found, event.target.wireName))
                is CorrectionEvent.ConvertPending ->
                    toast(context, context.getString(R.string.correction_convert_pending, event.target.wireName))
                is CorrectionEvent.ConvertFailed ->
                    toast(context, context.getString(R.string.correction_convert_failed, event.message))
                is CorrectionEvent.Dismiss -> {
                    onApplied(event.refreshed)
                    onDismiss()
                }
                is CorrectionEvent.OfferPickerOnNewItem -> reopenOn = event.newItem
            }
        }
    }

    CorrectionSheet(
        state = state,
        onDismiss = onDismiss,
        onSearch = vm::search,
        onApplyCandidate = vm::applyCandidate,
        onOpenTmdb = { query -> openTmdb(context, vm.tmdbSearchUrl(query)) },
        onApplyPastedId = { vm.applyPastedProviderId(it) },
        onSelectTarget = vm::selectTarget,
        onConvert = vm::convert,
    )

    // Re-open the picker on the fresh item, if offered and accepted.
    reopenOn?.let { fresh ->
        CorrectionHost(
            item = fresh,
            onDismiss = { reopenOn = null; onDismiss() },
            onApplied = onApplied,
        )
    }
}

private fun correctionFactory(item: LibraryItem) = viewModelFactory {
    initializer {
        val main = Handler(Looper.getMainLooper())
        CorrectionViewModel(
            item = item,
            apiProvider = { ApiClient.current },
            schedule = { delayMs, block -> main.postDelayed(block, delayMs) },
        )
    }
}

private fun openTmdb(context: Context, url: String) {
    try {
        context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
    } catch (_: ActivityNotFoundException) {
        toast(context, context.getString(R.string.correction_no_browser))
    }
}

private fun toast(context: Context, text: String) {
    Toast.makeText(context, text, Toast.LENGTH_LONG).show()
}

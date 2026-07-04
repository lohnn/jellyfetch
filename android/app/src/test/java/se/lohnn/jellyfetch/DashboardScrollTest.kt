package se.lohnn.jellyfetch

import android.app.Activity
import android.view.View
import android.view.ViewGroup
import android.widget.ArrayAdapter
import android.widget.FrameLayout
import android.widget.ListView
import android.widget.TextView
import androidx.core.widget.ListViewCompat
import androidx.swiperefreshlayout.widget.SwipeRefreshLayout
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.ConscryptMode
import org.robolectric.shadows.ShadowLooper

/**
 * Regression coverage for the "scrolls down fine, won't scroll back up" dashboard
 * bug. Precise symptom (per user correction): pulling down when the list is
 * scrolled to the MIDDLE (not the top) shows the refresh spinner instead of
 * scrolling the list back up — the refresh gesture fires when it should not.
 *
 * Root cause: SwipeRefreshLayout.canChildScrollUp() resolves its scroll-check
 * target via ensureTarget(), which walks to its *direct* child — the FrameLayout
 * wrapper in activity_main.xml (SwipeRefreshLayout -> FrameLayout -> ListView +
 * empty-state TextView) — not the ListView nested two levels down. Because that
 * FrameLayout is not `instanceof ListView`, canChildScrollUp() falls through to
 * `mTarget.canScrollVertically(-1)` against the FrameLayout itself, which is
 * never scrollable regardless of its child's actual scroll offset. So SRL
 * *always* believes content is at the top and hijacks every downward drag as a
 * pull-to-refresh gesture — at ANY scroll position, not just the top. That is
 * exactly "even scrolled to the middle, I get the refresh spinner instead of
 * scrolling up."
 *
 * This needs Robolectric (not plain Mockito, see the other tests in this
 * module): the defect lives inside real android.view/androidx.swiperefreshlayout
 * object behavior (ensureTarget()'s child-walk, View.canScrollVertically()),
 * which the stub-only compile-time android.jar can't execute. Robolectric runs
 * the real framework code in a shadow runtime on the plain JVM
 * testDebugUnitTest task — still no emulator/device required, but a step above
 * pure-logic mocking.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
// This dev host is Linux/arm64: Conscrypt ships no linux-aarch64 native JNI build
// (google/conscrypt#1051 — unresolved as of writing), so Robolectric's default
// security-provider bootstrap throws UnsatisfiedLinkError on setUpApplicationState
// before any test body runs. Disabling Conscrypt mode is Robolectric's documented
// workaround (robolectric/robolectric#8165) and this test exercises View/ViewGroup
// scroll plumbing only — no TLS/crypto path is involved, so it's safe here.
@ConscryptMode(ConscryptMode.Mode.OFF)
class DashboardScrollTest {

    private val activity: Activity by lazy {
        Robolectric.buildActivity(Activity::class.java).setup().get()
    }

    private val widthSpec = View.MeasureSpec.makeMeasureSpec(1080, View.MeasureSpec.EXACTLY)
    private val heightSpec = View.MeasureSpec.makeMeasureSpec(2000, View.MeasureSpec.EXACTLY)

    /**
     * Builds the exact view hierarchy activity_main.xml declares:
     * SwipeRefreshLayout -> FrameLayout -> (ListView, empty-state TextView).
     */
    private fun buildDashboardHierarchy(itemCount: Int): Pair<SwipeRefreshLayout, ListView> {
        val context = activity
        val listView = ListView(context).apply {
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT,
            )
            adapter = ArrayAdapter(
                context,
                android.R.layout.simple_list_item_1,
                (1..itemCount).map { "Job $it" },
            )
        }
        val emptyView = TextView(context).apply {
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            )
            visibility = View.GONE
        }
        val frame = FrameLayout(context).apply {
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT,
            )
            addView(listView)
            addView(emptyView)
        }
        val swipeRefresh = SwipeRefreshLayout(context).apply {
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT,
            )
            addView(frame)
        }

        layoutHierarchy(swipeRefresh)
        return swipeRefresh to listView
    }

    private fun layoutHierarchy(swipeRefresh: SwipeRefreshLayout) {
        swipeRefresh.measure(widthSpec, heightSpec)
        swipeRefresh.layout(0, 0, 1080, 2000)
    }

    /**
     * Scrolls [listView] to [position] and forces the follow-up layout pass its
     * real internal scroll bookkeeping needs. Under Robolectric,
     * ListView.setSelection() only *schedules* the traversal — without pumping
     * the (paused-by-default) main-looper scheduler and re-running layout, the
     * ListView's firstVisiblePosition/child bounds never actually update, so any
     * canScrollVertically()/canScrollList() check downstream would silently
     * observe stale (still-at-top) state.
     */
    private fun scrollListTo(listView: ListView, position: Int) {
        listView.setSelection(position)
        ShadowLooper.idleMainLooper()
        listView.measure(widthSpec, heightSpec)
        listView.layout(0, 0, 1080, 2000)
    }

    @Test
    fun `bug reproduction- SwipeRefreshLayout default target resolution ignores a mid-list scrolled ListView`() {
        val (swipeRefresh, listView) = buildDashboardHierarchy(itemCount = 200)

        // Scrolled to the MIDDLE of the list — matches the user's corrected report
        // exactly: not at the top, not at the bottom either.
        scrollListTo(listView, 100)

        // Sanity check: the ListView itself correctly knows it has content above.
        assertTrue(
            "sanity check: the ListView itself must report it can scroll up",
            ListViewCompat.canScrollList(listView, -1),
        )
        // The bug: SwipeRefreshLayout's default target resolution ignores that and
        // always reports false, so it will intercept the next downward drag as a
        // pull-to-refresh attempt instead of letting it scroll the list.
        assertFalse(
            "this is the bug: SRL's default resolution ignores the nested, " +
                "mid-scrolled ListView and always reports canChildScrollUp() == " +
                "false, showing the refresh spinner instead of scrolling back up " +
                "even though the list is nowhere near its top",
            swipeRefresh.canChildScrollUp(),
        )
    }

    @Test
    fun `fix- explicit OnChildScrollUpCallback correctly reflects the nested ListView's real scroll state`() {
        val (swipeRefresh, listView) = buildDashboardHierarchy(itemCount = 200)

        // Same wiring MainActivity.onCreate applies.
        swipeRefresh.setOnChildScrollUpCallback { _, _ ->
            ListViewCompat.canScrollList(listView, -1)
        }

        // At the very top: nothing above to scroll up to, so pull-to-refresh
        // must still be allowed to engage.
        scrollListTo(listView, 0)
        assertFalse(
            "at the top of the list, canChildScrollUp() must stay false so pull-to-refresh still works",
            swipeRefresh.canChildScrollUp(),
        )

        // Scrolled to the middle: canChildScrollUp() must now correctly reflect
        // the real, nested ListView state so a downward drag reaches the list
        // and scrolls it back up instead of being stolen for a refresh gesture.
        scrollListTo(listView, 100)
        assertTrue(
            "scrolled to the middle of the list, canChildScrollUp() must be true " +
                "so the drag scrolls the list back up instead of showing the refresh spinner",
            swipeRefresh.canChildScrollUp(),
        )
    }
}

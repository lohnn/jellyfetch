package se.lohnn.jellyfetch.ui.theme

import androidx.compose.ui.unit.dp

/**
 * Single-source shared dimension constants (SNG-026): screen edge padding, the
 * vertical rhythm between stacked blocks, and the poster frame size the metadata
 * / correction UIs share. Kept in one place so the migrated screens stay visually
 * consistent and a spacing tweak is one edit, not a scatter-hunt across files.
 */
object Dimens {
    /** Standard screen edge / content inset. */
    val screenPadding = 16.dp

    /** Gap between a section and the next within a scrolling column. */
    val sectionGap = 16.dp

    /** Tight gap between a label and its value / a title and its subtitle. */
    val tightGap = 4.dp

    /** Gap between a heading and the body of its section. */
    val blockGap = 8.dp

    /** Poster thumbnail frame (metadata card + correction candidates). */
    val posterWidth = 72.dp
    val posterHeight = 108.dp

    /** Corner radius shared by cards, badges, and progress bars. */
    val cardCorner = 8.dp
}

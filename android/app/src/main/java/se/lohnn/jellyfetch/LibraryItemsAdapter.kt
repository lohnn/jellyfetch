package se.lohnn.jellyfetch

import android.content.Context
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.BaseAdapter
import android.widget.ImageView
import android.widget.TextView
import se.lohnn.jellyfetch.api.LibraryItem
import se.lohnn.jellyfetch.api.LibraryItemType

/**
 * BaseAdapter for the "All library items" list — same classic Views idiom as
 * [JobsAdapter]. Each row shows name + (year · type · primary provider id) and
 * is tappable into the correction picker (handled by the Activity).
 */
class LibraryItemsAdapter(
    private val context: Context,
) : BaseAdapter() {

    private var items: List<LibraryItem> = emptyList()

    fun submitList(newItems: List<LibraryItem>) {
        items = newItems
        notifyDataSetChanged()
    }

    override fun getCount(): Int = items.size
    override fun getItem(position: Int): LibraryItem = items[position]
    override fun getItemId(position: Int): Long = items[position].id.hashCode().toLong()

    override fun getView(position: Int, convertView: View?, parent: ViewGroup): View {
        val view = convertView
            ?: LayoutInflater.from(context).inflate(R.layout.item_library, parent, false)
        val item = items[position]

        view.findViewById<TextView>(R.id.library_name).text = item.name

        val typeLabel = item.type?.let {
            when (it) {
                LibraryItemType.MOVIE -> context.getString(R.string.category_movie)
                LibraryItemType.SERIES -> context.getString(R.string.category_series)
            }
        }
        val primaryProvider = item.providerIds.entries.firstOrNull()?.let { "${it.key} ${it.value}" }
        val subtitleParts = listOfNotNull(item.year?.toString(), typeLabel, primaryProvider)
        view.findViewById<TextView>(R.id.library_subtitle).text = subtitleParts.joinToString(" · ")

        // Best-effort poster; PosterLoader's tag-based guard handles ListView recycling.
        PosterLoader.load(item.posterUrl, view.findViewById<ImageView>(R.id.library_poster))

        return view
    }
}

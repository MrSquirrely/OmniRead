using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using OmniRead.Services;

namespace OmniRead;

/// <summary>Rearranging the shelves by dragging a shelf's heading to another place among them.</summary>
public partial class MainWindow {
    private const string ShelfDragFormat = "OmniRead.Shelf";
    private const double ShelfScrollEdge = 48;

    private Point? _shelfDragStart;
    private ShelfDropLine? _shelfDropLine;

    private static bool SameShelf(string first, string second) {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private void OnShelfMouseDown(object sender, MouseButtonEventArgs e) {
        _shelfDragStart = e.GetPosition(LibraryList);
    }

    /// <summary>Starts dragging a shelf once the pointer has moved far enough from where its heading was pressed.</summary>
    private void OnShelfMouseMove(object sender, MouseEventArgs e) {
        if (e.LeftButton != MouseButtonState.Pressed) {
            _shelfDragStart = null;
            return;
        }
        if ((_shelfDragStart is not Point start)
            || (((FrameworkElement)sender).DataContext is not CollectionViewGroup { Name: ShelfGroup shelf })) {
            return;
        }
        Vector moved = e.GetPosition(LibraryList) - start;
        if ((Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance)
            && (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)) {
            return;
        }
        _shelfDragStart = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ShelfDragFormat, shelf.Key), DragDropEffects.Move);
        ClearShelfDropLine();
    }

    private void OnShelfDragOver(object sender, DragEventArgs e) {
        // Anything else being dragged (a book file from Explorer) is left to the window.
        if (e.Data.GetData(ShelfDragFormat) is not string dragged) {
            return;
        }
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        ScrollShelvesNearEdge(e.GetPosition(LibraryList).Y);
        if ((ShelfDropTarget(e) is not (GroupItem Item, ShelfGroup Shelf, bool Below) target) || SameShelf(target.Shelf.Key, dragged)) {
            ClearShelfDropLine();
            return;
        }
        e.Effects = DragDropEffects.Move;
        if ((_shelfDropLine is not null) && ReferenceEquals(_shelfDropLine.AdornedElement, target.Item) && (_shelfDropLine.Below == target.Below)) {
            return;
        }
        ClearShelfDropLine();
        if (AdornerLayer.GetAdornerLayer(target.Item) is AdornerLayer layer) {
            _shelfDropLine = new ShelfDropLine(target.Item, target.Below, (Brush)FindResource("AccentBrush")) { IsHitTestVisible = false };
            layer.Add(_shelfDropLine);
        }
    }

    private void OnShelfDragLeave(object sender, DragEventArgs e) {
        // The event also arrives when the pointer only moves from one part of the list to another.
        Point position = e.GetPosition(LibraryList);
        if (!new Rect(LibraryList.RenderSize).Contains(position)) {
            ClearShelfDropLine();
        }
    }

    private void OnShelfDrop(object sender, DragEventArgs e) {
        if (e.Data.GetData(ShelfDragFormat) is not string dragged) {
            return;
        }
        e.Handled = true;
        ClearShelfDropLine();
        if ((ShelfDropTarget(e) is not (GroupItem Item, ShelfGroup Shelf, bool Below) target) || SameShelf(target.Shelf.Key, dragged)) {
            return;
        }
        // Worked out on all the shelves, so the ones a search or filter hides keep their places.
        List<string> order = Library.ShelfOrder(_library, _settings.ShelfOrder);
        order.RemoveAll(key => SameShelf(key, dragged));
        int place = order.FindIndex(key => SameShelf(key, target.Shelf.Key));
        if (place < 0) {
            return;
        }
        order.Insert(target.Below ? (place + 1) : place, dragged);
        // Shelves that are away for now (a drive that is not plugged in) are remembered for when they return.
        order.AddRange(_settings.ShelfOrder.Where(key => !order.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList());
        _settings.ShelfOrder = order;
        SettingsStore.Save(_settings);

        ScrollViewer? viewer = FindShelfScroller(LibraryList);
        double offset = viewer?.VerticalOffset ?? 0;
        ShowLibrary();
        // Showing the shelves again starts at the top; go back to where the reader was.
        Dispatcher.BeginInvoke(() => FindShelfScroller(LibraryList)?.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The shelf under the pointer, and whether the pointer is in its lower half (so the drop goes after it).</summary>
    private static (GroupItem Item, ShelfGroup Shelf, bool Below)? ShelfDropTarget(DragEventArgs e) {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not (null or GroupItem)) {
            current = (current is Visual) ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if ((current is not GroupItem item) || ((item.DataContext ?? item.Content) is not CollectionViewGroup { Name: ShelfGroup shelf })) {
            return null;
        }
        return (item, shelf, e.GetPosition(item).Y > (item.ActualHeight / 2));
    }

    /// <summary>Scrolls the shelves while something is dragged near the top or bottom edge, to reach shelves out of view.</summary>
    private void ScrollShelvesNearEdge(double y) {
        if (FindShelfScroller(LibraryList) is not ScrollViewer viewer) {
            return;
        }
        // A list that scrolls by whole items counts in items rather than pixels.
        double step = viewer.CanContentScroll ? 1 : 18;
        if (y < ShelfScrollEdge) {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - step);
        }
        else if (y > (LibraryList.ActualHeight - ShelfScrollEdge)) {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + step);
        }
    }

    private static ScrollViewer? FindShelfScroller(DependencyObject parent) {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if ((child as ScrollViewer ?? FindShelfScroller(child)) is ScrollViewer viewer) {
                return viewer;
            }
        }
        return null;
    }

    private void ClearShelfDropLine() {
        if (_shelfDropLine is not null) {
            AdornerLayer.GetAdornerLayer(_shelfDropLine.AdornedElement)?.Remove(_shelfDropLine);
            _shelfDropLine = null;
        }
    }
}

/// <summary>A line across the top or bottom of a shelf, showing where the dragged shelf would land.</summary>
internal sealed class ShelfDropLine(UIElement shelf, bool below, Brush brush) : Adorner(shelf) {
    public bool Below => below;

    protected override void OnRender(DrawingContext drawingContext) {
        Size size = AdornedElement.RenderSize;
        double y = below ? size.Height : 0;
        drawingContext.DrawLine(new Pen(brush, 2), new Point(8, y), new Point(Math.Max(8, size.Width - 8), y));
    }
}
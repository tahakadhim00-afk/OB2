using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OB2.App.Infrastructure;

/// <summary>
/// Desktop-style rubber-band selection for a <see cref="DataGrid"/>: pressing the mouse on the
/// empty space below the rows and dragging draws a translucent rectangle, and every row it
/// touches is selected. Ctrl or Shift adds to the selection instead of replacing it, as
/// Explorer does.
///
/// Attach with <c>infra:RubberBandSelection.IsEnabled="True"</c>. The grid must allow more than
/// one selected row; with <see cref="DataGridSelectionMode.Single"/> the drag does nothing.
/// </summary>
internal static class RubberBandSelection
{
    /// <summary>Movement in device-independent pixels before a press counts as a drag, not a click.</summary>
    private static readonly double DragThreshold = Math.Max(SystemParameters.MinimumHorizontalDragDistance, 4);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(RubberBandSelection), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(DragState), typeof(RubberBandSelection));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            // Preview, because the DataGrid handles PreviewMouseLeftButtonDown on rows itself.
            grid.PreviewMouseLeftButtonDown += OnMouseDown;
            grid.PreviewMouseMove += OnMouseMove;
            grid.PreviewMouseLeftButtonUp += OnMouseUp;
            grid.LostMouseCapture += OnLostCapture;
        }
        else
        {
            grid.PreviewMouseLeftButtonDown -= OnMouseDown;
            grid.PreviewMouseMove -= OnMouseMove;
            grid.PreviewMouseLeftButtonUp -= OnMouseUp;
            grid.LostMouseCapture -= OnLostCapture;
        }
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.SelectionMode == DataGridSelectionMode.Single || grid.ItemsSource is null)
        {
            return;
        }

        // Only the empty space below the last row starts a band. A press on a row, a header, a
        // scroll bar or the detail panel keeps its own meaning (click to select, drag to
        // reorder a column, scroll).
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<DataGridRow>(source) is not null
            || FindAncestor<DataGridColumnHeader>(source) is not null
            || FindAncestor<ScrollBar>(source) is not null)
        {
            return;
        }

        var presenter = FindDescendant<ScrollContentPresenter>(grid);
        if (presenter is null)
        {
            return;
        }

        grid.SetValue(StateProperty, new DragState(presenter, e.GetPosition(presenter), grid.SelectedItems.Cast<object>().ToList()));
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.GetValue(StateProperty) is not DragState state)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Stop(grid);
            return;
        }

        var point = e.GetPosition(state.Presenter);
        if (!state.IsDragging)
        {
            if (Math.Abs(point.X - state.Origin.X) < DragThreshold && Math.Abs(point.Y - state.Origin.Y) < DragThreshold)
            {
                return;
            }

            // Past the threshold: this is a band, so take the mouse and draw.
            state.IsDragging = true;
            state.Begin(grid);
        }

        state.MoveTo(point);
        Apply(grid, state);
        e.Handled = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.GetValue(StateProperty) is not DragState state)
        {
            return;
        }

        // A drag ends here; a plain click falls through to the grid, which clears the selection.
        var wasDragging = state.IsDragging;
        Stop(grid);
        if (wasDragging)
        {
            e.Handled = true;
        }
    }

    private static void OnLostCapture(object sender, MouseEventArgs e) => Stop((DataGrid)sender);

    private static void Stop(DataGrid grid)
    {
        if (grid.GetValue(StateProperty) is DragState state)
        {
            grid.ClearValue(StateProperty);
            state.Dispose(grid);
        }
    }

    /// <summary>Selects every row the band touches, keeping what Ctrl or Shift started with.</summary>
    private static void Apply(DataGrid grid, DragState state)
    {
        var band = state.Band;
        var additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        var keep = additive ? state.Original : [];

        // Rows are virtualized, so only the realized ones can be measured. That is enough:
        // a row with no container is outside the viewport, and so outside the band.
        var hit = new List<object>();
        foreach (var item in grid.Items)
        {
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow { IsVisible: true } row)
            {
                continue;
            }

            var topLeft = row.TransformToAncestor(state.Presenter).Transform(default);
            var bounds = new Rect(topLeft, row.RenderSize);
            if (bounds.IntersectsWith(band))
            {
                hit.Add(item);
            }
        }

        var wanted = new HashSet<object>(keep);
        wanted.UnionWith(hit);

        // Changing SelectedItems one by one raises SelectionChanged per row, so only the
        // difference is applied; an unchanged drag over the same rows touches nothing.
        var selected = grid.SelectedItems;
        for (var i = selected.Count - 1; i >= 0; i--)
        {
            var item = selected[i]!;
            if (!wanted.Contains(item))
            {
                selected.Remove(item);
            }
        }

        foreach (var item in wanted)
        {
            if (!selected.Contains(item))
            {
                selected.Add(item);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? from) where T : DependencyObject
    {
        for (var node = from; node is not null; node = VisualTreeHelperParent(node))
        {
            if (node is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>The visual parent, stepping out of a popup or template through the logical tree.</summary>
    private static DependencyObject? VisualTreeHelperParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>One drag: where it started, what was selected before, and the rectangle on screen.</summary>
    private sealed class DragState(ScrollContentPresenter presenter, Point origin, IReadOnlyList<object> original)
    {
        private readonly Rectangle _visual = new()
        {
            // System colours, so high contrast and the user's accent colour both apply
            // (NFR Accessibility). Same shape as the desktop's band: tinted fill, solid edge.
            Fill = new SolidColorBrush(SystemColors.HighlightColor) { Opacity = 0.25 },
            Stroke = SystemColors.HighlightBrush,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        private AdornerLayer? _layer;
        private BandAdorner? _adorner;

        public ScrollContentPresenter Presenter { get; } = presenter;

        public Point Origin { get; } = origin;

        public IReadOnlyList<object> Original { get; } = original;

        public bool IsDragging { get; set; }

        public Rect Band { get; private set; }

        public void Begin(DataGrid grid)
        {
            grid.CaptureMouse();
            _layer = AdornerLayer.GetAdornerLayer(Presenter);
            if (_layer is not null)
            {
                _adorner = new BandAdorner(Presenter, _visual);
                _layer.Add(_adorner);
            }
        }

        public void MoveTo(Point point)
        {
            // Clamped to the presenter so the band never draws over the headers or the splitter.
            var x = Math.Clamp(point.X, 0, Presenter.ActualWidth);
            var y = Math.Clamp(point.Y, 0, Presenter.ActualHeight);
            Band = new Rect(new Point(Math.Min(Origin.X, x), Math.Min(Origin.Y, y)), new Size(Math.Abs(x - Origin.X), Math.Abs(y - Origin.Y)));

            _visual.Width = Band.Width;
            _visual.Height = Band.Height;
            _visual.Margin = new Thickness(Band.X, Band.Y, 0, 0);
        }

        public void Dispose(DataGrid grid)
        {
            if (IsDragging)
            {
                grid.ReleaseMouseCapture();
            }

            if (_layer is not null && _adorner is not null)
            {
                _layer.Remove(_adorner);
            }

            _layer = null;
            _adorner = null;
        }
    }

    /// <summary>Hosts the rectangle in the adorner layer, above the rows and below nothing.</summary>
    private sealed class BandAdorner : Adorner
    {
        private readonly VisualCollection _children;

        public BandAdorner(UIElement adornedElement, UIElement child)
            : base(adornedElement)
        {
            // The collection first: setting a force-inherited property such as IsHitTestVisible
            // walks the visual children, and WPF asks for the count while the constructor runs.
            _children = new VisualCollection(this) { child };
            IsHitTestVisible = false;
        }

        protected override int VisualChildrenCount => _children?.Count ?? 0;

        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in _children)
            {
                child.Arrange(new Rect(finalSize));
            }

            return finalSize;
        }
    }
}

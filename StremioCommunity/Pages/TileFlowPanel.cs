using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace StremioCommunity.Pages
{
    /// <summary>
    /// A simple flow panel for the home page grid: children are laid out left-to-right,
    /// wrapping to a new row after <see cref="Columns"/> of them. UWP (WinUI 2) has no
    /// WrapPanel or UniformGrid, so the Home poster grid uses this instead.
    /// </summary>
    public sealed class TileFlowPanel : Panel
    {
        private double _columnWidth = 285; // tile width (245 poster + margins) used as the measure unit

        /// <summary>Number of columns across (the home grid uses 4).</summary>
        public int Columns
        {
            get
            {
                return _columns;
            }

            set
            {
                if (value < 1)
                {
                    value = 1;
                }

                if (_columns == value)
                {
                    return;
                }

                _columns = value;
                InvalidateMeasure();
            }
        }

        private int _columns = 4;

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsPositiveInfinity(availableSize.Width) ? 2000 : availableSize.Width;
            _columnWidth = width / Columns;
            var available = new Size(_columnWidth, double.PositiveInfinity);

            foreach (UIElement child in Children)
            {
                child.Measure(available);
            }

            return new Size(width, 0);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var index = 0;
            var maxBottom = 0.0;

            foreach (UIElement child in Children)
            {
                if (child == null || child.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var row = index / Columns;
                var col = index % Columns;
                index++;

                var desired = child.DesiredSize;
                var top = row * desired.Height;
                var left = col * _columnWidth + (_columnWidth - desired.Width) / 2.0;
                if (left < 0)
                {
                    left = 0;
                }

                var bottom = top + desired.Height;
                if (bottom > maxBottom)
                {
                    maxBottom = bottom;
                }

                child.Arrange(new Rect(left, top, desired.Width, desired.Height));
            }

            return new Size(finalSize.Width, maxBottom);
        }
    }
}
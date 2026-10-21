using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MSSQLTool.Modules
{
    internal sealed class HyperlinkUnderlineAdorner : Adorner
    {
        private Rect _rect;
        private readonly Pen _pen;

        public HyperlinkUnderlineAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
            var brush = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            brush.Freeze();
            _pen = new Pen(brush, 1.2);
            _pen.Freeze();
        }

        public void UpdateBounds(Rect rect)
        {
            if (_rect != rect)
            {
                _rect = rect;
                InvalidateVisual();
            }
        }

        public void Clear()
        {
            if (!_rect.IsEmpty)
            {
                _rect = Rect.Empty;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!_rect.IsEmpty && _rect.Width > 0)
            {
                double y = Math.Round(_rect.Bottom) - 0.5;
                dc.DrawLine(_pen, new Point(Math.Round(_rect.Left), y), new Point(Math.Round(_rect.Right), y));
            }
        }
    }
}

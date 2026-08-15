using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Shadowsocks.View
{
    internal sealed class TrafficChartControl : Control
    {
        private static readonly Color InboundColor = Color.FromArgb(255, 128, 0);
        private static readonly Color OutboundColor = Color.FromArgb(128, 128, 255);
        private static readonly Color GridColor = Color.LightGray;

        private float[] _inboundPoints = [];
        private float[] _outboundPoints = [];
        private float _maximum = 1F;
        private string _unitName = string.Empty;
        private string _inboundValue = string.Empty;
        private string _outboundValue = string.Empty;
        private string _inboundLegendText = "Inbound";
        private string _outboundLegendText = "Outbound";

        public TrafficChartControl()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);

            BackColor = Color.White;
        }

        public void SetLegendText(string inbound, string outbound)
        {
            _inboundLegendText = inbound ?? string.Empty;
            _outboundLegendText = outbound ?? string.Empty;
            Invalidate();
        }

        public void SetData(
            IReadOnlyCollection<float> inboundPoints,
            IReadOnlyCollection<float> outboundPoints,
            float maximum,
            string unitName,
            string inboundValue,
            string outboundValue)
        {
            _inboundPoints = inboundPoints is null ? [] : inboundPoints.ToArray();
            _outboundPoints = outboundPoints is null ? [] : outboundPoints.ToArray();
            _maximum = Math.Max(1F, maximum);
            _unitName = unitName ?? string.Empty;
            _inboundValue = inboundValue ?? string.Empty;
            _outboundValue = outboundValue ?? string.Empty;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle client = ClientRectangle;
            if (client.Width <= 8 || client.Height <= 8)
            {
                return;
            }

            int axisWidth = Math.Min(58, Math.Max(38, client.Width / 8));
            int legendWidth = Math.Min(160, Math.Max(100, client.Width / 3));
            int top = 4;
            int bottom = 4;
            Rectangle plot = new(
                axisWidth,
                top,
                Math.Max(1, client.Width - axisWidth - legendWidth - 4),
                Math.Max(1, client.Height - top - bottom));

            using Pen gridPen = new(GridColor, 1F);
            using Pen inboundPen = new(InboundColor, 2F);
            using Pen outboundPen = new(OutboundColor, 2F);
            using SolidBrush textBrush = new(ForeColor);
            using SolidBrush mutedBrush = new(SystemColors.GrayText);

            DrawGrid(graphics, plot, gridPen);
            DrawYAxisLabels(graphics, plot, textBrush);
            DrawSeries(graphics, plot, _inboundPoints, inboundPen);
            DrawSeries(graphics, plot, _outboundPoints, outboundPen);
            DrawLegend(graphics, plot.Right + 8, client.Height, inboundPen, outboundPen, textBrush, mutedBrush);
        }

        private static void DrawGrid(Graphics graphics, Rectangle plot, Pen gridPen)
        {
            const int horizontalLines = 4;
            for (int i = 0; i <= horizontalLines; i++)
            {
                float y = plot.Top + (plot.Height - 1) * i / (float)horizontalLines;
                graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            }

            const int verticalLines = 6;
            for (int i = 0; i <= verticalLines; i++)
            {
                float x = plot.Left + (plot.Width - 1) * i / (float)verticalLines;
                graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            }
        }

        private void DrawYAxisLabels(Graphics graphics, Rectangle plot, Brush brush)
        {
            string maxLabel = $"{_maximum:0.##} {_unitName}";
            SizeF maxSize = graphics.MeasureString(maxLabel, Font);
            graphics.DrawString(maxLabel, Font, brush, Math.Max(0, plot.Left - maxSize.Width - 3), plot.Top - 1);

            if (plot.Height >= Font.Height * 2 + 4)
            {
                string zeroLabel = $"0 {_unitName}";
                SizeF zeroSize = graphics.MeasureString(zeroLabel, Font);
                graphics.DrawString(
                    zeroLabel,
                    Font,
                    brush,
                    Math.Max(0, plot.Left - zeroSize.Width - 3),
                    plot.Bottom - zeroSize.Height + 1);
            }
        }

        private void DrawSeries(Graphics graphics, Rectangle plot, float[] points, Pen pen)
        {
            if (points.Length == 0)
            {
                return;
            }

            PointF[] mapped = new PointF[points.Length];
            float denominator = Math.Max(1, points.Length - 1);
            for (int i = 0; i < points.Length; i++)
            {
                float normalized = Math.Clamp(points[i] / _maximum, 0F, 1F);
                mapped[i] = new PointF(
                    plot.Left + (plot.Width - 1) * i / denominator,
                    plot.Bottom - 1 - (plot.Height - 1) * normalized);
            }

            if (mapped.Length == 1)
            {
                graphics.DrawEllipse(pen, mapped[0].X - 1F, mapped[0].Y - 1F, 2F, 2F);
            }
            else
            {
                graphics.DrawLines(pen, mapped);
            }
        }

        private void DrawLegend(
            Graphics graphics,
            int x,
            int height,
            Pen inboundPen,
            Pen outboundPen,
            Brush textBrush,
            Brush mutedBrush)
        {
            int lineHeight = Math.Max(Font.Height + 2, 14);
            int firstY = Math.Max(2, (height - lineHeight * 2) / 2);

            DrawLegendRow(graphics, x, firstY, inboundPen, _inboundLegendText, _inboundValue, textBrush, mutedBrush);
            DrawLegendRow(graphics, x, firstY + lineHeight, outboundPen, _outboundLegendText, _outboundValue, textBrush, mutedBrush);
        }

        private void DrawLegendRow(
            Graphics graphics,
            int x,
            int y,
            Pen pen,
            string label,
            string value,
            Brush textBrush,
            Brush mutedBrush)
        {
            const int swatchWidth = 14;
            int midY = y + Font.Height / 2;
            graphics.DrawLine(pen, x, midY, x + swatchWidth, midY);

            string safeLabel = string.IsNullOrWhiteSpace(label) ? string.Empty : label;
            graphics.DrawString(safeLabel, Font, textBrush, x + swatchWidth + 4, y);

            if (!string.IsNullOrWhiteSpace(value))
            {
                SizeF labelSize = graphics.MeasureString(safeLabel, Font);
                graphics.DrawString(value, Font, mutedBrush, x + swatchWidth + 8 + labelSize.Width, y);
            }
        }
    }
}

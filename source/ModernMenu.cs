using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DesktopTodo
{
    enum MenuGlyph { None, Settings, Undo, Position, Export, Folder, Exit, Detail, Star, Complete, Restore, Delete, Edit, List, Plus }

    sealed class ModernMenuItem : ToolStripMenuItem
    {
        public readonly MenuGlyph Glyph;
        public readonly bool Destructive;
        public bool ActiveGlyph;
        readonly float scale;
        public ModernMenuItem(string text, MenuGlyph glyph, Action action, float scale, string shortcut, bool destructive)
            : base(text)
        {
            Glyph = glyph; Destructive = destructive; this.scale = scale;
            ShortcutKeyDisplayString = shortcut ?? "";
            Margin = Padding.Empty; Padding = Padding.Empty;
            if (action != null) Click += delegate { action(); };
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            var font = Owner == null ? SystemFonts.MenuFont : Owner.Font;
            int textWidth = TextRenderer.MeasureText(Text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
            int shortcutWidth = string.IsNullOrEmpty(ShortcutKeyDisplayString) ? 0 : TextRenderer.MeasureText(ShortcutKeyDisplayString, font, Size.Empty, TextFormatFlags.NoPadding).Width + D(34);
            return new Size(Math.Max(D(232), textWidth + shortcutWidth + D(72)), D(38));
        }
        int D(int value) { return (int)Math.Round(value * scale); }
    }

    sealed class ModernMenuSeparator : ToolStripSeparator
    {
        readonly float scale;
        public ModernMenuSeparator(float scale) { this.scale = scale; Margin = Padding.Empty; }
        public override Size GetPreferredSize(Size proposedSize) { return new Size(1, (int)Math.Round(11 * scale)); }
    }

    sealed class ModernMenu : ContextMenuStrip
    {
        readonly float scale;
        readonly Font menuFont;
        public ModernMenuItem DeleteCommand;
        public ModernMenu(float scale)
        {
            this.scale = scale;
            menuFont = new Font("Microsoft YaHei UI", 9F); Font = menuFont;
            BackColor = Color.FromArgb(250, 250, 250); ForeColor = Color.FromArgb(32, 32, 32);
            ShowImageMargin = false; ShowCheckMargin = false;
            Padding = new Padding(D(6)); DropShadowEnabled = true;
            Renderer = new ModernMenuRenderer(scale);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
        protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
        {
            int width = D(232), height = Padding.Vertical;
            foreach (ToolStripItem item in Items)
            {
                var preferred = item.GetPreferredSize(Size.Empty);
                width = Math.Max(width, preferred.Width); height += preferred.Height + item.Margin.Vertical;
            }
            var area=(SourceControl==null ? Screen.PrimaryScreen : Screen.FromControl(SourceControl)).WorkingArea;
            int maxWidth=Math.Max(1,area.Width-D(24)), maxHeight=Math.Max(1,area.Height-D(24));
            MaximumSize=new Size(maxWidth,maxHeight);
            MinimumSize = new Size(Math.Min(width + Padding.Horizontal,maxWidth),Math.Min(height,maxHeight));
            base.OnOpening(e);
        }
        public ModernMenuItem AddCommand(string text, MenuGlyph glyph, Action action, string shortcut = null, bool destructive = false)
        {
            var item = new ModernMenuItem(text, glyph, action, scale, shortcut, destructive); Items.Add(item); return item;
        }
        public void AddDivider() { Items.Add(new ModernMenuSeparator(scale)); }
        internal bool DispatchCommandKey(Keys keyData)
        {
            if (!Visible || keyData != Keys.Delete || DeleteCommand == null || !DeleteCommand.Enabled) return false;
            DeleteCommand.Select(); DeleteCommand.PerformClick(); return true;
        }
        protected override bool ProcessCmdKey(ref Message m, Keys keyData)
        { return DispatchCommandKey(keyData) || base.ProcessCmdKey(ref m, keyData); }
        int D(int value) { return (int)Math.Round(value * scale); }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width < 1 || Height < 1) return;
            using (var path = Shapes.Round(new RectangleF(0, 0, Width, Height), D(9)))
            { var old = Region; Region = new Region(path); if (old != null) old.Dispose(); }
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && menuFont != null) menuFont.Dispose(); }
    }

    sealed class ModernMenuRenderer : ToolStripRenderer
    {
        readonly float scale;
        static readonly Color Surface = Color.FromArgb(250, 250, 250), Text = Color.FromArgb(32, 32, 32), Muted = Color.FromArgb(105, 105, 105), Disabled = Color.FromArgb(165, 165, 165), Danger = Color.FromArgb(190, 38, 48);
        public ModernMenuRenderer(float scale) { this.scale = scale; }
        int D(int value) { return (int)Math.Round(value * scale); }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Surface); }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shapes.Round(new RectangleF(.5F, .5F, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), D(9)))
            using (var pen = new Pen(Color.FromArgb(218, 218, 218))) e.Graphics.DrawPath(pen, path);
        }
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = e.Item as ModernMenuItem;
            if (item == null) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (item.Enabled && item.Selected)
                using (var path = Shapes.Round(new RectangleF(0, D(1), item.Width - 1, item.Height - D(2)), D(5)))
                using (var brush = new SolidBrush(Color.FromArgb(235, 236, 239))) e.Graphics.FillPath(brush, path);
            Color color = !item.Enabled ? Disabled : item.Destructive ? Danger : Text;
            DrawGlyph(e.Graphics, new Rectangle(D(11), (item.Height - D(18)) / 2, D(18), D(18)), item.Glyph, color, item.ActiveGlyph);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = e.Item as ModernMenuItem;
            if (item == null) { base.OnRenderItemText(e); return; }
            Color color = !item.Enabled ? Disabled : item.Destructive ? Danger : Text;
            var font = e.TextFont;
            int shortcutWidth = string.IsNullOrEmpty(item.ShortcutKeyDisplayString) ? 0 : TextRenderer.MeasureText(e.Graphics, item.ShortcutKeyDisplayString, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var rect = new Rectangle(D(40), 0, Math.Max(1, item.Width - D(54) - (shortcutWidth == 0 ? 0 : shortcutWidth + D(24))), item.Height);
            TextRenderer.DrawText(e.Graphics, item.Text, font, rect, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (shortcutWidth > 0)
                TextRenderer.DrawText(e.Graphics, item.ShortcutKeyDisplayString, font, new Rectangle(item.Width - D(12) - shortcutWidth, 0, shortcutWidth, item.Height), item.Enabled ? Muted : Disabled,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        { using (var pen = new Pen(Color.FromArgb(228, 228, 228))) e.Graphics.DrawLine(pen, D(8), e.Item.Height / 2, e.Item.Width - D(8), e.Item.Height / 2); }
        static void DrawGlyph(Graphics graphics, Rectangle bounds, MenuGlyph glyph, Color color, bool active)
        {
            var state = graphics.Save(); graphics.TranslateTransform(bounds.X, bounds.Y); graphics.ScaleTransform(bounds.Width / 20F, bounds.Height / 20F);
            using (var pen = new Pen(color, 1.35F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (glyph)
                {
                    case MenuGlyph.List:
                        for(int i=0;i<3;i++) { graphics.DrawEllipse(pen,2,4+i*5,1,1); graphics.DrawLine(pen,7,4.5F+i*5,18,4.5F+i*5); } break;
                    case MenuGlyph.Plus:
                        graphics.DrawLine(pen,10,3,10,17); graphics.DrawLine(pen,3,10,17,10); break;
                    case MenuGlyph.Settings:
                        var gear = new PointF[32];
                        for (int i = 0; i < gear.Length; i++) { double angle = i * Math.PI / 16; float r = (i % 4 == 0 || i % 4 == 3) ? 8 : 6.4F; gear[i] = new PointF(10 + (float)Math.Cos(angle) * r, 10 + (float)Math.Sin(angle) * r); }
                        graphics.DrawPolygon(pen, gear); graphics.DrawEllipse(pen, 7, 7, 6, 6); break;
                    case MenuGlyph.Undo:
                        graphics.DrawArc(pen, 4, 5, 12, 11, 205, 245); graphics.DrawLines(pen, new[] { new PointF(3, 3), new PointF(3, 8), new PointF(8, 8) }); break;
                    case MenuGlyph.Position:
                        graphics.DrawLine(pen, 10, 2, 10, 18); graphics.DrawLine(pen, 2, 10, 18, 10);
                        graphics.DrawLines(pen, new[] { new PointF(7, 5), new PointF(10, 2), new PointF(13, 5) }); graphics.DrawLines(pen, new[] { new PointF(7, 15), new PointF(10, 18), new PointF(13, 15) });
                        graphics.DrawLines(pen, new[] { new PointF(5, 7), new PointF(2, 10), new PointF(5, 13) }); graphics.DrawLines(pen, new[] { new PointF(15, 7), new PointF(18, 10), new PointF(15, 13) }); break;
                    case MenuGlyph.Export:
                        graphics.DrawLines(pen, new[] { new PointF(3, 10), new PointF(3, 17), new PointF(17, 17), new PointF(17, 10) });
                        graphics.DrawLine(pen, 10, 13, 10, 2); graphics.DrawLines(pen, new[] { new PointF(6, 6), new PointF(10, 2), new PointF(14, 6) }); break;
                    case MenuGlyph.Folder:
                        graphics.DrawPolygon(pen, new[] { new PointF(2, 5), new PointF(8, 5), new PointF(10, 7), new PointF(18, 7), new PointF(18, 16), new PointF(2, 16) }); break;
                    case MenuGlyph.Exit:
                        graphics.DrawLines(pen, new[] { new PointF(9, 3), new PointF(3, 3), new PointF(3, 17), new PointF(9, 17) }); graphics.DrawLine(pen, 8, 10, 18, 10);
                        graphics.DrawLines(pen, new[] { new PointF(14, 6), new PointF(18, 10), new PointF(14, 14) }); break;
                    case MenuGlyph.Detail:
                        graphics.DrawRectangle(pen, 4, 2, 12, 16); graphics.DrawLine(pen, 7, 7, 13, 7); graphics.DrawLine(pen, 7, 10, 13, 10); graphics.DrawLine(pen, 7, 13, 11, 13); break;
                    case MenuGlyph.Star:
                        var star = new PointF[10];
                        for (int i = 0; i < 10; i++) { double a = -Math.PI / 2 + i * Math.PI / 5; float r = i % 2 == 0 ? 8 : 3.7F; star[i] = new PointF(10 + (float)Math.Cos(a) * r, 10 + (float)Math.Sin(a) * r); }
                        if (active) using (var brush = new SolidBrush(color)) graphics.FillPolygon(brush, star); else graphics.DrawPolygon(pen, star); break;
                    case MenuGlyph.Complete:
                        graphics.DrawEllipse(pen, 2, 2, 16, 16); graphics.DrawLines(pen, new[] { new PointF(6, 10), new PointF(9, 13), new PointF(14, 7) }); break;
                    case MenuGlyph.Restore:
                        graphics.DrawArc(pen, 3, 3, 14, 14, 210, 285); graphics.DrawLines(pen, new[] { new PointF(2, 2), new PointF(2, 7), new PointF(7, 7) }); break;
                    case MenuGlyph.Delete:
                        graphics.DrawLine(pen, 3, 5, 17, 5); graphics.DrawLines(pen, new[] { new PointF(5, 5), new PointF(6, 18), new PointF(14, 18), new PointF(15, 5) });
                        graphics.DrawLines(pen, new[] { new PointF(7, 5), new PointF(7, 2), new PointF(13, 2), new PointF(13, 5) }); graphics.DrawLine(pen, 8, 8, 8, 14); graphics.DrawLine(pen, 12, 8, 12, 14); break;
                    case MenuGlyph.Edit:
                        graphics.DrawPolygon(pen, new[] { new PointF(3, 13), new PointF(13, 3), new PointF(17, 7), new PointF(7, 17), new PointF(2, 18) }); graphics.DrawLine(pen, 11, 5, 15, 9); break;
                }
            }
            graphics.Restore(state);
        }
    }
}

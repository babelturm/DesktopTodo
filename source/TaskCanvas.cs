using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace DesktopTodo
{
    enum TaskPart { Body, Check, Star }
    class TaskPointerEventArgs : EventArgs
    {
        public readonly string Id; public readonly TaskPart Part; public readonly Point Location;
        public TaskPointerEventArgs(string id, TaskPart part, Point location) { Id = id; Part = part; Location = location; }
    }
    // One HWND and one double-buffered scene for the whole task list. Scrolling
    // changes an offset only; there are no child windows to move or rebuild.
    class TaskCanvas : Control
    {
        public sealed class Row
        {
            public Todo Task; public string Meta; public Rectangle Bounds, TitleBounds, MetaBounds, CheckBounds, StarBounds;
            public object Tag { get { return Task; } }
            public int Right { get { return Bounds.Right; } }
            public int Bottom { get { return Bounds.Bottom; } }
            public int Height { get { return Bounds.Height; } }
        }
        readonly List<Row> rows = new List<Row>();
        List<Todo> tasks = new List<Todo>();
        Func<Todo, string> metadata;
        readonly Font titleFont = new Font("Microsoft YaHei UI", 10.5F), doneFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Strikeout), metaFont = new Font("Microsoft YaHei UI", 8F);
        readonly Palette colors = new Palette(false);
        string emptyText = "", selectedId, pressedId;
        TaskPart pressedPart;
        int offset, contentHeight, wheelRemainder, dragGrab;
        float scale = 1;
        bool draggingRail, pressedCompleted;
        bool pointerInside;
        Point pointerLocation;
        const string CompletedSelection = "__completed_section";
        Rectangle completedBounds;
        public bool CompletedExpanded { get; private set; }
        public int CompletedCount { get; private set; }
        public Rectangle CompletedBounds { get { return completedBounds; } }
        public int LayoutPasses { get; private set; }
        public event EventHandler<TaskPointerEventArgs> PrimaryAction, ContextAction;
        public event EventHandler CompletedToggleRequested;
        public IEnumerable<Row> Items { get { return rows; } }
        public bool HasOverflow { get { return contentHeight > ClientSize.Height; } }
        public int RailLeft { get { return ClientSize.Width - D(8); } }
        public int CardWidth { get { return Math.Max(1, ClientSize.Width - D(14)); } }
        public int ScrollOffset { get { return offset; } }
        int MaxOffset { get { return Math.Max(0, contentHeight - ClientSize.Height); } }
        int D(int value) { return (int)Math.Round(value * scale); }
        public TaskCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; AccessibleRole = AccessibleRole.List; AccessibleName = "任务清单"; Cursor = Cursors.Default;
            Resize += delegate { LayoutItems(); };
        }
        public void SetTasks(IEnumerable<Todo> values, Func<Todo, string> getMetadata, string empty, bool reset, bool expanded = false)
        {
            Row anchor = rows.FirstOrDefault(r => r.Bottom > offset); string anchorId = anchor == null ? null : anchor.Task.Id;
            int delta = anchor == null ? 0 : offset - anchor.Bounds.Top;
            tasks = values.ToList(); metadata = getMetadata; emptyText = empty; CompletedExpanded = expanded; CompletedCount = tasks.Count(t => t.Done); LayoutItems();
            if (reset) offset = 0;
            else if (anchorId != null) { var replacement = rows.FirstOrDefault(r => r.Task.Id == anchorId); if (replacement != null) offset = replacement.Bounds.Top + delta; }
            offset = Math.Max(0, Math.Min(MaxOffset, offset)); RefreshPointerCursor(); Invalidate();
        }
        public void LayoutItems()
        {
            if (IsDisposed || ClientSize.Width < 1) return;
            using (var g = CreateGraphics()) scale = g.DpiX / 96F;
            LayoutPasses++; LayoutPass(CardWidth);
            offset = Math.Max(0, Math.Min(MaxOffset, offset)); RefreshPointerCursor(); Invalidate();
        }
        void LayoutPass(int width)
        {
            rows.Clear(); completedBounds = Rectangle.Empty; int y = 0, gap = D(4), tw = Math.Max(D(60), width - D(89));
            foreach (Todo task in tasks.Where(t => !t.Done).Concat(tasks.Where(t => t.Done)))
            {
                if (task.Done && completedBounds.IsEmpty)
                {
                    if (y > 0) y += D(10);
                    string caption = "已完成 · " + CompletedCount;
                    int headerWidth = Math.Min(width, TextRenderer.MeasureText(caption, Font).Width + D(48));
                    completedBounds = new Rectangle(0, y, headerWidth, D(32)); y += D(40);
                }
                if (task.Done && !CompletedExpanded) continue;
                string meta = metadata == null ? "" : metadata(task);
                Font font = task.Done ? doneFont : titleFont;
                int th = Math.Max(D(24), TextRenderer.MeasureText(task.Text, font, new Size(tw, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height + D(3));
                int mh = string.IsNullOrEmpty(meta) ? 0 : D(20), content = th + mh, height = Math.Max(D(58), content + D(24));
                int top = y + (height - content) / 2;
                rows.Add(new Row { Task = task, Meta = meta, Bounds = new Rectangle(0, y, width, height), TitleBounds = new Rectangle(D(46), top, tw, th), MetaBounds = new Rectangle(D(46), top + th, tw, mh), CheckBounds = new Rectangle(D(5), y + (height - D(35)) / 2, D(35), D(35)), StarBounds = new Rectangle(width - D(38), y + (height - D(34)) / 2, D(30), D(34)) });
                y += height + gap;
            }
            contentHeight = Math.Max(0, y - gap);
        }
        public void ScrollBy(int amount) { offset = (int)Math.Max(0L, Math.Min((long)MaxOffset, (long)offset + amount)); RefreshPointerCursor(); Invalidate(); }
        public void ScrollToTask(string id)
        {
            var row = rows.FirstOrDefault(r => r.Task.Id == id); if (row == null) return;
            if (row.Bounds.Top < offset) offset = row.Bounds.Top;
            else if (row.Bottom > offset + ClientSize.Height) offset = row.Bottom - ClientSize.Height;
            offset = Math.Max(0, Math.Min(MaxOffset, offset)); RefreshPointerCursor(); Invalidate();
        }
        Rectangle View(Rectangle rect) { rect.Offset(0, -offset); return rect; }
        Rectangle Thumb()
        {
            if (!HasOverflow || Height < 1) return Rectangle.Empty;
            int height = Math.Min(ClientSize.Height, Math.Max(D(24), (int)((long)ClientSize.Height * ClientSize.Height / contentHeight)));
            int y = MaxOffset == 0 ? 0 : (int)((long)(ClientSize.Height - height) * offset / MaxOffset);
            return new Rectangle(RailLeft + D(2), y, D(4), height);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { WidgetPaint.Background(this, e.Graphics); }
        internal Bitmap RenderScene(Color initial)
        {
            var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            using (var screen = CreateGraphics()) bitmap.SetResolution(screen.DpiX, screen.DpiY);
            using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(initial); var args = new PaintEventArgs(graphics, ClientRectangle); OnPaintBackground(args); OnPaint(args); }
            return bitmap;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (tasks.Count == 0) TextRenderer.DrawText(g, emptyText, Font, new Rectangle(0, D(18), CardWidth, D(140)), Color.FromArgb(234, 239, 255), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            foreach (var row in rows)
            {
                Rectangle rect = View(row.Bounds); if (rect.Bottom < 0 || rect.Top >= Height) continue;
                using (var path = Shapes.Round(new RectangleF(rect.Left, rect.Top, rect.Width - 1, rect.Height - 1), D(7))) using (var brush = new SolidBrush(colors.Row)) g.FillPath(brush, path);
                if (Focused && ShowFocusCues && row.Task.Id == selectedId) using (var pen = new Pen(colors.Accent)) using (var path = Shapes.Round(new RectangleF(rect.X + 2, rect.Y + 2, rect.Width - 5, rect.Height - 5), D(5))) g.DrawPath(pen, path);
                DrawCheck(g, View(row.CheckBounds), row.Task.Done);
                TextRenderer.DrawText(g, row.Task.Text, row.Task.Done ? doneFont : titleFont, View(row.TitleBounds), row.Task.Done ? colors.Muted : colors.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                if (row.MetaBounds.Height > 0) TextRenderer.DrawText(g, row.Meta, metaFont, View(row.MetaBounds), colors.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TaskStar.Draw(g, View(row.StarBounds), row.Task.Important, colors.Accent, colors.Muted);
            }
            if (!completedBounds.IsEmpty)
            {
                var rect = View(completedBounds);
                using (var path = Shapes.Round(new RectangleF(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), D(5)))
                using (var brush = new SolidBrush(Color.FromArgb(218, 225, 251))) g.FillPath(brush, path);
                float x = rect.X + D(15), y = rect.Y + rect.Height / 2F;
                using (var pen = new Pen(Color.FromArgb(63,79,139), D(1)))
                    g.DrawLines(pen, CompletedExpanded ? new[] { new PointF(x-D(4),y-D(2)), new PointF(x,y+D(2)), new PointF(x+D(4),y-D(2)) } : new[] { new PointF(x-D(2),y-D(4)), new PointF(x+D(2),y), new PointF(x-D(2),y+D(4)) });
                TextRenderer.DrawText(g, "已完成 · " + CompletedCount, Font, new Rectangle(rect.X+D(30),rect.Y,rect.Width-D(34),rect.Height), Color.FromArgb(45,54,82), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if (Focused && ShowFocusCues && selectedId == CompletedSelection) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(rect, -D(3), -D(3)));
            }
            if (HasOverflow) { Rectangle thumb = Thumb(); using (var path = Shapes.Round(thumb, D(2))) using (var brush = new SolidBrush(Color.FromArgb(185, 88, 106, 164))) g.FillPath(brush, path); }
            base.OnPaint(e);
        }
        void DrawCheck(Graphics g, Rectangle bounds, bool done)
        {
            float d = bounds.Width * .56F; var circle = new RectangleF(bounds.X + (bounds.Width - d) / 2, bounds.Y + (bounds.Height - d) / 2, d, d);
            using (var pen = new Pen(done ? colors.Accent : Color.FromArgb(116, 121, 133), Math.Max(1.5F, bounds.Width / 22F)))
            {
                if (!done) g.DrawEllipse(pen, circle);
                else { using (var brush = new SolidBrush(colors.Accent)) g.FillEllipse(brush, circle); pen.Color = Color.White; g.DrawLines(pen, new[] { new PointF(circle.X + d * .25F, circle.Y + d * .51F), new PointF(circle.X + d * .44F, circle.Y + d * .7F), new PointF(circle.X + d * .77F, circle.Y + d * .33F) }); }
            }
        }
        Row Hit(Point point) { return !ClientRectangle.Contains(point) ? null : rows.FirstOrDefault(r => r.Bounds.Contains(point.X, point.Y + offset)); }
        bool HitCompleted(Point point) { return ClientRectangle.Contains(point) && !completedBounds.IsEmpty && View(completedBounds).Contains(point); }
        void RefreshPointerCursor()
        {
            bool interactive = Enabled && (draggingRail || (pointerInside && ClientRectangle.Contains(pointerLocation) &&
                (Hit(pointerLocation) != null || HitCompleted(pointerLocation) || (HasOverflow && pointerLocation.X >= RailLeft))));
            Cursor = interactive ? Cursors.Hand : Cursors.Default;
        }
        void TrackPointer(Point point) { pointerLocation = point; pointerInside = ClientRectangle.Contains(point); RefreshPointerCursor(); }
        protected override void OnMouseEnter(EventArgs e) { TrackPointer(PointToClient(MousePosition)); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { pointerInside = false; RefreshPointerCursor(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); RefreshPointerCursor(); }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { pointerInside = false; RefreshPointerCursor(); }
            else if (IsHandleCreated) TrackPointer(PointToClient(MousePosition));
        }
        TaskPart Part(Row row, Point point) { point.Offset(0, offset); return row.CheckBounds.Contains(point) ? TaskPart.Check : row.StarBounds.Contains(point) ? TaskPart.Star : TaskPart.Body; }
        internal void DispatchPointer(Point point, MouseButtons button)
        {
            if (HitCompleted(point))
            { if (button == MouseButtons.Left) ToggleCompleted(); return; }
            Row row = Hit(point); if (row == null) return;
            var args = new TaskPointerEventArgs(row.Task.Id, Part(row, point), point);
            if (button == MouseButtons.Right) { if (ContextAction != null) ContextAction(this, args); }
            else if (button == MouseButtons.Left) { selectedId = row.Task.Id; if (PrimaryAction != null) PrimaryAction(this, args); }
        }
        internal void ToggleCompleted()
        { selectedId = CompletedSelection; if (CompletedToggleRequested != null) CompletedToggleRequested(this, EventArgs.Empty); Invalidate(); }
        void MoveRail(int y)
        { Rectangle thumb = Thumb(); int travel = Math.Max(1, Height - thumb.Height); offset = (int)Math.Max(0L, Math.Min((long)MaxOffset, (long)(y - dragGrab) * MaxOffset / travel)); RefreshPointerCursor(); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); TrackPointer(e.Location); Focus();
            pressedCompleted = e.Button == MouseButtons.Left && HitCompleted(e.Location);
            if (e.Button == MouseButtons.Left && HasOverflow && e.X >= RailLeft)
            { draggingRail = true; Rectangle thumb = Thumb(); dragGrab = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2; Capture = true; MoveRail(e.Y); return; }
            var row = Hit(e.Location); pressedId = row == null ? null : row.Task.Id; if (row != null) pressedPart = Part(row, e.Location);
        }
        protected override void OnMouseMove(MouseEventArgs e) { TrackPointer(e.Location); if (draggingRail) MoveRail(e.Y); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            TrackPointer(e.Location);
            if (draggingRail) { draggingRail = false; Capture = false; RefreshPointerCursor(); return; }
            if (pressedCompleted) { pressedCompleted = false; if (e.Button == MouseButtons.Left && HitCompleted(e.Location)) DispatchPointer(e.Location, e.Button); pressedId = null; base.OnMouseUp(e); return; }
            var row = Hit(e.Location);
            if (row != null && row.Task.Id == pressedId && (e.Button == MouseButtons.Right || Part(row, e.Location) == pressedPart)) DispatchPointer(e.Location, e.Button);
            pressedId = null; base.OnMouseUp(e);
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { draggingRail = false; RefreshPointerCursor(); base.OnMouseCaptureChanged(e); }
        protected override void OnMouseWheel(MouseEventArgs e)
        { wheelRemainder += e.Delta; int ticks = wheelRemainder / 120; wheelRemainder %= 120; ScrollBy(-ticks * D(56)); var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true; }
        protected override bool IsInputKey(Keys keyData) { return keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            var entries = rows.Where(r => !r.Task.Done).Select(r => r.Task.Id).ToList();
            if (CompletedCount > 0) entries.Add(CompletedSelection);
            entries.AddRange(rows.Where(r => r.Task.Done).Select(r => r.Task.Id));
            int index = entries.IndexOf(selectedId);
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            { index = Math.Max(0, Math.Min(entries.Count - 1, index + (e.KeyCode == Keys.Down ? 1 : -1))); if (entries.Count > 0) { selectedId = entries[index]; if (selectedId == CompletedSelection) { if (completedBounds.Top < offset) offset = completedBounds.Top; else if (completedBounds.Bottom > offset + Height) offset = completedBounds.Bottom - Height; Invalidate(); } else ScrollToTask(selectedId); } e.Handled = true; }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && index >= 0)
            { if (selectedId == CompletedSelection) ToggleCompleted(); else if (PrimaryAction != null) PrimaryAction(this, new TaskPointerEventArgs(selectedId, e.KeyCode == Keys.Space ? TaskPart.Check : TaskPart.Body, Point.Empty)); e.Handled = true; }
            RefreshPointerCursor(); base.OnKeyDown(e);
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new CanvasAccessible(this); }
        class CanvasAccessible : ControlAccessibleObject
        {
            readonly TaskCanvas canvas;
            public CanvasAccessible(TaskCanvas canvas) : base(canvas) { this.canvas = canvas; }
            public override int GetChildCount() { return canvas.rows.Count + (canvas.CompletedCount > 0 ? 1 : 0); }
            public override AccessibleObject GetChild(int index)
            {
                int pending = canvas.rows.Count(r => !r.Task.Done);
                if (index < 0 || index >= GetChildCount()) return null;
                if (canvas.CompletedCount > 0 && index == pending) return new CompletedAccessible(canvas);
                return new RowAccessible(canvas, canvas.rows[index - (canvas.CompletedCount > 0 && index > pending ? 1 : 0)].Task.Id);
            }
        }
        class CompletedAccessible : AccessibleObject
        {
            readonly TaskCanvas canvas;
            public CompletedAccessible(TaskCanvas canvas) { this.canvas = canvas; }
            public override string Name { get { return "已完成 " + canvas.CompletedCount; } set { } }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override AccessibleStates State { get { return canvas.CompletedExpanded ? AccessibleStates.Expanded : AccessibleStates.Collapsed; } }
            public override AccessibleObject Parent { get { return canvas.AccessibilityObject; } }
            public override Rectangle Bounds { get { return canvas.RectangleToScreen(Rectangle.Intersect(canvas.View(canvas.completedBounds), canvas.ClientRectangle)); } }
            public override string DefaultAction { get { return canvas.CompletedExpanded ? "折叠" : "展开"; } }
            public override void DoDefaultAction() { if (canvas.InvokeRequired) canvas.BeginInvoke((Action)canvas.ToggleCompleted); else canvas.ToggleCompleted(); }
        }
        class RowAccessible : AccessibleObject
        {
            readonly TaskCanvas canvas; readonly string id;
            public RowAccessible(TaskCanvas canvas, string id) { this.canvas = canvas; this.id = id; }
            Row Current { get { return canvas.rows.FirstOrDefault(r => r.Task.Id == id); } }
            public override string Name { get { var row = Current; return row == null ? "" : row.Task.Text + (row.Task.Done ? "，已完成" : "，待办") + (row.Task.Important ? "，重要" : ""); } set { } }
            public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
            public override AccessibleObject Parent { get { return canvas.AccessibilityObject; } }
            public override Rectangle Bounds { get { var row = Current; return row == null ? Rectangle.Empty : canvas.RectangleToScreen(Rectangle.Intersect(canvas.View(row.Bounds), canvas.ClientRectangle)); } }
            public override string DefaultAction { get { return "打开任务详情"; } }
            public override void DoDefaultAction()
            {
                Action open = delegate { if (Current != null && canvas.PrimaryAction != null) canvas.PrimaryAction(canvas, new TaskPointerEventArgs(id, TaskPart.Body, Point.Empty)); };
                if (canvas.InvokeRequired) canvas.BeginInvoke(open); else open();
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { titleFont.Dispose(); doneFont.Dispose(); metaFont.Dispose(); } base.Dispose(disposing); }
    }
    static class TaskStar
    {
        // Both task surfaces use the original main-list geometry and palette.
        public static void Draw(Graphics g, Rectangle bounds, bool filled, Color accent, Color outline)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = bounds.X + bounds.Width / 2F, cy = bounds.Y + bounds.Height / 2F, radius = Math.Min(bounds.Width, bounds.Height) * .32F;
            var points = new PointF[10];
            for (int i = 0; i < 10; i++) { double angle = -Math.PI / 2 + i * Math.PI / 5; float r = i % 2 == 0 ? radius : radius * .43F; points[i] = new PointF(cx + (float)Math.Cos(angle) * r, cy + (float)Math.Sin(angle) * r); }
            if (filled) using (var brush = new SolidBrush(accent)) g.FillPolygon(brush, points);
            else using (var pen = new Pen(outline, Math.Max(1, (int)Math.Round(g.DpiX / 96F)))) g.DrawPolygon(pen, points);
        }
    }
    class TaskStarButton : FlatButton
    {
        readonly bool filled;
        readonly Palette colors = new Palette(false);
        public TaskStarButton(bool important) : base("", "标记当前任务为重要") { filled = important; }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); TaskStar.Draw(e.Graphics, ClientRectangle, filled, colors.Accent, colors.Muted); }
    }
    class VerticalMoreButton : FlatButton
    {
        public VerticalMoreButton(string name) : base("",name) { }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F, diameter = 2.3F * scale;
            using(var brush = new SolidBrush(ForeColor)) for(int i=-1;i<=1;i++) e.Graphics.FillEllipse(brush,Width/2F-diameter/2,Height/2F+i*6*scale-diameter/2,diameter,diameter);
        }
    }
    class PinButton : FlatButton
    {
        public bool IsPinned;
        public PinButton() : base("", "固定位置和大小") { }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var saved = e.Graphics.Save(); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TranslateTransform(Width / 2F, Height / 2F); if (!IsPinned) e.Graphics.RotateTransform(30);
            float s = Math.Min(Width, Height) / 30F;
            using (var pen = new Pen(ForeColor, Math.Max(1.4F, 1.6F * s)))
            { e.Graphics.DrawLine(pen, -5 * s, -8 * s, 5 * s, -8 * s); e.Graphics.DrawLine(pen, -3 * s, -8 * s, -3 * s, 0); e.Graphics.DrawLine(pen, 3 * s, -8 * s, 3 * s, 0); e.Graphics.DrawLines(pen, new[] { new PointF(-3 * s, 0), new PointF(-6 * s, 4 * s), new PointF(6 * s, 4 * s), new PointF(3 * s, 0) }); e.Graphics.DrawLine(pen, 0, 4 * s, 0, 11 * s); }
            e.Graphics.Restore(saved);
        }
    }
}

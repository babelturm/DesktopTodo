using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("桌面待办 DesktopTodo")]
[assembly: AssemblyDescription("本地保存的 Windows 桌面层待办清单")]
[assembly: AssemblyProduct("DesktopTodo")]
[assembly: AssemblyVersion("2.0.5.0")]
[assembly: AssemblyFileVersion("2.0.5.0")]

namespace DesktopTodo
{
    public class TodoStep
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public bool Done { get; set; }
    }
    public class Todo
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public bool Done { get; set; }
        public bool Important { get; set; }
        public string ListId { get; set; }
        public bool Deleted { get; set; }
        public string CreatedUtc { get; set; }
        public string CompletedUtc { get; set; }
        public List<TodoStep> Steps { get; set; }
        public string Notes { get; set; }
        public string DueDate { get; set; }
        public string ReminderUtc { get; set; }
        public string ReminderDeliveredUtc { get; set; }
        public int? ReminderCountdownMinutes { get; set; }
        public Todo() { Steps = new List<TodoStep>(); Notes = ""; }
    }
    public class AppState
    {
        public int Version { get; set; }
        public List<Todo> Tasks { get; set; }
        public List<TodoList> Lists { get; set; }
        public string SelectedListId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Light { get; set; }
        public int Opacity { get; set; }
        public bool GeometryLocked { get; set; }
        public AppState()
        {
            Version = 2; Tasks = new List<Todo>(); Lists = ListSchema.Defaults(); SelectedListId = ListSchema.DefaultId; X = int.MinValue; Y = int.MinValue;
            Width = 380; Height = 560; Opacity = 100;
        }
    }

    public sealed class Store : IDisposable
    {
        public readonly string DirectoryPath;
        public readonly string FilePath;
        public string Notice;
        readonly FileStream writeLock;
        bool migrationBackupChecked;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        public Store(string directory)
        {
            DirectoryPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(DirectoryPath);
            FilePath = Path.Combine(DirectoryPath, "tasks.json");
            writeLock = new FileStream(Path.Combine(DirectoryPath, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        public static string Encode(AppState state) { return Json.Serialize(state); }
        public static AppState Clone(AppState state) { return Decode(Encode(state)); }
        public static AppState Decode(string text)
        {
            var root = Json.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("Version") || !root.ContainsKey("Tasks"))
                throw new InvalidDataException("清单缺少必要字段。");
            var state = Json.Deserialize<AppState>(text);
            if (state == null || (state.Version != 1 && state.Version != 2) || state.Tasks == null) throw new InvalidDataException("清单格式不正确或版本不兼容。");
            ListSchema.UpgradeAndValidate(state,root.ContainsKey("Lists"));
            var ids = new HashSet<string>();
            foreach (Todo t in state.Tasks)
            {
                if (t == null || string.IsNullOrEmpty(t.Id) || t.Text == null || !ids.Add(t.Id))
                    throw new InvalidDataException("清单包含无效任务。");
                // Old recycle-bin items remain accessible after removing that view.
                if (t.Deleted) { t.Deleted = false; t.Done = true; t.CompletedUtc = t.CompletedUtc ?? t.CreatedUtc; }
                if (t.Steps == null) t.Steps = new List<TodoStep>();
                if (t.Notes == null) t.Notes = "";
                var stepIds = new HashSet<string>();
                if (t.Steps.Any(s => s == null || string.IsNullOrEmpty(s.Id) || s.Text == null || !stepIds.Add(s.Id))) throw new InvalidDataException("步骤格式不正确。");
                DateTimeOffset reminder;
                if (t.ReminderUtc != null && !DateTimeOffset.TryParse(t.ReminderUtc, out reminder)) throw new InvalidDataException("提醒时间格式不正确。");
                if (t.ReminderCountdownMinutes.HasValue && (t.ReminderCountdownMinutes.Value < 1 || t.ReminderCountdownMinutes.Value > 43200 || t.ReminderUtc == null)) throw new InvalidDataException("倒计时格式不正确。");
                DateTime date;
                if (t.DueDate != null && !DateTime.TryParseExact(t.DueDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date)) throw new InvalidDataException("截止日期格式不正确。");
            }
            state.Opacity = 100;
            return state;
        }
        public AppState Load()
        {
            if (!File.Exists(FilePath) && !File.Exists(FilePath + ".bak")) return new AppState();
            Exception original = null;
            if (File.Exists(FilePath))
                try { return Decode(File.ReadAllText(FilePath, Encoding.UTF8)); }
                catch (Exception e) { original = e; }
            if (File.Exists(FilePath + ".bak"))
            {
                try
                {
                    var recovered = Decode(File.ReadAllText(FilePath + ".bak", Encoding.UTF8));
                    if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), false);
                    // Remove an invalid primary only after preserving it. Do not rotate it into the valid backup.
                    File.Copy(FilePath + ".bak", FilePath, true);
                    Notice = "主文件异常，已从上一次保存的备份恢复。原文件已保留。";
                    return recovered;
                }
                catch (Exception e) { throw new InvalidDataException("清单和备份无法恢复。文件已保留，请先备份数据目录。", e); }
            }
            throw new InvalidDataException("无法读取清单，原文件已保留。", original);
        }
        public void Save(AppState state)
        {
            if(!migrationBackupChecked)
            {
                if(File.Exists(FilePath))
                {
                    string original=File.ReadAllText(FilePath,Encoding.UTF8);
                    var root=Json.DeserializeObject(original) as Dictionary<string,object>;
                    if(root!=null && root.ContainsKey("Version") && Convert.ToInt32(root["Version"])==1)
                    { Decode(original); if(!File.Exists(FilePath+".pre-v2.bak")) File.Copy(FilePath,FilePath+".pre-v2.bak",false); }
                    var rawState=Json.Deserialize<AppState>(original);
                    if(rawState!=null && rawState.Tasks!=null && rawState.Tasks.Any(t => t!=null && t.Deleted))
                    { Decode(original); if(!File.Exists(FilePath+".pre-v2.0.3.bak")) File.Copy(FilePath,FilePath+".pre-v2.0.3.bak",false); }
                }
                migrationBackupChecked=true;
            }
            byte[] bytes = new UTF8Encoding(false).GetBytes(Encode(state));
            string temp = FilePath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak", true);
            else File.Move(temp, FilePath);
        }
        public void Dispose() { writeLock.Dispose(); }
    }

    public sealed partial class TaskService
    {
        readonly Store store;
        readonly Stack<AppState> undo = new Stack<AppState>();
        public AppState State { get; private set; }
        public bool CanUndo { get { return undo.Count > 0; } }
        public TaskService(Store store) { this.store = store; State = store.Load(); }
        public void Change(Action<AppState> change)
        {
            var next = Store.Clone(State);
            change(next);
            store.Save(next); // Commit before exposing any changes to the UI.
            undo.Push(State); State = next;
        }
        public void Undo()
        {
            if (!CanUndo) return;
            var next = Store.Clone(State);
            var restored=Store.Clone(undo.Peek()); next.Tasks=restored.Tasks; next.Lists=restored.Lists;
            foreach(var category in next.Lists) { var current=State.Lists.FirstOrDefault(l => l.Id==category.Id); if(current!=null) category.CompletedExpanded=current.CompletedExpanded; }
            if(!next.Lists.Any(l => l.Id==next.SelectedListId)) next.SelectedListId=restored.SelectedListId;
            foreach (var task in next.Tasks)
            {
                var current = State.Tasks.FirstOrDefault(t => t.Id == task.Id);
                if (current != null && task.ReminderUtc == current.ReminderUtc && current.ReminderDeliveredUtc != null) task.ReminderDeliveredUtc = current.ReminderDeliveredUtc;
            }
            store.Save(next); undo.Pop(); State = next;
        }
        public void Settings(Action<AppState> update)
        {
            var next = Store.Clone(State); update(next); store.Save(next); State = next;
        }
        public void Add(string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 2000) throw new ArgumentException("每条任务最多 2000 个字符。");
            Change(s => s.Tasks.Add(new Todo { Id = Guid.NewGuid().ToString("N"), Text = text, ListId = s.SelectedListId, CreatedUtc = DateTime.UtcNow.ToString("o") }));
        }
        public void Toggle(string id)
        {
            Change(s => { var t = s.Tasks.Single(x => x.Id == id); t.Done = !t.Done; t.CompletedUtc = t.Done ? DateTime.UtcNow.ToString("o") : null; });
        }
        public void Delete(string id)
        {
            var next = Store.Clone(State);
            var task = next.Tasks.Single(t => t.Id == id); next.Tasks.Remove(task);
            store.Save(next);
            // A confirmed permanent deletion must not reappear through an older undo snapshot.
            foreach (var snapshot in undo) snapshot.Tasks.RemoveAll(t => t.Id == id);
            State = next;
        }
        public void AddStep(string id, string text)
        {
            text = text.Trim(); if (text.Length == 0) return;
            if (text.Length > 2000) throw new ArgumentException("步骤内容最多 2000 个字符。");
            Change(s => s.Tasks.Single(t => t.Id == id).Steps.Add(new TodoStep { Id = Guid.NewGuid().ToString("N"), Text = text }));
        }
        public void ToggleStep(string id, string stepId) { Change(s => { var step = s.Tasks.Single(t => t.Id == id).Steps.Single(x => x.Id == stepId); step.Done = !step.Done; }); }
        public void RemoveStep(string id, string stepId) { Change(s => s.Tasks.Single(t => t.Id == id).Steps.RemoveAll(x => x.Id == stepId)); }
        public void SaveText(string id, string text, string notes)
        { SaveDetailText(id, text, notes, null); }
        public void SaveDetailText(string id, string text, string notes, IDictionary<string,string> stepTexts, string removeStepId = null)
        {
            text = text.Trim(); if (text.Length == 0 || text.Length > 2000) throw new ArgumentException("任务标题不能为空，且最多 2000 个字符。");
            if (notes.Length > 20000) throw new ArgumentException("备注最多 20000 个字符。");
            var task = State.Tasks.Single(t => t.Id == id);
            if (removeStepId != null && !task.Steps.Any(s => s.Id == removeStepId)) throw new ArgumentException("该步骤已不存在。");
            var edits = new Dictionary<string,string>();
            if (stepTexts != null) foreach (var pair in stepTexts)
            {
                if (pair.Key == removeStepId) continue;
                if (!task.Steps.Any(s => s.Id == pair.Key)) throw new ArgumentException("该步骤已不存在。");
                string value = (pair.Value ?? "").Trim();
                if (value.Length == 0 || value.Length > 2000) throw new ArgumentException("步骤内容不能为空，且最多 2000 个字符。");
                edits.Add(pair.Key,value);
            }
            if (task.Text == text && task.Notes == notes && removeStepId == null && edits.All(p => task.Steps.Single(s => s.Id == p.Key).Text == p.Value)) return;
            Change(s =>
            {
                var t = s.Tasks.Single(x => x.Id == id); t.Text = text; t.Notes = notes;
                foreach (var pair in edits) t.Steps.Single(step => step.Id == pair.Key).Text = pair.Value;
                if (removeStepId != null) t.Steps.RemoveAll(step => step.Id == removeStepId);
            });
        }
        public void SetDueDate(string id, DateTime? date) { Change(s => s.Tasks.Single(t => t.Id == id).DueDate = date.HasValue ? date.Value.ToString("yyyy-MM-dd") : null); }
        public void SetReminder(string id, DateTimeOffset? when) { Change(s => { var t = s.Tasks.Single(x => x.Id == id); t.ReminderUtc = when.HasValue ? when.Value.ToUniversalTime().ToString("o") : null; t.ReminderDeliveredUtc = null; t.ReminderCountdownMinutes = null; }); }
        public static DateTimeOffset CountdownTarget(DateTimeOffset now, int amount, bool hours)
        {
            long minutes = (long)amount * (hours ? 60 : 1);
            if (minutes < 1 || minutes > 43200) throw new ArgumentOutOfRangeException("amount", "倒计时需要在 1 分钟至 30 天之间。");
            return now.ToUniversalTime().AddMinutes(minutes);
        }
        public void SetCountdown(string id, int amount, bool hours, DateTimeOffset now)
        {
            DateTimeOffset target = CountdownTarget(now, amount, hours);
            Change(s => { var t = s.Tasks.Single(x => x.Id == id); t.ReminderUtc = target.ToString("o"); t.ReminderDeliveredUtc = null; t.ReminderCountdownMinutes = amount * (hours ? 60 : 1); });
        }
        public List<Todo> DueReminders(DateTimeOffset now)
        {
            return State.Tasks.Where(t => !t.Done && !t.Deleted && t.ReminderUtc != null && t.ReminderDeliveredUtc == null && DateTimeOffset.Parse(t.ReminderUtc) <= now).ToList();
        }
        public void MarkReminderDelivered(string id, string scheduled, DateTimeOffset now)
        {
            Settings(s => { var t = s.Tasks.Single(x => x.Id == id); if (t.ReminderUtc == scheduled) t.ReminderDeliveredUtc = now.ToUniversalTime().ToString("o"); });
        }
    }

    static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string title);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int Set32(IntPtr h, int n, int v);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr Set64(IntPtr h, int n, IntPtr v);
        public static void SetOwner(IntPtr h, IntPtr owner) { if (IntPtr.Size == 8) Set64(h, -8, owner); else Set32(h, -8, owner.ToInt32()); }
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetStyle(IntPtr h, int n);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr handle, IntPtr rect, IntPtr region, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] public static extern IntPtr SendTextMessage(IntPtr h, int msg, IntPtr wp, string lp);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] public struct WindowPos { public IntPtr Hwnd, After; public int X, Y, CX, CY; public uint Flags; }
    }

    class Palette
    {
        public Color Bg, Bottom, Row, Text, Muted, Line, Accent, Input, Soft;
        public Palette(bool light)
        {
            Bg = Color.FromArgb(91, 112, 194); Bottom = Color.FromArgb(130, 148, 223);
            Row = Color.FromArgb(248, 249, 253); Text = Color.FromArgb(37, 42, 55);
            Muted = Color.FromArgb(108, 117, 139); Line = Color.FromArgb(210, 220, 246);
            Accent = Color.FromArgb(86, 111, 195); Input = Color.FromArgb(232, 237, 252);
            Soft = Color.FromArgb(219, 227, 251);
        }
    }

    static class Shapes
    {
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            var p = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (d < 1) { p.AddRectangle(rect); return p; }
            p.AddArc(rect.Left, rect.Top, d, d, 180, 90); p.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            p.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); p.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
    }
    // WinForms transparency asks ancestors to repaint into a child DC. Never use
    // that path for this desktop widget: paint the complete surface explicitly.
    static class WidgetPaint
    {
        public static void Background(Control control, Graphics g)
        {
            Form form = control.FindForm();
            if (form == null) { g.Clear(SystemColors.Control); return; }
            Point origin = form.PointToClient(control.PointToScreen(Point.Empty));
            var saved = g.Save();
            try
            {
                g.TranslateTransform(-origin.X, -origin.Y);
                var widget = form as TodoForm;
                if (widget != null) widget.PaintBaseSurface(g);
                else using (var brush = new SolidBrush(form.BackColor)) g.FillRectangle(brush, form.ClientRectangle);
                var ancestors = new List<Control>();
                for (Control parent = control.Parent; parent != null && parent != form; parent = parent.Parent) ancestors.Add(parent);
                ancestors.Reverse();
                foreach (Control parent in ancestors)
                {
                    var panel = parent as SmoothPanel;
                    if (panel != null && panel.FillColor.A > 0)
                    {
                        Point location = form.PointToClient(panel.PointToScreen(Point.Empty));
                        FillPanel(g, panel, location.X, location.Y);
                    }
                }
            }
            finally { g.Restore(saved); }
        }
        public static void FillPanel(Graphics g, SmoothPanel panel, int x, int y)
        {
            if (panel.Width < 1 || panel.Height < 1 || panel.FillColor.A == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shapes.Round(new RectangleF(x, y, panel.Width - 1, panel.Height - 1), panel.Radius * g.DpiX / 96F))
            using (var brush = new SolidBrush(panel.FillColor)) g.FillPath(brush, path);
        }
        public static Bitmap Render(Control control, Color initial)
        {
            var bitmap = new Bitmap(Math.Max(1, control.Width), Math.Max(1, control.Height));
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                using (Graphics screen = control.CreateGraphics()) bitmap.SetResolution(screen.DpiX, screen.DpiY);
                graphics.Clear(initial);
                Background(control, graphics);
                var flat = control as FlatButton;
                if (flat != null) flat.RenderSurface(graphics);
                var toggle = control as ToggleSwitch;
                if (toggle != null) toggle.RenderSurface(graphics);
            }
            return bitmap;
        }
    }
    class SurfaceLabel : Label
    {
        public SurfaceLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            WidgetPaint.Background(this, e.Graphics);
            if (BackColor.A == 255) using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }
    class SmoothPanel : Panel
    {
        public Color FillColor { get; set; }
        public int Radius = 7;
        public SmoothPanel() { FillColor = Color.Transparent; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaintBackground(PaintEventArgs e) { WidgetPaint.Background(this, e.Graphics); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (FillColor.A > 0 && Width > 0 && Height > 0)
            { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var path = Shapes.Round(new RectangleF(0, 0, Width - 1, Height - 1), Radius * e.Graphics.DpiX / 96F)) using (var brush = new SolidBrush(FillColor)) e.Graphics.FillPath(brush, path); }
        }
    }
    // The scroll rail is outside card bounds, with a small gap between card and thumb.
    class TaskListPanel : SmoothPanel
    {
        int offset, contentHeight;
        bool layingOut, draggingScroll;
        int updateDepth;
        bool redrawSuspended;
        public Action<Control, int> SizeItem;
        public bool HasOverflow { get { return contentHeight > ClientSize.Height; } }
        public int RailLeft { get { return overlay.Left; } }
        int MaxOffset { get { return Math.Max(0, contentHeight - ClientSize.Height); } }
        readonly ScrollOverlay overlay;
        public TaskListPanel()
        {
            AutoScroll = false; overlay = new ScrollOverlay(this); Controls.Add(overlay);
            Resize += delegate { LayoutItems(); }; ControlAdded += delegate { LayoutItems(); }; ControlRemoved += delegate { LayoutItems(); };
        }
        public IEnumerable<Control> Items { get { return Controls.Cast<Control>().Where(c => c != overlay); } }
        public void BeginUpdate()
        {
            if (updateDepth++ != 0) return; SuspendLayout(); redrawSuspended = IsHandleCreated && Visible;
            if (redrawSuspended) Native.SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
        }
        public void EndUpdate()
        {
            if (updateDepth == 0 || --updateDepth != 0) return; ResumeLayout(false);
            if (redrawSuspended) { Native.SendMessage(Handle, 0x000B, new IntPtr(1), IntPtr.Zero); redrawSuspended = false; Native.RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0085); }
            Invalidate(true);
        }
        public void ClearItems() { foreach (var c in Items.ToArray()) { Controls.Remove(c); c.Dispose(); } offset = 0; }
        public void AddItem(Control c) { Controls.Add(c); overlay.BringToFront(); }
        public void LayoutItems()
        {
            if (layingOut || IsDisposed || updateDepth > 0) return; layingOut = true; BeginUpdate();
            try
            {
                int gap = Math.Max(3, (int)(4 * CreateScale()));
                int total = 0, width = ClientSize.Width;
                foreach (Control c in Items) { if (SizeItem != null) SizeItem(c, ClientSize.Width); total += c.Height + gap; }
                if (total - gap > ClientSize.Height)
                {
                    width = Math.Max(1, ClientSize.Width - (int)Math.Round(14 * CreateScale())); total = 0;
                    foreach (Control c in Items) { if (SizeItem != null) SizeItem(c, width); total += c.Height + gap; }
                }
                contentHeight = Math.Max(0, total - gap); offset = Math.Min(offset, MaxOffset);
                int y = -offset;
                foreach (Control c in Items) { c.Location = new Point(0, y); y += c.Height + gap; }
                int railWidth = Math.Max(8, (int)(8 * CreateScale()));
                overlay.SetBounds(Math.Max(0, Width - railWidth), 2, railWidth, Math.Max(0, Height - 4)); overlay.Visible = HasOverflow; overlay.BringToFront(); overlay.Invalidate(); Invalidate();
            }
            finally { EndUpdate(); layingOut = false; }
        }
        float CreateScale() { using (var g = CreateGraphics()) return g.DpiX / 96F; }
        void PositionItems()
        {
            BeginUpdate();
            try { int y = -offset, gap = Math.Max(3, (int)(4 * CreateScale())); foreach (var c in Items) { c.Location = new Point(0, y); y += c.Height + gap; } overlay.Invalidate(); }
            finally { EndUpdate(); }
        }
        public void ScrollBy(int amount) { offset = (int)Math.Max(0L, Math.Min((long)MaxOffset, (long)offset + amount)); PositionItems(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        { ScrollBy(-(e.Delta / 120) * (int)(56 * CreateScale())); var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true; }
        class ScrollOverlay : Control
        {
            readonly TaskListPanel owner;
            public ScrollOverlay(TaskListPanel owner) { this.owner = owner; SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); BackColor = Color.Transparent; Cursor = Cursors.Hand; TabStop = false; }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                WidgetPaint.Background(this, e.Graphics);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                if (!owner.HasOverflow || Height < 1) return;
                float thumb = Math.Max(24, Height * (float)owner.Height / owner.contentHeight);
                float y = owner.MaxOffset == 0 ? 0 : (Height - thumb) * owner.offset / owner.MaxOffset;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float tw = Math.Max(4, Width * .45F);
                using (var p = Shapes.Round(new RectangleF((Width - tw) / 2, y, tw, thumb), 2)) using (var b = new SolidBrush(Color.FromArgb(185, 88, 106, 164))) e.Graphics.FillPath(b, p);
            }
            void Jump(int y) { owner.offset = Math.Max(0, Math.Min(owner.MaxOffset, (int)((float)y / Math.Max(1, Height) * owner.contentHeight - owner.Height / 2))); owner.PositionItems(); }
            protected override void OnMouseDown(MouseEventArgs e) { owner.draggingScroll = true; Capture = true; Jump(e.Y); }
            protected override void OnMouseMove(MouseEventArgs e) { if (owner.draggingScroll) Jump(e.Y); }
            protected override void OnMouseUp(MouseEventArgs e) { owner.draggingScroll = false; Capture = false; }
            protected override void OnMouseWheel(MouseEventArgs e) { owner.OnMouseWheel(e); }
        }
    }

    class FlatButton : Control
    {
        bool hover, pressed;
        MouseButtons lastMouseButton;
        public bool AlignLeft;
        public FlatButton(string text, string name)
        {
            Text = text; AccessibleName = name; AccessibleRole = AccessibleRole.PushButton; Cursor = Cursors.Hand; TabStop = true;
            Font = new Font("Microsoft YaHei UI", 9F);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        }
        public void PerformClick() { lastMouseButton = MouseButtons.None; if (Enabled) OnClick(EventArgs.Empty); }
        protected override void OnClick(EventArgs e) { if (lastMouseButton == MouseButtons.Right || lastMouseButton == MouseButtons.Middle) return; base.OnClick(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { WidgetPaint.Background(this, e.Graphics); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { lastMouseButton = e.Button; if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); lastMouseButton = MouseButtons.None; }
        protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PerformClick(); } base.OnKeyDown(e); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ClickAccessible(this); }
        class ClickAccessible : ControlAccessibleObject
        {
            readonly FlatButton button;
            public ClickAccessible(FlatButton button) : base(button) { this.button = button; }
            public override string DefaultAction { get { return "按下"; } }
            public override void DoDefaultAction() { button.PerformClick(); }
        }
        public void SetHoverForTest(bool enabled) { if (enabled) OnMouseEnter(EventArgs.Empty); else OnMouseLeave(EventArgs.Empty); }
        public void RenderSurface(Graphics graphics) { OnPaint(new PaintEventArgs(graphics, ClientRectangle)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (BackColor.A == 255)
                using (var p = Shapes.Round(new RectangleF(0, 0, Width - 1, Height - 1), 5)) using (var b = new SolidBrush(BackColor)) e.Graphics.FillPath(b, p);
            if (Enabled && (hover || pressed)) using (var p = Shapes.Round(new RectangleF(0, 0, Width - 1, Height - 1), 5)) using (var b = new SolidBrush(Color.FromArgb(pressed ? 40 : 20, 90, 113, 198))) e.Graphics.FillPath(b, p);
            Rectangle textRect = AlignLeft ? new Rectangle(12, 0, Math.Max(1, Width - 24), Height) : ClientRectangle;
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect, Enabled ? ForeColor : Color.FromArgb(180, 191, 225), (AlignLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) using (var pen = new Pen(Color.FromArgb(120, ForeColor))) { pen.DashStyle = DashStyle.Dot; e.Graphics.DrawRectangle(pen, 3, 3, Width - 7, Height - 7); }
        }
    }
    class HamburgerButton : FlatButton
    {
        public HamburgerButton() : base("", "设置菜单") { }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float left = Width * .22F, right = Width * .78F;
            using (var pen = new Pen(ForeColor, Math.Max(1.5F, Width / 25F)))
            { for (int i = 0; i < 3; i++) { float y = Height * (.29F + i * .21F); e.Graphics.DrawLine(pen, left, y, right, y); } }
        }
    }

    class CheckTaskButton : FlatButton
    {
        readonly bool done, restore;
        public CheckTaskButton(bool done, bool restore, string name) : base("", name) { this.done = done; this.restore = restore; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (restore) { Text = "↶"; base.OnPaint(e); return; }
            float d = Math.Min(Width, Height) * 0.56F; var r = new RectangleF((Width - d) / 2, (Height - d) / 2, d, d);
            using (var pen = new Pen(done ? ForeColor : Color.FromArgb(116, 121, 133), Math.Max(1.5F, Width / 22F)))
            {
                if (done) { using (var b = new SolidBrush(ForeColor)) e.Graphics.FillEllipse(b, r); pen.Color = Color.White; pen.Width = Math.Max(1.7F, d / 11F); e.Graphics.DrawLines(pen, new[] { new PointF(r.X + d * .25F, r.Y + d * .51F), new PointF(r.X + d * .44F, r.Y + d * .7F), new PointF(r.X + d * .77F, r.Y + d * .33F) }); }
                else e.Graphics.DrawEllipse(pen, r);
            }
        }
    }

    interface IStartupStore { string Read(); void Write(string value); }
    class RegistryStartupStore : IStartupStore
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public string Read() { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? null : k.GetValue("DesktopTodoLocal") as string; }
        public void Write(string value) { using (var k = Registry.CurrentUser.CreateSubKey(Key)) { if (value == null) k.DeleteValue("DesktopTodoLocal", false); else k.SetValue("DesktopTodoLocal", value); } }
    }
    class MemoryStartupStore : IStartupStore { public string Value; public string Read() { return Value; } public void Write(string value) { Value = value; } }
    class StartupService
    {
        readonly IStartupStore store; readonly string executable;
        public StartupService(IStartupStore store, string executable) { this.store = store; this.executable = executable; }
        public bool Enabled { get { return !string.IsNullOrWhiteSpace(store.Read()); } }
        public void SetEnabled(bool enabled) { store.Write(enabled ? "\"" + executable + "\"" : null); }
        public void UpdatePathIfEnabled() { string current=store.Read(),expected="\""+executable+"\""; if(!string.IsNullOrWhiteSpace(current) && current!=expected) store.Write(expected); }
    }
    class ToggleSwitch : Control
    {
        bool isChecked;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return isChecked; } set { if (isChecked == value) return; isChecked = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        public ToggleSwitch() { Size = new Size(48, 26); Text = ""; AccessibleName = "开机启动开关"; AccessibleRole = AccessibleRole.CheckButton; Cursor = Cursors.Hand; TabStop = true; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; }
        protected override void OnPaintBackground(PaintEventArgs e) { if (Parent is DialogCard) e.Graphics.Clear(Parent.BackColor); else WidgetPaint.Background(this, e.Graphics); }
        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); } base.OnKeyDown(e); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new SwitchAccessible(this); }
        class SwitchAccessible : ControlAccessibleObject
        {
            readonly ToggleSwitch toggle;
            public SwitchAccessible(ToggleSwitch toggle) : base(toggle) { this.toggle = toggle; }
            public override AccessibleStates State { get { return base.State | (toggle.Checked ? AccessibleStates.Checked : 0); } }
            public override string DefaultAction { get { return "切换"; } }
            public override void DoDefaultAction() { toggle.OnClick(EventArgs.Empty); }
        }
        public void RenderSurface(Graphics graphics) { OnPaint(new PaintEventArgs(graphics, ClientRectangle)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F, pad = 4 * scale;
            using (var p = Shapes.Round(new RectangleF(1, 1, Width - 2, Height - 2), Height / 2F))
            { using (var b = new SolidBrush(Checked ? Color.FromArgb(0, 95, 184) : Color.White)) e.Graphics.FillPath(b, p); if (!Checked) using (var pen = new Pen(Color.FromArgb(112,112,112), scale)) e.Graphics.DrawPath(pen, p); }
            float d = Height - 2 * pad; using (var b = new SolidBrush(Checked ? Color.White : Color.FromArgb(105,105,105))) e.Graphics.FillEllipse(b, Checked ? Width - d - pad : pad, pad, d, d);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1));
        }
    }
    class TaskInput : TextBox
    {
        public string Cue = "添加任务";
        public bool IsComposing { get; private set; }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x10D) IsComposing = true;
            if (m.Msg == 0x10E) IsComposing = false;
            base.WndProc(ref m);
        }
        protected override void OnHandleCreated(EventArgs e)
        { base.OnHandleCreated(e); Native.SendTextMessage(Handle, 0x1501, new IntPtr(1), Cue); }
    }

    sealed partial class TodoForm : Form
    {
        readonly Store store;
        readonly TaskService service;
        readonly string diagnosticPath;
        readonly SmoothPanel header = new SmoothPanel(), inputArea = new SmoothPanel(), footer = new SmoothPanel();
        readonly TaskCanvas list = new TaskCanvas();
        readonly ListTitleButton title = new ListTitleButton();
        readonly Label subtitle = new SurfaceLabel(), status = new SurfaceLabel();
        readonly TaskInput input = new TaskInput();
        readonly FlatButton add = new FlatButton("＋", "添加任务"), menu = new HamburgerButton(), hide = new FlatButton("—", "隐藏到托盘"), undoButton = new FlatButton("撤销", "撤销上一次操作");
        readonly PinButton geometryPin = new PinButton();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly System.Windows.Forms.Timer pinTimer = new System.Windows.Forms.Timer(), locationTimer = new System.Windows.Forms.Timer();
        readonly ToolTip tips = new ToolTip();
        readonly List<Font> rowFonts = new List<Font>();
        Palette colors;
        string renderedListId;
        int utilityDialogDepth;
        bool closing, layoutReady, savingBounds;
        bool desktopMode = true;
        readonly bool inspectUi;
        readonly string uiTestDirectory;
        readonly StartupService startup;
        string message;

        public TodoForm(Store store, string diagnostics, bool inspectUi, string uiTestDirectory)
        {
            this.store = store; service = new TaskService(store); diagnosticPath = diagnostics;
            startup = new StartupService(diagnostics == null ? (IStartupStore)new RegistryStartupStore() : new MemoryStartupStore(), Application.ExecutablePath);
            this.uiTestDirectory = uiTestDirectory;
            this.inspectUi = inspectUi && diagnostics != null; desktopMode = !this.inspectUi && uiTestDirectory == null;
            Text = "桌面待办"; Name = "DesktopTodo"; AccessibleName = "桌面待办";
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Microsoft YaHei UI", 9F); FormBorderStyle = FormBorderStyle.None;
            DoubleBuffered = true; ShowInTaskbar = this.inspectUi; StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            MinimumSize = new Size(320, 390); MaximumSize = new Size(760, 1400);
            Icon = MakeIcon();
            BuildUi(); ApplyTheme();
            BuildDetails();
            UpdateGeometryPin();
            pinTimer.Interval = 3000; pinTimer.Tick += delegate { Pin(); WriteDiagnostics(); }; pinTimer.Start();
            locationTimer.Interval = 500; locationTimer.Tick += delegate { locationTimer.Stop(); SaveBounds(); };
            tray.Icon = Icon; tray.Text = "桌面待办 · 双击显示"; tray.Visible = uiTestDirectory == null;
            tray.DoubleClick += delegate { ShowOnDesktop(); };
            tray.ContextMenuStrip = TrayMenu();
            ConfigureReminders();
            Resize += delegate { if (layoutReady) { LayoutUi(); QueueBounds(); Invalidate(true); } };
            LocationChanged += delegate { if (layoutReady) QueueBounds(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!SaveDetailDraft()) { e.Cancel = true; closing = false; return; } if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } else SaveBounds(); };
            FormClosed += delegate { if (notifications != null) notifications.Dispose(); tray.Visible = false; tray.Dispose(); pinTimer.Dispose(); locationTimer.Dispose(); detailSaveTimer.Dispose(); reminderTimer.Dispose(); tips.Dispose(); foreach (Font f in rowFonts) f.Dispose(); };
        }

        protected override CreateParams CreateParams
        { get { var cp = base.CreateParams; if (!inspectUi) cp.ExStyle |= 0x80; return cp; } }
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var s = service.State;
            float scale = DeviceDpiScale();
            Size = new Size((int)(Math.Max(320, Math.Min(760, s.Width)) * scale), (int)(Math.Max(390, Math.Min(1400, s.Height)) * scale));
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            if (s.X != int.MinValue && Screen.AllScreens.Any(x => x.WorkingArea.IntersectsWith(new Rectangle(s.X, s.Y, Width, Height))))
                Location = new Point(s.X, s.Y);
            else Location = new Point(area.Right - Width - 28, area.Top + 72);
            ClampToScreen(); layoutReady = true; LayoutUi(); RenderTasks(); Pin(); WriteDiagnostics();
            if(diagnosticPath==null && uiTestDirectory==null) try { startup.UpdatePathIfEnabled(); } catch(Exception ex) { message="开机启动路径未更新："+ex.Message; RenderTasks(); }
            if (diagnosticPath == null && uiTestDirectory == null) InitializeNotifications();
            if (store.Notice != null) MessageBox.Show(store.Notice, "清单恢复", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (uiTestDirectory != null)
            {
                var testTimer = new System.Windows.Forms.Timer { Interval = 800 };
                testTimer.Tick += delegate { testTimer.Stop(); testTimer.Dispose(); RunUiTests(); }; testTimer.Start();
            }
        }
        float DeviceDpiScale() { using (Graphics g = CreateGraphics()) return g.DpiX / 96F; }
        int D(int n) { return (int)Math.Round(n * DeviceDpiScale()); }

        void BuildUi()
        {
            Controls.Add(list); Controls.Add(footer); Controls.Add(inputArea); Controls.Add(header);
            header.Controls.Add(title); header.Controls.Add(subtitle); header.Controls.Add(menu); header.Controls.Add(hide); header.Controls.Add(geometryPin);
            title.Text = "待办"; title.AutoSize = false; title.Font = new Font("Microsoft YaHei UI", 23F, FontStyle.Bold);
            subtitle.AutoSize = false; subtitle.Font = new Font("Microsoft YaHei UI", 9F);
            title.DragRequested += delegate { if(!service.State.GeometryLocked) { Native.ReleaseCapture(); Native.SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero); } };
            title.Click += delegate { BuildListMenu().Show(title,new Point(0,title.Height+D(4))); };
            subtitle.MouseDown += DragHeader; header.MouseDown += DragHeader;
            menu.Click += delegate { ShowSettings(menu, new Point(0, menu.Height)); };
            hide.Click += delegate { Hide(); };
            geometryPin.Click += delegate { ToggleGeometryLock(); };
            tips.SetToolTip(hide, "隐藏到托盘；双击托盘图标重新显示");
            tips.SetToolTip(title, "点击标题切换列表；拖动标题移动清单");
            inputArea.Controls.Add(input); inputArea.Controls.Add(add);
            input.BorderStyle = BorderStyle.None; input.MaxLength = 2000; input.AccessibleName = "新任务";
            input.Font = new Font("Microsoft YaHei UI", 10F); input.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && !input.IsComposing) { e.SuppressKeyPress = true; AddTask(); } };
            tips.SetToolTip(input, "写下任务，按 Enter 添加");
            add.Click += delegate { AddTask(); };
            add.Font = new Font("Segoe UI", 19F);
            list.CompletedToggleRequested += delegate
            { Run(delegate { string id = service.State.SelectedListId; service.Settings(s => { var category = s.Lists.Single(l => l.Id == id); category.CompletedExpanded = !category.CompletedExpanded; }); }, null); };
            list.AccessibleName = "任务清单";
            list.PrimaryAction += delegate(object sender, TaskPointerEventArgs e)
            {
                if (e.Part == TaskPart.Check) Run(delegate { service.Toggle(e.Id); }, "已保存");
                else if (e.Part == TaskPart.Star) Run(delegate { service.Change(s => { var t = s.Tasks.Single(x => x.Id == e.Id); t.Important = !t.Important; }); }, "已保存");
                else OpenDetail(e.Id);
            };
            list.ContextAction += delegate(object sender, TaskPointerEventArgs e) { TaskMenu(e.Id).Show(list, e.Location); };
            footer.Controls.Add(status); status.Font = new Font("Microsoft YaHei UI", 8F); status.AutoEllipsis = true;
            undoButton.Click += delegate { Run(delegate { service.Undo(); }, "已撤销"); };
        }

        void LayoutUi()
        {
            if (ClientSize.Width < 1 || ClientSize.Height < 1) return;
            int pad = D(16), w = ClientSize.Width, h = ClientSize.Height;
            SuspendLayout();
            header.SetBounds(0, 0, w, D(118)); title.SetBounds(pad + D(3), D(45), w - D(60), D(46));
            subtitle.SetBounds(pad + D(5), D(94), w - 2 * pad, D(22));
            menu.SetBounds(pad, D(10), D(32), D(30)); hide.SetBounds(w - D(46), D(10), D(28), D(30)); geometryPin.SetBounds(w - D(84), D(10), D(30), D(30));
            list.SetBounds(pad, D(128), w - 2 * pad, Math.Max(D(80), h - D(218)));
            int cardWidth = list.CardWidth;
            inputArea.SetBounds(pad, h - D(78), cardWidth, D(48));
            add.SetBounds(D(5), (inputArea.Height - D(37)) / 2, D(37), D(37)); input.SetBounds(D(48), (inputArea.Height - input.PreferredHeight) / 2, inputArea.Width - D(62), input.PreferredHeight);
            footer.SetBounds(pad, h - D(27), w - 2 * pad, D(24));
            undoButton.SetBounds(footer.Width - D(62), 0, D(51), D(22));
            status.SetBounds(D(2), D(4), footer.Width - D(4), D(18));
            using (var rounded = Shapes.Round(new RectangleF(0, 0, w, h), D(11))) { Region old = Region; Region = new Region(rounded); if (old != null) old.Dispose(); }
            ResizeRows(); LayoutDetails(); ResumeLayout(false);
        }
        void ApplyTheme()
        {
            colors = new Palette(service.State.Light); BackColor = colors.Bg; ForeColor = colors.Text;
            title.BackColor = subtitle.BackColor = status.BackColor = Color.Transparent;
            title.ForeColor = Color.White; subtitle.ForeColor = status.ForeColor = Color.FromArgb(229, 235, 255);
            subtitle.Text = DateTime.Now.ToString("M 月 d 日  dddd", new System.Globalization.CultureInfo("zh-CN"));
            inputArea.FillColor = colors.Input; inputArea.Invalidate(); input.BackColor = colors.Input; input.ForeColor = colors.Text;
            foreach (FlatButton b in new[] { menu, hide, undoButton }) { b.BackColor = Color.Transparent; b.ForeColor = Color.White; }
            geometryPin.BackColor = Color.Transparent; geometryPin.ForeColor = Color.White;
            add.BackColor = colors.Input; add.ForeColor = colors.Accent;
            Opacity = 1;
            Invalidate(true);
        }
        void AddTask()
        {
            string text = input.Text.Trim(); if (text.Length == 0) { input.Focus(); return; }
            if (Run(delegate { service.Add(text); }, "已保存")) { input.Clear(); list.ScrollToTask(service.State.Tasks.Last().Id); input.Focus(); }
        }
        bool Run(Action action, string result)
        {
            try { action(); message = result; RenderTasks(); return true; }
            catch (Exception e) { message = "保存失败，请检查数据目录"; RenderTasks(); MessageBox.Show("操作未保存，原清单保持不变。\n\n" + e.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        }
        void RenderTasks()
        {
            var current=service.State.Lists.Single(l => l.Id==service.State.SelectedListId);
            title.Text=current.Name; title.AllowDrag=!service.State.GeometryLocked;
            title.AccessibleName="列表："+current.Name+"，点击切换";
            tips.SetToolTip(title,current.Name+"\n点击切换列表；拖动标题移动清单");
            var all = service.State.Tasks.Where(t => t.ListId==current.Id).ToList();
            int pending = all.Count(t => !t.Done);
            var tasks = all.Where(t => !t.Done).OrderByDescending(t => t.Important).ThenBy(t => t.CreatedUtc)
                .Concat(all.Where(t => t.Done).OrderByDescending(t => t.CompletedUtc ?? t.CreatedUtc));
            list.SetTasks(tasks, TaskMetadata, "今天想完成什么？\n\n在下方添加第一件事。", renderedListId != current.Id, current.CompletedExpanded); renderedListId=current.Id;
            undoButton.Enabled = service.CanUndo; status.Text = message ?? (pending == 0 ? "" : pending + " 件待办");
        }
        void ResizeRows() { list.LayoutItems(); }
        ContextMenuStrip TaskMenu(string id)
        {
            Todo task = service.State.Tasks.Single(t => t.Id == id);
            var m = NewMenu();
            m.AddCommand("任务详情", MenuGlyph.Detail, delegate { OpenDetail(id); });
            var important = m.AddCommand(task.Important ? "取消重要标记" : "标记为重要", MenuGlyph.Star, delegate { Run(delegate { service.Change(s => s.Tasks.Single(t => t.Id == id).Important = !task.Important); }, "已保存"); }); important.ActiveGlyph = task.Important;
            m.AddCommand(task.Done ? "恢复为待办" : "标记为已完成", task.Done ? MenuGlyph.Restore : MenuGlyph.Complete, delegate { Run(delegate { service.Toggle(id); }, "已保存"); });
            m.AddCommand("移动到列表",MenuGlyph.List,delegate { MoveTaskToList(id); });
            m.AddDivider();
            m.DeleteCommand = m.AddCommand("删除任务", MenuGlyph.Delete, delegate { m.Close(); ConfirmDeleteTask(id); }, "Delete", true);
            return m;
        }
        void ConfirmDeleteTask(string id)
        {
            if (!SaveDetailDraft()) return;
            var task = service.State.Tasks.FirstOrDefault(t => t.Id == id); if (task == null) return;
            using (var dialog = new DeleteTaskDialog(task.Text))
            {
                if (ShowUtilityDialog(dialog) != DialogResult.OK) return;
                if (Run(delegate { service.Delete(id); }, "已删除任务") && detailId == id) CloseDetail();
            }
        }
        ModernMenu NewMenu()
        {
            var m = new ModernMenu(DeviceDpiScale());
            m.Closed += delegate { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)delegate { m.Dispose(); }); else m.Dispose(); }; return m;
        }
        ContextMenuStrip TrayMenu()
        {
            var m = new ModernMenu(DeviceDpiScale());
            m.AddCommand("显示桌面待办", MenuGlyph.Detail, delegate { ShowOnDesktop(); });
            m.AddCommand("打开数据目录", MenuGlyph.Folder, delegate { System.Diagnostics.Process.Start("explorer.exe", store.DirectoryPath); });
            m.AddDivider();
            m.AddCommand("退出", MenuGlyph.Exit, delegate { Exit(); }); return m;
        }
        void ShowSettings(Control anchor, Point at)
        { BuildAppMenu().Show(anchor, at); }
        ModernMenu BuildAppMenu()
        {
            var m = NewMenu();
            if (inspectUi) m.AddCommand("测试：切换桌面层", MenuGlyph.Position, delegate { desktopMode = !desktopMode; if (desktopMode) Pin(); else Native.SetOwner(Handle, IntPtr.Zero); WriteDiagnostics(); });
            m.AddCommand("设置", MenuGlyph.Settings, delegate { OpenPreferences(); });
            m.AddCommand("新建列表",MenuGlyph.Plus,delegate { CreateListFromUi(); });
            m.AddDivider();
            var undo = m.AddCommand("撤销上一步", MenuGlyph.Undo, delegate { if (SaveDetailDraft()) { Run(delegate { service.Undo(); }, "已撤销"); if (detailRoot.Visible) RefreshDetails(); } }, "Ctrl+Z"); undo.Enabled = service.CanUndo;
            var resetPosition = m.AddCommand("重置到屏幕右侧", MenuGlyph.Position, delegate { Rectangle a = Screen.PrimaryScreen.WorkingArea; Location = new Point(a.Right - Width - D(28), a.Top + D(72)); }); resetPosition.Enabled = !service.State.GeometryLocked;
            m.AddDivider();
            m.AddCommand("导出任务备份", MenuGlyph.Export, delegate { Export(); });
            m.AddCommand("打开数据目录", MenuGlyph.Folder, delegate { System.Diagnostics.Process.Start("explorer.exe", store.DirectoryPath); });
            m.AddDivider();
            m.AddCommand("退出", MenuGlyph.Exit, delegate { Exit(); }); return m;
        }
        void OpenPreferences()
        {
            try { if (!SaveDetailDraft()) return; using (var d = new SettingsDialog(startup, SendTestNotification)) ShowUtilityDialog(d); }
            catch (Exception e) { MessageBox.Show(e.Message, "无法打开设置"); }
        }
        void Export()
        {
            using (var d = new SaveFileDialog { Title = "导出任务备份", Filter = "JSON 备份 (*.json)|*.json", FileName = "桌面待办备份-" + DateTime.Now.ToString("yyyyMMdd") + ".json", OverwritePrompt = true })
                if (d.ShowDialog() == DialogResult.OK)
                    try { File.WriteAllText(d.FileName, Store.Encode(service.State), new UTF8Encoding(false)); message = "备份已导出"; RenderTasks(); }
                    catch (Exception e) { MessageBox.Show(e.Message, "导出失败"); }
        }
        void ShowOnDesktop() { Show(); ClampToScreen(); Pin(); }
        void Exit() { closing = true; Close(); }
        void QueueBounds() { locationTimer.Stop(); locationTimer.Start(); }
        void SaveBounds()
        {
            if (!layoutReady || savingBounds || WindowState != FormWindowState.Normal) return;
            savingBounds = true;
            try { float scale = DeviceDpiScale(); service.Settings(s => { s.X = Left; s.Y = Top; s.Width = (int)(Width / scale); s.Height = (int)(Height / scale); }); }
            catch (Exception e) { status.Text = "位置未保存：" + e.Message; }
            finally { savingBounds = false; }
        }
        void ClampToScreen()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            if (Width > area.Width) Width = area.Width; if (Height > area.Height) Height = area.Height;
            Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width)); Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
        }
        void DragHeader(object sender, MouseEventArgs e)
        { if (!service.State.GeometryLocked && e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } }
        void ToggleGeometryLock()
        {
            try { if (!service.State.GeometryLocked) SaveBounds(); service.Settings(s => s.GeometryLocked = !s.GeometryLocked); UpdateGeometryPin(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "无法保存固定状态"); }
        }
        void UpdateGeometryPin()
        {
            bool locked = service.State.GeometryLocked;
            geometryPin.IsPinned = locked; geometryPin.AccessibleName = locked ? "解除位置和大小固定" : "固定位置和大小";
            tips.SetToolTip(geometryPin, locked ? "已固定：点击后允许移动和调整大小" : "固定位置和大小"); geometryPin.Invalidate();
            geometryPin.BackColor = locked ? Color.FromArgb(68, 91, 168) : Color.Transparent;
            title.AllowDrag=!locked;
            tips.SetToolTip(title, locked ? "点击标题切换列表（位置和大小已固定）" : "点击标题切换列表；拖动标题移动清单");
        }
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (layoutReady && service != null && service.State.GeometryLocked) { x = Left; y = Top; width = Width; height = Height; }
            base.SetBoundsCore(x, y, width, height, specified);
        }
        void Pin()
        {
            if (utilityDialogDepth > 0 || !desktopMode || !IsHandleCreated || IsDisposed || !Visible) return;
            IntPtr desktop = Native.FindWindow("Progman", null);
            if (desktop != IntPtr.Zero && Native.GetWindow(Handle, 4) != desktop) Native.SetOwner(Handle, desktop);
            Native.SetWindowPos(Handle, new IntPtr(1), 0, 0, 0, 0, 0x0013);
        }
        protected override void WndProc(ref Message m)
        {
            bool locked = layoutReady && service != null && service.State.GeometryLocked;
            if (locked && m.Msg == 0x112 && (((long)m.WParam & 0xFFF0) == 0xF000 || ((long)m.WParam & 0xFFF0) == 0xF010)) { m.Result = IntPtr.Zero; return; }
            if (m.Msg == 0x46 && m.LParam != IntPtr.Zero)
            {
                var p = (Native.WindowPos)Marshal.PtrToStructure(m.LParam, typeof(Native.WindowPos));
                if (desktopMode && utilityDialogDepth == 0 && (p.Flags & 4) == 0) p.After = new IntPtr(1);
                if (locked) p.Flags |= 0x0001 | 0x0002;
                Marshal.StructureToPtr(p, m.LParam, false);
            }
            base.WndProc(ref m);
            if (m.Msg == 0x84 && (int)m.Result == 1)
            {
                if (locked) { m.Result = new IntPtr(1); return; }
                Point p = PointToClient(new Point(unchecked((short)((long)m.LParam & 0xffff)), unchecked((short)(((long)m.LParam >> 16) & 0xffff))));
                int b = D(6); bool l = p.X < b, r = p.X >= Width - b, t = p.Y < b, bot = p.Y >= Height - b;
                m.Result = new IntPtr(t ? (l ? 13 : r ? 14 : 12) : bot ? (l ? 16 : r ? 17 : 15) : l ? 10 : r ? 11 : 1);
            }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && detailRoot.Visible) { CloseDetail(); return true; }
            if (keyData == (Keys.Control | Keys.N)) { if (CloseDetail()) input.Focus(); return true; }
            if (keyData == (Keys.Control | Keys.Z) && !IsEditingText(this)) { if (SaveDetailDraft()) { Run(delegate { service.Undo(); }, "已撤销"); if (detailRoot.Visible) RefreshDetails(); } return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (colors == null || ClientSize.Width < 1 || ClientSize.Height < 1) { base.OnPaintBackground(e); return; }
            PaintBaseSurface(e.Graphics);
        }
        public void PaintBaseSurface(Graphics graphics)
        {
            if (colors == null || ClientSize.Width < 1 || ClientSize.Height < 1) { graphics.Clear(BackColor); return; }
            using (var gradient = new LinearGradientBrush(ClientRectangle, colors.Bg, colors.Bottom, LinearGradientMode.Vertical)) graphics.FillRectangle(gradient, ClientRectangle);
        }
        void WriteDiagnostics()
        {
            if (string.IsNullOrEmpty(diagnosticPath)) return;
            try { File.WriteAllText(diagnosticPath, new JavaScriptSerializer().Serialize(new { OwnerIsDesktop = Native.GetWindow(Handle, 4) == Native.FindWindow("Progman", null), Topmost = (Native.GetStyle(Handle, -20) & 8) != 0, Visible = Native.IsWindowVisible(Handle), Minimized = Native.IsIconic(Handle), Left, Top, Width, Height, Handle = Handle.ToInt64(), Pending = service.State.Tasks.Count(t => !t.Done && !t.Deleted) })); } catch { }
        }
        void RunUiTests() { RunRenderTests(); }
        void SavePreview(string path)
        { using (var bitmap = new Bitmap(Width, Height)) { DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png); } }
        static string BitmapSignature(Bitmap bitmap)
        { using (var stream = new MemoryStream()) { bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(stream.ToArray())); } }
        static Icon MakeIcon()
        { return NotificationIcon.CreateIcon(32); }
    }

    static class SelfTests
    {
        static int count;
        static void Check(bool pass, string name) { if (!pass) throw new Exception("FAIL: " + name); count++; }
        public static int Run(string directory)
        {
            var log = new List<string>();
            try
            {
                Directory.CreateDirectory(directory);
                string root = Path.Combine(directory, "case-" + Guid.NewGuid().ToString("N"));
                using (var store = new Store(root))
                {
                    var svc = new TaskService(store); svc.Add("中文任务 / emoji 🌱 / 引号\"和换行\n第二行"); string id = svc.State.Tasks[0].Id;
                    Check(new TaskService(store).State.Tasks[0].Text == svc.State.Tasks[0].Text, "Unicode round trip");
                    svc.Toggle(id); Check(svc.State.Tasks[0].Done && svc.State.Tasks[0].CompletedUtc != null, "completion retained");
                    svc.Add("永久删除检查"); string removedId=svc.State.Tasks.Last().Id;
                    svc.AddStep(removedId,"不会残留的步骤"); svc.SaveText(removedId,"永久删除检查","不会残留的备注");
                    svc.Delete(removedId); Check(!svc.State.Tasks.Any(t => t.Id==removedId) && !new TaskService(store).State.Tasks.Any(t => t.Id==removedId), "permanent deletion removes task and detail payload from saved state");
                    svc.Undo(); Check(!svc.State.Tasks.Any(t => t.Id==removedId), "older undo cannot resurrect a permanently deleted task");
                    svc.Toggle(id); svc.Settings(s => s.Light = true); svc.Undo(); Check(svc.State.Tasks[0].Done && svc.State.Light, "undo task preserves preferences");
                    Check(File.Exists(store.FilePath + ".bak"), "backup created");
                    bool locked = false; try { using (var other = new Store(root)) { } } catch (IOException) { locked = true; } Check(locked, "exclusive writer");
                    var before = Store.Decode(File.ReadAllText(store.FilePath + ".bak"));
                    File.WriteAllText(store.FilePath, "{broken"); var recovered = store.Load();
                    Check(Store.Encode(before) == Store.Encode(recovered), "corrupt primary recovered from backup");
                    Check(Directory.GetFiles(root, "*.corrupt-*").Length == 1, "corrupt original preserved");
                    store.Save(recovered); Check(Store.Decode(File.ReadAllText(store.FilePath + ".bak")).Tasks.Count > 0, "valid backup after recovery");
                    var frozen = Store.Encode(svc.State); Directory.CreateDirectory(store.FilePath + ".tmp-block");
                    File.Move(store.FilePath + ".bak", store.FilePath + ".bak-original"); Directory.CreateDirectory(store.FilePath + ".bak");
                    bool saveFailed = false; try { svc.Add("should not be committed"); } catch (IOException) { saveFailed = true; } catch (UnauthorizedAccessException) { saveFailed = true; }
                    Check(saveFailed && frozen == Store.Encode(svc.State), "failed save does not commit in memory");
                }
                string invalid = root + "-invalid";
                using (var s = new Store(invalid))
                { File.WriteAllText(s.FilePath, "broken"); bool refused = false; try { s.Load(); } catch (InvalidDataException) { refused = true; } Check(refused && File.ReadAllText(s.FilePath) == "broken", "unrecoverable data is not overwritten"); }
                bool schema = false; try { Store.Decode("{\"Version\":99,\"Tasks\":[]}"); } catch (InvalidDataException) { schema = true; } Check(schema, "future schema rejected");
                bool incomplete = false; try { Store.Decode("{}"); } catch (InvalidDataException) { incomplete = true; } Check(incomplete, "incomplete JSON is not treated as an empty list");
                var memoryStartup = new MemoryStartupStore { Value = "\"C:\\Old Path\\DesktopTodo.exe\"" };
                var startup = new StartupService(memoryStartup, "C:\\New Path\\DesktopTodo.exe");
                Check(startup.Enabled, "startup recognizes old executable path"); startup.SetEnabled(false);
                Check(!startup.Enabled && memoryStartup.Value == null, "startup disable removes old entry"); startup.SetEnabled(true);
                Check(startup.Enabled && memoryStartup.Value == "\"C:\\New Path\\DesktopTodo.exe\"", "startup enable quotes current path"); startup.SetEnabled(false);
                Check(!startup.Enabled, "startup switch can be disabled repeatedly");
                using (var featureStore = new Store(root + "-features"))
                {
                    var features = new TaskService(featureStore); features.Add("准备会议"); string id = features.State.Tasks[0].Id;
                    string creation = features.State.Tasks[0].CreatedUtc;
                    features.AddStep(id, "准备资料"); features.AddStep(id, "发送邀请"); string step = features.State.Tasks[0].Steps[0].Id;
                    features.ToggleStep(id, step); Check(features.State.Tasks[0].Steps[0].Done && !features.State.Tasks[0].Done, "step completion is independent of parent");
                    features.Toggle(id); Check(features.State.Tasks[0].Steps[0].Done && !features.State.Tasks[0].Steps[1].Done, "parent completion preserves individual step states"); features.Toggle(id);
                    features.SaveText(id, "准备下周会议", "议题一：进度\n议题二：分工 & 🌱"); Check(features.State.Tasks[0].Notes.Contains("\n"), "multiline notes saved");
                    features.SaveDetailText(id,features.State.Tasks[0].Text,features.State.Tasks[0].Notes,new Dictionary<string,string>{{step," 更新资料与检测记录 🌱 "}});
                    Check(features.State.Tasks[0].Steps[0].Text=="更新资料与检测记录 🌱" && features.State.Tasks[0].Steps[0].Done && !features.State.Tasks[0].Done,"inline step rename preserves step and parent completion");
                    Check(new TaskService(featureStore).State.Tasks[0].Steps[0].Id==step && new TaskService(featureStore).State.Tasks[0].CreatedUtc==creation,"step rename persists with stable identifiers and creation timestamp");
                    string beforeInvalid=Store.Encode(features.State); bool blankStepRejected=false;
                    try { features.SaveDetailText(id,"不应部分保存的标题","不应保存的备注",new Dictionary<string,string>{{step," "}}); } catch(ArgumentException) { blankStepRejected=true; }
                    Check(blankStepRejected && Store.Encode(features.State)==beforeInvalid && Store.Encode(new TaskService(featureStore).State)==beforeInvalid,"invalid step prevents partial saving of title and notes");
                    bool longStepRejected=false; try { features.SaveDetailText(id,features.State.Tasks[0].Text,features.State.Tasks[0].Notes,new Dictionary<string,string>{{step,new string('长',2001)}}); } catch(ArgumentException) { longStepRejected=true; }
                    Check(longStepRejected && Store.Encode(features.State)==beforeInvalid,"overlong inline step is rejected without changes");
                    bool missingStepRejected=false; try { features.SaveDetailText(id,features.State.Tasks[0].Text,features.State.Tasks[0].Notes,new Dictionary<string,string>{{Guid.NewGuid().ToString("N"),"不存在的步骤"}}); } catch(ArgumentException) { missingStepRejected=true; }
                    Check(missingStepRejected && Store.Encode(features.State)==beforeInvalid,"stale step identifiers cannot overwrite another step");
                    string secondStep=features.State.Tasks[0].Steps[1].Id;
                    features.SaveDetailText(id,"同次保存的标题","同次保存的备注",new Dictionary<string,string>{{step,""},{secondStep,"同次保存的另一步"}},step);
                    Check(features.State.Tasks[0].Steps.Count==1 && features.State.Tasks[0].Steps[0].Text=="同次保存的另一步" && features.State.Tasks[0].Text=="同次保存的标题","deleting an empty draft atomically saves the other detail edits");
                    features.Undo(); Check(Store.Encode(features.State)==beforeInvalid,"undo restores the whole inline-edit deletion transaction");
                    features.SetDueDate(id, new DateTime(2026, 12, 1)); Check(features.State.Tasks[0].DueDate == "2026-12-01", "deadline is saved as a local date");
                    var now = DateTimeOffset.UtcNow; features.SetReminder(id, now.AddMinutes(-1)); Check(features.DueReminders(now).Count == 1, "overdue reminder is due");
                    features.AddStep(id, "确认场地"); string scheduled = features.State.Tasks[0].ReminderUtc;
                    features.MarkReminderDelivered(id, scheduled, now); Check(features.DueReminders(now).Count == 0, "delivered reminder is not repeated");
                    features.Undo(); Check(features.DueReminders(now).Count == 0, "undoing a task edit does not resend the same reminder");
                    Check(new TaskService(featureStore).DueReminders(now).Count == 0, "reminder delivery survives restart");
                    features.SetReminder(id, now.AddMinutes(5)); Check(features.DueReminders(now).Count == 0 && features.State.Tasks[0].ReminderDeliveredUtc == null, "rescheduling clears delivery state and waits for future time");
                    features.SetReminder(id, now.AddMinutes(-2)); features.Toggle(id); Check(features.DueReminders(now).Count == 0, "completed tasks do not notify"); features.Toggle(id);
                    features.SetReminder(id,now.AddDays(1)); features.Add("即将删除的提醒"); string deleteReminderId=features.State.Tasks.Last().Id; features.SetReminder(deleteReminderId,now.AddMinutes(-2));
                    features.Delete(deleteReminderId); Check(features.DueReminders(now).Count == 0, "permanently deleted tasks do not notify"); features.SetReminder(id,now.AddMinutes(-2));
                    Check(new TaskService(featureStore).DueReminders(now).Count == 1, "unnotified overdue reminder is found after restart");
                    features.SetReminder(id, null); features.SetDueDate(id, null); Check(features.State.Tasks[0].ReminderUtc == null && features.State.Tasks[0].DueDate == null, "reminder and deadline can be cleared");
                    Check(features.State.Tasks[0].CreatedUtc == creation, "creation timestamp survives edits and restoration");
                    Check(new TaskService(featureStore).State.Tasks[0].Steps.Count == 2 && new TaskService(featureStore).State.Tasks[0].Notes.Contains("分工"), "steps and notes survive reload");
                    features.RemoveStep(id, step); features.Undo(); Check(features.State.Tasks[0].Steps.Any(s => s.Id == step), "deleting a step can be undone");
                    features.Settings(s => s.GeometryLocked = true); Check(new TaskService(featureStore).State.GeometryLocked, "geometry lock survives reload");
                    features.SetCountdown(id, 25, false, now); DateTimeOffset target = DateTimeOffset.Parse(features.State.Tasks[0].ReminderUtc);
                    Check(target == now.ToUniversalTime().AddMinutes(25) && features.State.Tasks[0].ReminderCountdownMinutes == 25, "minute countdown stores the correct target");
                    Check(features.DueReminders(now.AddMinutes(24).AddSeconds(59)).Count == 0, "countdown does not notify early");
                    Check(features.DueReminders(now.AddMinutes(25)).Count == 1, "countdown becomes due at the target");
                    Check(new TaskService(featureStore).State.Tasks[0].ReminderCountdownMinutes == 25 && new TaskService(featureStore).State.Tasks[0].ReminderUtc == target.ToString("o"), "countdown survives restarting the program");
                    features.MarkReminderDelivered(id, target.ToString("o"), target); Check(features.DueReminders(target.AddMinutes(1)).Count == 0, "countdown reminder is delivered once");
                    features.SetCountdown(id, 1, true, now); Check(DateTimeOffset.Parse(features.State.Tasks[0].ReminderUtc) == now.ToUniversalTime().AddHours(1), "hour countdown is converted correctly");
                    features.SetReminder(id, now.AddDays(1)); Check(features.State.Tasks[0].ReminderCountdownMinutes == null, "absolute reminder exits countdown mode");
                    features.SetCountdown(id, 5, false, now); features.SetReminder(id, null); Check(features.State.Tasks[0].ReminderCountdownMinutes == null && features.State.Tasks[0].ReminderUtc == null, "clearing a reminder cancels countdown mode");
                    var midnight = new DateTimeOffset(2026, 10, 29, 23, 50, 0, TimeSpan.FromHours(8));
                    Check(TaskService.CountdownTarget(midnight, 25, false).ToOffset(TimeSpan.FromHours(8)) == midnight.AddMinutes(25), "countdown crosses midnight correctly");
                    bool zeroRejected = false; try { TaskService.CountdownTarget(now, 0, false); } catch (ArgumentOutOfRangeException) { zeroRejected = true; } Check(zeroRejected, "zero duration is rejected");
                    bool longRejected = false; try { TaskService.CountdownTarget(now, 721, true); } catch (ArgumentOutOfRangeException) { longRejected = true; } Check(longRejected, "duration beyond thirty days is rejected");
                    Check(TaskService.CountdownTarget(now, 720, true) == now.ToUniversalTime().AddDays(30), "maximum countdown duration is supported");
                    NotificationTests.Run(Check, features, id, now);
                }
                ListTests.Run(Check,root+"-lists");
                CompletionTests.Run(Check,root+"-completion");
                log.Add("PASS: " + count + " checks"); log.Add("Data cases: " + root);
                File.WriteAllLines(Path.Combine(directory, "self-test.txt"), log, Encoding.UTF8); return 0;
            }
            catch (Exception e) { log.Add(e.ToString()); File.WriteAllLines(Path.Combine(directory, "self-test.txt"), log, Encoding.UTF8); return 1; }
        }
    }

    static class Program
    {
        [STAThread] static int Main(string[] args)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTodo");
            string diagnostics = null, uiTestDirectory = null; bool demo = false, inspectUi = false, explicitDataDirectory = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--self-test" && i + 1 < args.Length) return SelfTests.Run(Path.GetFullPath(args[i + 1]));
                if (args[i] == "--notification-integration-test" && i + 1 < args.Length) return NotificationTests.RunWindowsIntegration(Path.GetFullPath(args[i + 1]));
                if (args[i] == "--data-dir" && i + 1 < args.Length) { directory = Path.GetFullPath(args[++i]); explicitDataDirectory = true; }
                else if (args[i] == "--diagnostics" && i + 1 < args.Length) diagnostics = Path.GetFullPath(args[++i]);
                else if (args[i] == "--demo") demo = true;
                else if (args[i] == "--inspect-ui") inspectUi = true;
                else if (args[i] == "--render-test" && i + 1 < args.Length) uiTestDirectory = Path.GetFullPath(args[++i]);
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string normalDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopTodo");
            if ((uiTestDirectory != null || demo) && (!explicitDataDirectory || diagnostics == null || string.Equals(Path.GetFullPath(directory), Path.GetFullPath(normalDirectory), StringComparison.OrdinalIgnoreCase)))
            { MessageBox.Show("测试模式必须指定独立的数据目录和诊断输出，不能使用正式清单目录。", "桌面待办"); return 1; }
            try
            {
                using (var store = new Store(directory))
                {
                    if (demo && !File.Exists(store.FilePath) && diagnostics != null)
                    {
                        var svc = new TaskService(store); svc.Add("把今天最重要的一件事写下来"); svc.Add("散步十分钟，给眼睛放个假"); svc.Add("长任务也能自动换行：整理桌面上的资料，给每个文件找到合适的位置，再慢慢推进下一件事。"); svc.Add("已经完成的事情，也值得被记住"); svc.Toggle(svc.State.Tasks.Last().Id);
                        svc.Change(s => s.Tasks[0].Important = true);
                    }
                    Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                    {
                        if(uiTestDirectory != null) { Directory.CreateDirectory(uiTestDirectory); File.AppendAllText(Path.Combine(uiTestDirectory,"unhandled-ui-error.txt"), e.Exception.ToString()); Environment.Exit(1); }
                        MessageBox.Show("遇到错误：" + e.Exception.Message + "\n已保存的数据仍在本机。", "桌面待办");
                    };
                    Application.Run(new TodoForm(store, diagnostics, inspectUi, uiTestDirectory));
                }
                return Environment.ExitCode;
            }
            catch (IOException e) { MessageBox.Show("无法打开数据目录。可能已有一个实例正在使用此清单。\n\n" + e.Message, "桌面待办"); return 1; }
            catch (Exception e) { MessageBox.Show(e.Message + "\n\n数据目录：" + directory, "桌面待办", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        }
    }
}

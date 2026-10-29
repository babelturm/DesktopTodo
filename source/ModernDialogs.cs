using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace DesktopTodo
{
    enum DialogButtonKind { Secondary, Primary, Quiet, Close, Danger }
    // Do not derive from Button: its flat-style adapter can repaint borders
    // directly on hover/focus, outside the custom rounded OnPaint surface.
    class DialogButton : Control, IButtonControl
    {
        public DialogButtonKind Kind;
        public bool CalendarIcon, ChevronIcon, AlignTextLeft;
        bool hover, pressed;
        MouseButtons lastMouseButton;
        public DialogResult DialogResult { get; set; }
        protected bool CanActivate { get { return Enabled && lastMouseButton != MouseButtons.Right && lastMouseButton != MouseButtons.Middle; } }
        public DialogButton(string text, DialogButtonKind kind = DialogButtonKind.Secondary)
        {
            Text = text; Kind = kind; Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);
        }
        public void NotifyDefault(bool value) { Invalidate(); }
        public void PerformClick() { if (Enabled) { lastMouseButton = MouseButtons.None; OnClick(EventArgs.Empty); } }
        protected override void OnClick(EventArgs e)
        {
            if (!CanActivate) return;
            var form = FindForm(); if (form != null) form.DialogResult = DialogResult;
            base.OnClick(e);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { lastMouseButton = e.Button; if (e.Button == MouseButtons.Left) { Focus(); pressed = true; } Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); lastMouseButton = MouseButtons.None; }
        protected override void OnMouseCaptureChanged(EventArgs e) { pressed = false; Invalidate(); base.OnMouseCaptureChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Cursor = Enabled ? Cursors.Hand : Cursors.Default; }
        protected override bool IsInputKey(Keys keyData) { var key = keyData & Keys.KeyCode; return key == Keys.Space || key == Keys.Enter || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; PerformClick(); }
            else if (e.KeyCode == Keys.Space) { e.SuppressKeyPress = true; pressed = true; Invalidate(); }
            base.OnKeyDown(e);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        { if (e.KeyCode == Keys.Space) { bool click = pressed; pressed = false; e.SuppressKeyPress = true; Invalidate(); if (click) PerformClick(); } base.OnKeyUp(e); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new DialogButtonAccessible(this); }
        class DialogButtonAccessible : ControlAccessibleObject
        {
            readonly DialogButton button;
            public DialogButtonAccessible(DialogButton button) : base(button) { this.button = button; }
            public override string DefaultAction { get { return "按下"; } }
            public override void DoDefaultAction() { if (button.InvokeRequired) button.BeginInvoke((Action)button.PerformClick); else button.PerformClick(); }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Parent == null ? SystemColors.Control : Parent.BackColor); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // Repaint the full rectangular surface, including the pixels outside
            // the rounded path, on every frame and every state transition.
            g.Clear(Parent == null ? SystemColors.Control : Parent.BackColor);
            float scale = g.DpiX / 96F; g.SmoothingMode = SmoothingMode.AntiAlias;
            bool primary = Kind == DialogButtonKind.Primary || Kind == DialogButtonKind.Danger, quiet = Kind == DialogButtonKind.Quiet || Kind == DialogButtonKind.Close;
            Color fill = primary ? Color.FromArgb(0, 95, 184) : quiet ? (Parent == null ? Color.FromArgb(243,243,243) : Parent.BackColor) : Color.White;
            Color text = primary ? Color.White : Color.FromArgb(32,32,32);
            if (hover && Enabled) fill = primary ? Color.FromArgb(0, 84, 166) : Color.FromArgb(233,233,233);
            if (pressed && Enabled) fill = primary ? Color.FromArgb(0, 72, 148) : Color.FromArgb(222,222,222);
            if (Kind == DialogButtonKind.Danger) fill = pressed ? Color.FromArgb(175,25,33) : hover ? Color.FromArgb(199,30,39) : Color.FromArgb(218,38,47);
            if (!Enabled) { fill = Color.FromArgb(243,243,243); text = Color.FromArgb(158,158,158); }
            if (Kind == DialogButtonKind.Close && hover && Enabled) { fill = Color.FromArgb(196,43,28); text = Color.White; }
            using (var path = Shapes.Round(new RectangleF(.5F,.5F,Width-1,Height-1), 5 * scale))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush,path);
                if (!quiet && !primary) using (var pen = new Pen(Color.FromArgb(211,211,211))) g.DrawPath(pen,path);
            }
            if (Kind == DialogButtonKind.Close)
            { using (var pen = new Pen(text,1.3F*scale)) { float r=5*scale,cx=Width/2F,cy=Height/2F; g.DrawLine(pen,cx-r,cy-r,cx+r,cy+r); g.DrawLine(pen,cx-r,cy+r,cx+r,cy-r); } }
            else
            {
                var rect = ClientRectangle;
                if (CalendarIcon || ChevronIcon || AlignTextLeft) rect = new Rectangle((int)(12*scale),0,Math.Max(1,Width-(int)((CalendarIcon || ChevronIcon ? 44 : 24)*scale)),Height);
                TextRenderer.DrawText(g,Text,Font,rect,text,TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | (CalendarIcon || ChevronIcon || AlignTextLeft ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
                if (CalendarIcon || ChevronIcon) using (var pen=new Pen(text,1.2F*scale))
                {
                    float x=Width-26*scale,y=Height/2F;
                    if (CalendarIcon) { g.DrawRectangle(pen,x,y-6*scale,13*scale,12*scale); g.DrawLine(pen,x,y-2*scale,x+13*scale,y-2*scale); g.DrawLine(pen,x+3*scale,y-8*scale,x+3*scale,y-4*scale); g.DrawLine(pen,x+10*scale,y-8*scale,x+10*scale,y-4*scale); }
                    else g.DrawLines(pen,new[] {new PointF(x,y-2*scale),new PointF(x+5*scale,y+3*scale),new PointF(x+10*scale,y-2*scale)});
                }
            }
            if (Focused && ShowFocusCues) using (var path=Shapes.Round(new RectangleF(3*scale,3*scale,Width-6*scale,Height-6*scale),3*scale)) using(var pen=new Pen(primary ? Color.White : Color.FromArgb(0,95,184),scale)) g.DrawPath(pen,path);
        }
    }
    class DialogCard : Panel
    {
        public DialogCard() { BackColor = Color.White; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g=e.Graphics; g.Clear(Parent==null ? Color.FromArgb(243,243,243) : Parent.BackColor); g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=Shapes.Round(new RectangleF(.5F,.5F,Width-1,Height-1),8*g.DpiX/96F)) { using(var brush=new SolidBrush(BackColor)) g.FillPath(brush,path); using(var pen=new Pen(Color.FromArgb(227,227,227))) g.DrawPath(pen,path); }
        }
    }
    class ModernDialog : FloatingDialog
    {
        protected readonly Panel Body = new Panel { AutoScroll = true }, Footer = new Panel();
        readonly Panel header = new Panel();
        readonly Label heading = new Label();
        readonly DialogButton close = new DialogButton("",DialogButtonKind.Close);
        readonly List<Font> fonts = new List<Font>();
        protected readonly float UiScale;
        protected static readonly Color Muted = Color.FromArgb(99,99,99), Accent = Color.FromArgb(0,95,184);
        protected int ContentWidth { get { return Body.ClientSize.Width - D(48); } }
        public ModernDialog(string title, int width, int height)
        {
            using(var g=Graphics.FromHwnd(IntPtr.Zero)) UiScale=g.DpiX/96F;
            AutoScaleMode=AutoScaleMode.None; Font=F(9); Text=title; BackColor=Color.FromArgb(243,243,243); ForeColor=Color.FromArgb(32,32,32);
            FormBorderStyle=FormBorderStyle.None; MaximizeBox=MinimizeBox=false; StartPosition=FormStartPosition.CenterParent;
            var area=Screen.PrimaryScreen.WorkingArea; ClientSize=new Size(Math.Min(D(width),area.Width-D(32)),Math.Min(D(height),area.Height-D(32)));
            header.BackColor=BackColor; Body.BackColor=BackColor; Footer.BackColor=Color.FromArgb(249,249,249);
            heading.Text=title; heading.Font=F(18,true); heading.ForeColor=ForeColor; heading.TextAlign=ContentAlignment.MiddleLeft;
            close.AccessibleName="关闭"; close.DialogResult=DialogResult.Cancel;
            header.Controls.AddRange(new Control[]{heading,close}); Controls.AddRange(new Control[]{header,Body,Footer});
            MouseEventHandler drag=delegate(object sender,MouseEventArgs e) { if(e.Button==MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero); } };
            heading.MouseDown+=drag; header.MouseDown+=drag;
            Footer.Paint+=delegate(object sender,PaintEventArgs e) { using(var pen=new Pen(Color.FromArgb(225,225,225))) e.Graphics.DrawLine(pen,0,0,Footer.Width,0); };
            LayoutChrome();
        }
        protected int D(int value) { return (int)Math.Round(value*UiScale); }
        protected Font F(float size,bool bold=false) { var font=new Font("Microsoft YaHei UI",size,bold ? FontStyle.Bold : FontStyle.Regular); fonts.Add(font); return font; }
        protected Label LabelAt(Control parent,string text,int x,int y,int w,int h,float size=9,bool bold=false,Color? color=null)
        { var label=new Label {Text=text,Font=F(size,bold),ForeColor=color ?? ForeColor,BackColor=Color.Transparent,TextAlign=ContentAlignment.MiddleLeft}; label.SetBounds(D(x),D(y),D(w),D(h)); parent.Controls.Add(label); return label; }
        protected void Place(Control parent,Control control,int x,int y,int w,int h)
        { control.Font=Font; control.SetBounds(D(x),D(y),D(w),D(h)); parent.Controls.Add(control); }
        protected DialogCard Card(int y,int height)
        { var card=new DialogCard(); card.SetBounds(D(24),D(y),ContentWidth,D(height)); Body.Controls.Add(card); return card; }
        void LayoutChrome()
        {
            header.SetBounds(0,0,ClientSize.Width,D(68)); heading.SetBounds(D(24),D(14),ClientSize.Width-D(100),D(42)); close.SetBounds(ClientSize.Width-D(52),D(12),D(36),D(36));
            Body.SetBounds(0,D(68),ClientSize.Width,Math.Max(1,ClientSize.Height-D(144))); Footer.SetBounds(0,ClientSize.Height-D(76),ClientSize.Width,D(76));
            if(Width>0 && Height>0) using(var path=Shapes.Round(new RectangleF(0,0,Width,Height),D(10))) { var old=Region; Region=new Region(path); if(old!=null) old.Dispose(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias; using(var path=Shapes.Round(new RectangleF(.5F,.5F,Width-1,Height-1),D(10))) using(var pen=new Pen(Color.FromArgb(207,207,207))) e.Graphics.DrawPath(pen,path); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if(disposing) foreach(var font in fonts) font.Dispose(); }
    }
    class DeleteTaskDialog : ModernDialog
    {
        public readonly DialogButton ConfirmButton = new DialogButton("删除", DialogButtonKind.Danger);
        public readonly DialogButton CancelAction = new DialogButton("取消");
        public DeleteTaskDialog(string taskTitle) : base("删除任务", 420, 290)
        {
            string text = "将永久删除“" + taskTitle + "”。\n任务及其步骤、备注将一并删除，无法撤销。";
            int width = ContentWidth;
            int height = TextRenderer.MeasureText(text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height + D(16);
            var label = new Label { Text = text, Font = Font, ForeColor = ForeColor, BackColor = Color.Transparent, AccessibleName = text };
            label.SetBounds(D(24), D(8), width, Math.Max(D(90), height)); Body.Controls.Add(label);
            Body.AutoScrollMinSize = new Size(0, label.Bottom + D(12));
            ConfirmButton.DialogResult = DialogResult.OK; CancelAction.DialogResult = DialogResult.Cancel;
            ConfirmButton.Font = CancelAction.Font = Font; Footer.Controls.AddRange(new Control[] { ConfirmButton, CancelAction });
            // Fit both controls even on a small working area.
            int buttonWidth = (ClientSize.Width - D(60)) / 2;
            ConfirmButton.SetBounds(D(24), D(18), buttonWidth, D(38)); CancelAction.SetBounds(D(36) + buttonWidth, D(18), buttonWidth, D(38));
            AcceptButton = CancelAction; CancelButton = CancelAction;
        }
        protected override void OnShown(EventArgs e) { ActiveControl = CancelAction; CancelAction.Select(); base.OnShown(e); }
    }
    class DialogTextField : Panel
    {
        public readonly TextBox Editor = new TextBox();
        public DialogTextField(float scale)
        {
            BackColor=Color.White; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw,true);
            Editor.BorderStyle=BorderStyle.None; Editor.BackColor=BackColor; Editor.AccessibleName="提醒时间"; Controls.Add(Editor);
            Editor.GotFocus+=delegate { Invalidate(); }; Editor.LostFocus+=delegate { Invalidate(); };
            Resize+=delegate { Editor.SetBounds((int)(12*scale),(Height-Editor.PreferredHeight)/2,Math.Max(1,Width-(int)(24*scale)),Editor.PreferredHeight); };
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if(Editor!=null) Editor.Font=Font; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=Shapes.Round(new RectangleF(.5F,.5F,Width-1,Height-1),5*g.DpiX/96F)) using(var pen=new Pen(ContainsFocus ? Color.FromArgb(0,95,184) : Color.FromArgb(211,211,211),ContainsFocus ? 2 : 1)) g.DrawPath(pen,path);
        }
    }
    class DialogNumberField : DialogTextField
    {
        readonly DialogButton minus=new DialogButton("−",DialogButtonKind.Quiet), plus=new DialogButton("+",DialogButtonKind.Quiet);
        int maximum=43200;
        public int Maximum { get { return maximum; } set { maximum=value; int n; if(int.TryParse(Editor.Text,out n) && n>maximum) Value=maximum; } }
        public int Value { get { int n; if(!int.TryParse(Editor.Text,out n) || n<1 || n>Maximum) throw new ArgumentException("请输入 1 至 "+Maximum+" 的整数时长。"); return n; } set { Editor.Text=Math.Max(1,Math.Min(Maximum,value)).ToString(CultureInfo.InvariantCulture); } }
        public DialogNumberField(float scale) : base(scale)
        {
            Editor.AccessibleName="倒计时时长";
            Controls.AddRange(new Control[]{minus,plus}); minus.TabStop=plus.TabStop=false; minus.AccessibleName="减少时长"; plus.AccessibleName="增加时长";
            Action<int> step=delta=> { int n; if(!int.TryParse(Editor.Text,out n)) n=1; Value=Math.Max(1,Math.Min(Maximum,n+delta)); };
            minus.Click+=delegate { step(-1); }; plus.Click+=delegate { step(1); };
            Editor.KeyDown+=delegate(object sender,KeyEventArgs e) { if(e.KeyCode==Keys.Up || e.KeyCode==Keys.Down) { step(e.KeyCode==Keys.Up ? 1 : -1); e.SuppressKeyPress=true; } };
            Resize+=delegate { int b=(int)(30*scale),pad=(int)(4*scale); plus.SetBounds(Width-b-pad,pad,b,Height-2*pad); minus.SetBounds(Width-2*b-2*pad,pad,b,Height-2*pad); Editor.Width=Math.Max(1,Width-2*b-(int)(25*scale)); };
        }
    }
    class DialogDateField : DialogButton
    {
        DateTime value=DateTime.Today;
        readonly float scale;
        public event EventHandler ValueChanged;
        internal ModernMenu ActivePopup { get; private set; }
        public DateTime Value { get { return value; } set { bool changed=this.value!=value.Date; this.value=value.Date; Text=this.value.ToString("yyyy年M月d日"); if(changed && ValueChanged!=null) ValueChanged(this,EventArgs.Empty); } }
        public DialogDateField(float scale) : base("") { this.scale=scale; CalendarIcon=true; Value=DateTime.Today; AccessibleName="提醒日期"; }
        protected override void OnClick(EventArgs e)
        {
            if(!CanActivate) return;
            base.OnClick(e);
            if(ActivePopup!=null) { ActivePopup.Close(); return; }
            var calendar=new MonthCalendar {MaxSelectionCount=1,ShowTodayCircle=false,SelectionStart=Value,Font=Font};
            var host=new ToolStripControlHost(calendar) {Margin=Padding.Empty,Padding=Padding.Empty};
            var popup=new ModernMenu(scale); ActivePopup=popup; popup.Items.Add(host);
            calendar.DateSelected+=delegate { Value=calendar.SelectionStart; popup.Close(); };
            popup.Closed+=delegate { ActivePopup=null; DialogPopupLifetime.DisposeLater(this,popup); };
            popup.Show(this,new Point(0,Height+(int)(4*scale))); calendar.Focus();
        }
    }
    class DialogUnitField : DialogButton
    {
        readonly float scale; int index;
        internal ModernMenu ActivePopup { get; private set; }
        public event EventHandler SelectedIndexChanged;
        public int SelectedIndex { get { return index; } set { if(value!=0 && value!=1) throw new ArgumentOutOfRangeException("value"); bool changed=index!=value; index=value; Text=index==0 ? "分钟" : "小时"; if(changed && SelectedIndexChanged!=null) SelectedIndexChanged(this,EventArgs.Empty); } }
        public DialogUnitField(float scale) : base("分钟") { this.scale=scale; ChevronIcon=true; AccessibleName="倒计时单位"; }
        protected override void OnClick(EventArgs e)
        {
            if(!CanActivate) return; base.OnClick(e); if(ActivePopup!=null) { ActivePopup.Close(); return; } var popup=new ModernMenu(scale); ActivePopup=popup;
            for(int i=0;i<2;i++) { int selected=i; popup.AddCommand(i==0 ? "分钟" : "小时",index==i ? MenuGlyph.Complete : MenuGlyph.None,delegate { SelectedIndex=selected; }); }
            popup.Closed+=delegate { ActivePopup=null; DialogPopupLifetime.DisposeLater(this,popup); }; popup.Show(this,new Point(0,Height+(int)(4*scale)));
        }
    }
    static class DialogPopupLifetime
    {
        public static void DisposeLater(Control owner,ModernMenu popup)
        {
            // ToolStripDropDown still reparents its HWND after the Closed event.
            // Dispose on the next UI turn rather than inside that event.
            var context=System.Threading.SynchronizationContext.Current;
            if(context!=null) context.Post(delegate { popup.Dispose(); },null);
            else if(owner.IsHandleCreated && !owner.IsDisposed) owner.BeginInvoke((Action)delegate { popup.Dispose(); });
        }
    }
    class DialogChoice : DialogButton
    {
        bool isChecked;
        public event EventHandler CheckedChanged;
        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if(isChecked==value) return;
                isChecked=value; TabStop=value;
                if(value && Parent!=null) foreach(Control control in Parent.Controls) { var other=control as DialogChoice; if(other!=null && other!=this) other.Checked=false; }
                Invalidate(); if(CheckedChanged!=null) CheckedChanged(this,EventArgs.Empty);
            }
        }
        public DialogChoice(string text) : base(text,DialogButtonKind.Quiet) { TabStop=false; AccessibleRole=AccessibleRole.RadioButton; }
        protected override void OnClick(EventArgs e) { if(!CanActivate) return; Checked=true; base.OnClick(e); }
        protected override bool IsInputKey(Keys keyData) { var key=keyData & Keys.KeyCode; return key==Keys.Left || key==Keys.Right || key==Keys.Up || key==Keys.Down || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if(e.KeyCode==Keys.Left || e.KeyCode==Keys.Right || e.KeyCode==Keys.Up || e.KeyCode==Keys.Down)
            {
                if(Parent!=null) foreach(Control control in Parent.Controls) { var other=control as DialogChoice; if(other!=null && other!=this && other.Visible && other.Enabled) { other.Checked=true; other.Focus(); break; } }
                e.SuppressKeyPress=true;
            }
            base.OnKeyDown(e);
        }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ChoiceAccessible(this); }
        class ChoiceAccessible : ControlAccessibleObject
        {
            readonly DialogChoice choice;
            public ChoiceAccessible(DialogChoice choice) : base(choice) { this.choice=choice; }
            public override AccessibleStates State { get { return base.State | (choice.Checked ? AccessibleStates.Checked : 0); } }
            public override string DefaultAction { get { return "选择"; } }
            public override void DoDefaultAction() { if(choice.InvokeRequired) choice.BeginInvoke((Action)choice.PerformClick); else choice.PerformClick(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics; g.Clear(Parent.BackColor); g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=Shapes.Round(new RectangleF(.5F,.5F,Width-1,Height-1),5*g.DpiX/96F))
            { using(var brush=new SolidBrush(Checked ? Color.White : Parent.BackColor)) g.FillPath(brush,path); if(Checked) using(var pen=new Pen(Color.FromArgb(218,218,218))) g.DrawPath(pen,path); }
            TextRenderer.DrawText(g,Text,Font,ClientRectangle,Checked ? Color.FromArgb(0,95,184) : Color.FromArgb(99,99,99),TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g,new Rectangle(4,4,Width-8,Height-8));
        }
    }
    class SettingsDialog : ModernDialog
    {
        public readonly ToggleSwitch StartupToggle = new ToggleSwitch();
        public readonly DialogButton TestNotificationButton = new DialogButton("发送测试通知");
        bool updating;
        public SettingsDialog(StartupService startup,Action testNotification=null) : base("设置",460,624)
        {
            int cw=(int)Math.Round(ContentWidth/UiScale);
            var startupCard=Card(0,96); LabelAt(startupCard,"开机启动",16,12,cw-150,26,10,true);
            var state=LabelAt(startupCard,"",cw-120,14,48,24,8.5F,false,Muted); Place(startupCard,StartupToggle,cw-64,15,46,24); StartupToggle.Checked=startup.Enabled;
            LabelAt(startupCard,"登录 Windows 后自动显示桌面待办",16,44,cw-32,23,9,false,Muted);
            var error=LabelAt(startupCard,"",16,69,cw-32,20,8,false,Color.Firebrick);
            Action refresh=delegate { state.Text=StartupToggle.Checked ? "已开启" : "已关闭"; state.ForeColor=StartupToggle.Checked ? Accent : Muted; };
            refresh(); StartupToggle.CheckedChanged+=delegate
            {
                if(updating) return;
                try { startup.SetEnabled(StartupToggle.Checked); if(startup.Enabled!=StartupToggle.Checked) throw new IOException("启动项未保存。"); error.Text=""; refresh(); }
                catch(Exception ex) { updating=true; StartupToggle.Checked=startup.Enabled; updating=false; refresh(); error.Text=ex.Message; }
            };
            var notifications=Card(108,182); LabelAt(notifications,"通知",16,12,cw-32,26,10,true);
            LabelAt(notifications,"使用 Windows 系统通知，需保持程序运行或留在托盘。\n勿扰模式可能隐藏通知横幅。",16,42,cw-32,44,8.5F,false,Muted);
            Place(notifications,TestNotificationButton,16,94,152,34); TestNotificationButton.Enabled=testNotification!=null;
            var status=LabelAt(notifications,"",16,136,cw-32,38,8.5F,false,Muted); status.AccessibleName="测试通知结果";
            TestNotificationButton.Click+=delegate { try { testNotification(); status.ForeColor=Accent; status.Text="已提交给 Windows，请查看通知中心。"; } catch(Exception ex) { status.ForeColor=Color.Firebrick; status.Text=ex.Message; } };
            var keys=Card(302,190); LabelAt(keys,"快捷键",16,12,cw-32,26,10,true);
            string[] shortcuts={"Enter","Ctrl + N","Ctrl + Z","Esc","Delete"}, descriptions={"添加任务或步骤","返回列表并输入新任务","撤销操作（不含永久删除）","返回列表 / 取消弹窗","任务菜单内：请求删除"};
            for(int i=0;i<shortcuts.Length;i++) { int y=45+i*27; var keycap=new DialogCard {BackColor=Color.FromArgb(246,246,246)}; Place(keys,keycap,16,y,78,23); var key=LabelAt(keycap,shortcuts[i],0,0,78,23,8.5F); key.TextAlign=ContentAlignment.MiddleCenter; LabelAt(keys,descriptions[i],108,y,cw-124,23,8.5F,false,Muted); }
            Body.AutoScrollMinSize=new Size(0,D(508));
            LabelAt(Footer,"桌面待办 2.0.5 · 本地保存",24,26,250,24,8.5F,false,Muted);
            var done=new DialogButton("完成",DialogButtonKind.Primary) {DialogResult=DialogResult.OK}; Place(Footer,done,(int)(ClientSize.Width/UiScale)-114,20,90,36); AcceptButton=CancelButton=done;
        }
    }
    class ScheduleDialog : ModernDialog
    {
        public DateTimeOffset? Selected { get; private set; }
        public bool Cleared { get; private set; }
        public bool UsesCountdown { get; private set; }
        public DateTimeOffset CountdownStartedAt { get; private set; }
        public int DurationAmount { get { return duration.Value; } }
        public bool DurationHours { get { return unit.SelectedIndex==1; } }
        public readonly DialogChoice CountdownChoice = new DialogChoice("倒计时");
        public readonly DialogButton ConfirmButton = new DialogButton("确定",DialogButtonKind.Primary);
        readonly DialogChoice absolute=new DialogChoice("日期和时间");
        readonly DialogDateField date;
        readonly DialogTextField time;
        readonly DialogNumberField duration;
        readonly DialogUnitField unit;
        public readonly Label ErrorMessage;
        public void SelectCountdown(int amount,bool hours) { unit.SelectedIndex=hours ? 1 : 0; duration.Value=amount; CountdownChoice.Checked=true; }
        public void SelectAbsolute(DateTime value) { absolute.Checked=true; date.Value=value.Date; time.Editor.Text=value.ToString("HH:mm"); }
        public ScheduleDialog(bool reminder,string current,int? currentCountdownMinutes=null) : base(reminder ? "提醒我" : "截止日期",460,reminder ? 500 : 320)
        {
            int cw=(int)Math.Round(ContentWidth/UiScale),windowWidth=(int)Math.Round(ClientSize.Width/UiScale);
            var modes=new Panel {BackColor=Color.FromArgb(231,231,231),Visible=reminder}; Place(Body,modes,24,0,cw,40);
            Place(modes,absolute,3,3,(cw-8)/2,34); Place(modes,CountdownChoice,(cw-8)/2+5,3,(cw-8)/2,34); absolute.Checked=true;
            var absoluteCard=Card(reminder ? 52 : 0,reminder ? 208 : 116);
            LabelAt(absoluteCard,reminder ? "指定日期和时间" : "选择截止日期",16,12,cw-32,28,10,true);
            date=new DialogDateField(UiScale); date.AccessibleName=reminder ? "提醒日期" : "截止日期";
            time=new DialogTextField(UiScale); time.Editor.MaxLength=5;
            if(reminder)
            {
                LabelAt(absoluteCard,"在指定时刻发送系统通知",16,43,cw-32,23,8.5F,false,Muted);
                LabelAt(absoluteCard,"日期",16,80,cw-160,22,8.5F,false,Muted); LabelAt(absoluteCard,"时间",cw-130,80,114,22,8.5F,false,Muted);
                Place(absoluteCard,date,16,108,cw-162,40); Place(absoluteCard,time,cw-130,108,114,40);
                LabelAt(absoluteCard,"时间格式为 24 小时制，例如 18:30",16,164,cw-32,24,8.5F,false,Muted);
            }
            else { Place(absoluteCard,date,16,56,cw-32,40); Place(absoluteCard,time,0,0,1,1); time.Visible=false; }
            var countdownCard=Card(52,208); countdownCard.Visible=false;
            LabelAt(countdownCard,"倒计时时长",16,12,cw-32,28,10,true);
            LabelAt(countdownCard,"从点击“开始倒计时”的时刻起算",16,43,cw-32,23,8.5F,false,Muted);
            duration=new DialogNumberField(UiScale); duration.Value=25; unit=new DialogUnitField(UiScale);
            Place(countdownCard,duration,16,80,cw-170,40); Place(countdownCard,unit,cw-138,80,122,40);
            int[] presets={5,15,30,60}; string[] captions={"5 分钟","15 分钟","30 分钟","1 小时"}; int presetWidth=(cw-56)/4;
            for(int i=0;i<4;i++) { int minutes=presets[i]; var preset=new DialogButton(captions[i]); Place(countdownCard,preset,16+i*(presetWidth+8),136,presetWidth,34); preset.Font=F(8.5F); preset.Click+=delegate { SelectCountdown(minutes==60 ? 1 : minutes,minutes==60); }; }
            LabelAt(countdownCard,"支持 1 分钟至 30 天",16,178,cw-32,22,8.5F,false,Muted);
            var hint=LabelAt(Body,"",24,276,cw,36,8.5F,false,Muted); hint.Visible=reminder;
            ErrorMessage=LabelAt(Body,"",24,reminder ? 316 : 128,cw,36,8.5F,false,Color.Firebrick);
            date.ValueChanged+=delegate { ErrorMessage.Text=""; }; time.Editor.TextChanged+=delegate { ErrorMessage.Text=""; }; duration.Editor.TextChanged+=delegate { ErrorMessage.Text=""; };
            var clear=new DialogButton(reminder ? "清除提醒" : "清除日期",DialogButtonKind.Quiet); Place(Footer,clear,24,20,94,36);
            clear.Click+=delegate { Cleared=true; Selected=null; DialogResult=DialogResult.OK; Close(); };
            var cancel=new DialogButton("取消") {DialogResult=DialogResult.Cancel}; Place(Footer,cancel,windowWidth-240,20,90,36);
            Place(Footer,ConfirmButton,windowWidth-138,20,114,36); AcceptButton=ConfirmButton; CancelButton=cancel;
            Action updateMode=delegate { bool countdown=reminder && CountdownChoice.Checked; absoluteCard.Visible=!countdown; countdownCard.Visible=countdown; ConfirmButton.Text=countdown ? "开始倒计时" : "确定"; hint.Text=countdown ? "再次确认会重新开始倒计时。" : "提醒需要程序运行或留在托盘。"; ErrorMessage.Text=""; };
            absolute.CheckedChanged+=delegate { updateMode(); }; CountdownChoice.CheckedChanged+=delegate { updateMode(); };
            unit.SelectedIndexChanged+=delegate { duration.Maximum=unit.SelectedIndex==1 ? 720 : 43200; };
            ConfirmButton.Click+=delegate
            {
                if(reminder && CountdownChoice.Checked)
                { try { CountdownStartedAt=DateTimeOffset.Now; Selected=TaskService.CountdownTarget(CountdownStartedAt,DurationAmount,DurationHours); UsesCountdown=true; DialogResult=DialogResult.OK; Close(); } catch(Exception ex) { ErrorMessage.Text=ex.Message; } return; }
                DateTime parsed=DateTime.Today;
                if(reminder && !DateTime.TryParseExact(time.Editor.Text.Trim(),new[]{"H:mm","HH:mm"},CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)) { ErrorMessage.Text="请输入有效的时间，例如 18:30。"; time.Editor.Focus(); return; }
                else if(!reminder) parsed=DateTime.Today;
                DateTime local=date.Value.Date.Add(reminder ? parsed.TimeOfDay : TimeSpan.Zero);
                local=new DateTime(local.Year,local.Month,local.Day,local.Hour,local.Minute,0,DateTimeKind.Local);
                if(reminder && local<=DateTime.Now) { ErrorMessage.Text="请选择未来的提醒时间。"; return; }
                Selected=new DateTimeOffset(local); DialogResult=DialogResult.OK; Close();
            };
            DateTime value=DateTime.Now.AddHours(1); DateTimeOffset existing;
            if(current!=null && DateTimeOffset.TryParse(current,out existing)) value=existing.LocalDateTime;
            SelectAbsolute(value);
            if(reminder && currentCountdownMinutes.HasValue) SelectCountdown(currentCountdownMinutes.Value%60==0 ? currentCountdownMinutes.Value/60 : currentCountdownMinutes.Value,currentCountdownMinutes.Value%60==0);
            CountdownChoice.Visible=reminder; updateMode();
        }
    }
}

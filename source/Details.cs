using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DesktopTodo
{
    class FloatingDialog : Form
    {
        public FloatingDialog() { TopMost = true; ShowInTaskbar = false; }
        protected override void OnShown(EventArgs e)
        {
            TopMost = true; base.OnShown(e);
            if (Visible && !IsDisposed) Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
        }
    }
    class DetailCard : SmoothPanel
    {
        public Action<int> Arrange;
        public DetailCard() { FillColor = Color.White; Radius = 6; }
        public void SetWidth(int width) { Width = width; if (Arrange != null) Arrange(width); }
    }

    sealed partial class TodoForm
    {
        readonly SmoothPanel detailRoot = new SmoothPanel { FillColor = Color.FromArgb(245, 246, 250), Radius = 0 };
        readonly TaskListPanel detailList = new TaskListPanel();
        readonly SurfaceLabel detailHeading = new SurfaceLabel(), detailCreated = new SurfaceLabel(), detailMessage = new SurfaceLabel();
        readonly FlatButton detailBack = new FlatButton("←", "返回任务列表");
        readonly VerticalMoreButton detailMore = new VerticalMoreButton("任务菜单");
        readonly System.Windows.Forms.Timer detailSaveTimer = new System.Windows.Forms.Timer(), reminderTimer = new System.Windows.Forms.Timer();
        readonly List<Font> detailFonts = new List<Font>();
        readonly Dictionary<string,TaskInput> stepEditors = new Dictionary<string,TaskInput>();
        string detailId;
        WindowsReminderNotifications notifications;
        DateTimeOffset notificationRetryAfter;
        TextBox detailTitleInput, notesInput;
        TaskInput stepInput;
        bool buildingDetails, detailDirty;
        FlatButton activeReminderButton;

        DialogResult ShowUtilityDialog(Form dialog)
        {
            utilityDialogDepth++; pinTimer.Stop(); dialog.TopMost = true;
            try { return dialog.ShowDialog(this); }
            finally { utilityDialogDepth--; if (utilityDialogDepth == 0 && !IsDisposed) { TopMost = false; pinTimer.Start(); Pin(); } }
        }

        void BuildDetails()
        {
            Controls.Add(detailRoot); detailRoot.Visible = false; detailRoot.BringToFront();
            detailRoot.Controls.AddRange(new Control[] { detailList, detailBack, detailHeading, detailCreated, detailMore, detailMessage });
            detailHeading.Text = "任务详情"; detailHeading.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            foreach (var label in new[] { detailHeading, detailCreated, detailMessage }) { label.BackColor = Color.Transparent; label.ForeColor = colors.Text; label.TextAlign = ContentAlignment.MiddleLeft; }
            detailCreated.Font = new Font("Microsoft YaHei UI", 8F); detailCreated.ForeColor = colors.Muted; detailCreated.AutoEllipsis = true;
            detailMessage.Font = new Font("Microsoft YaHei UI", 8F); detailMessage.ForeColor = Color.Firebrick;
            detailBack.BackColor = detailMore.BackColor = Color.Transparent; detailBack.ForeColor = colors.Accent; detailMore.ForeColor = colors.Muted;
            detailBack.Font = new Font("Segoe UI", 18F); detailBack.Click += delegate { CloseDetail(); };
            detailHeading.MouseDown += DragHeader;
            detailMore.Click += delegate { if (detailId != null) TaskMenu(detailId).Show(detailMore, new Point(0, detailMore.Height)); };
            detailList.SizeItem = delegate(Control control, int width) { var card = control as DetailCard; if (card != null) card.SetWidth(width); else control.Width = width; };
            detailSaveTimer.Interval = 700; detailSaveTimer.Tick += delegate { detailSaveTimer.Stop(); if (stepEditors.Values.Any(e => e.IsComposing)) detailSaveTimer.Start(); else SaveDetailDraft(false); };
            FormClosed += delegate { foreach (var f in detailFonts) f.Dispose(); };
        }
        void LayoutDetails()
        {
            if (detailRoot == null || detailRoot.Parent == null) return;
            int w = ClientSize.Width, h = ClientSize.Height, pad = D(15);
            detailRoot.SetBounds(0, 0, w, h); detailBack.SetBounds(D(12), D(13), D(35), D(35));
            detailHeading.SetBounds(D(54), D(16), w - D(110), D(31)); detailMore.SetBounds(w - D(48), D(14), D(32), D(32)); detailMessage.SetBounds(pad, D(49), w - 2 * pad, D(18));
            detailList.SetBounds(pad, D(72), w - 2 * pad, Math.Max(D(80), h - D(125)));
            detailCreated.SetBounds(pad, h - D(42), w - 2 * pad, D(28));
            detailList.LayoutItems();
        }
        static bool IsEditingText(Control control)
        { if (control is TextBoxBase && control.Focused) return true; return control.Controls.Cast<Control>().Any(IsEditingText); }
        string TaskMetadata(Todo task)
        {
            var parts = new List<string>();
            if (task.Steps.Count > 0) parts.Add(task.Steps.Count(s => s.Done) + "/" + task.Steps.Count + " 步");
            if (task.DueDate != null) parts.Add("截止 " + DateTime.ParseExact(task.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("M月d日"));
            if (task.ReminderUtc != null) parts.Add(task.ReminderDeliveredUtc != null ? "已提醒" : task.ReminderCountdownMinutes.HasValue ? "倒计时提醒" : "已设提醒");
            if (!string.IsNullOrWhiteSpace(task.Notes)) parts.Add("有备注");
            return string.Join("  ·  ", parts);
        }
        void OpenDetail(string id)
        {
            if (!SaveDetailDraft()) return;
            string targetList=service.State.Tasks.Single(t => t.Id==id).ListId;
            if(service.State.SelectedListId!=targetList) service.SelectList(targetList);
            detailId = id;
            foreach (var control in new Control[] { header, list, inputArea, footer }) control.Visible = false;
            detailRoot.Visible = true; detailRoot.BringToFront(); RefreshDetails(); LayoutDetails();
        }
        bool CloseDetail()
        {
            if (!SaveDetailDraft()) return false;
            detailSaveTimer.Stop(); detailRoot.Visible = false; detailId = null; detailDirty = false;
            foreach (var control in new Control[] { header, list, inputArea, footer }) control.Visible = true;
            RenderTasks(); Invalidate(true); return true;
        }
        bool SaveDetailDraft(bool showError = true, string removeStepId = null)
        {
            if (buildingDetails || (!detailDirty && removeStepId == null) || detailId == null || detailTitleInput == null || notesInput == null) return true;
            if (stepEditors.Values.Any(e => e.IsComposing)) { detailSaveTimer.Start(); return false; }
            detailSaveTimer.Stop();
            try
            {
                service.SaveDetailText(detailId, detailTitleInput.Text, notesInput.Text, stepEditors.ToDictionary(p => p.Key,p => p.Value.Text), removeStepId); detailDirty = false; detailMessage.Text = ""; RenderTasks(); return true;
            }
            catch (Exception ex) { detailMessage.Text = ex.Message; if (showError) MessageBox.Show(ex.Message, "尚未保存"); return false; }
        }
        void DraftChanged()
        { if (buildingDetails) return; detailDirty = true; detailSaveTimer.Stop(); detailSaveTimer.Start(); }
        void DetailChange(Action action)
        {
            if (!SaveDetailDraft()) return;
            try { action(); RenderTasks(); RefreshDetails(); }
            catch (Exception ex) { detailMessage.Text = ex.Message; }
        }
        void RefreshDetails()
        {
            if (detailId == null) return;
            var task = service.State.Tasks.FirstOrDefault(t => t.Id == detailId);
            if (task == null) { detailRoot.Visible = false; detailId = null; return; }
            buildingDetails = true;
            detailList.BeginUpdate();
            try
            {
                detailTitleInput = null; notesInput = null; stepInput = null; activeReminderButton = null;
                stepEditors.Clear();
                detailList.ClearItems(); foreach (var font in detailFonts) font.Dispose(); detailFonts.Clear();
                detailList.AddItem(BuildTaskDetail(task)); detailList.AddItem(BuildTaskListCard(task)); detailList.AddItem(BuildScheduleCard(task)); detailList.AddItem(BuildNotesCard(task));
                DateTimeOffset created; detailCreated.Text = DateTimeOffset.TryParse(task.CreatedUtc, out created) ? "创建于 " + created.ToLocalTime().ToString("yyyy年M月d日H点mm分") : "创建时间未知";
                detailMessage.Text = ""; detailDirty = false; LayoutDetails();
            }
            finally { detailList.EndUpdate(); buildingDetails = false; detailList.LayoutItems(); }
        }
        Font DetailFont(float size, FontStyle style = FontStyle.Regular)
        { var font = new Font("Microsoft YaHei UI", size, style); detailFonts.Add(font); return font; }
        DetailCard BuildTaskDetail(Todo task)
        {
            string id = task.Id; var card = new DetailCard { Name = "task-detail" };
            var check = new CheckTaskButton(task.Done, false, "完成当前任务") { BackColor = Color.White, ForeColor = colors.Accent };
            check.Click += delegate { DetailChange(delegate { service.Toggle(id); }); };
            detailTitleInput = new TextBox { Text = task.Text, BorderStyle = BorderStyle.None, Multiline = true, WordWrap = true, MaxLength = 2000, BackColor = Color.White, ForeColor = colors.Text, Font = DetailFont(12F, FontStyle.Bold), AccessibleName = "任务标题" };
            detailTitleInput.TextChanged += delegate { DraftChanged(); detailList.LayoutItems(); };
            detailTitleInput.Leave += delegate { if (!buildingDetails) SaveDetailDraft(false); };
            var star = new TaskStarButton(task.Important) { BackColor = Color.White, ForeColor = colors.Accent, Name = "task-star" };
            star.Click += delegate { DetailChange(delegate { service.Change(s => { var t = s.Tasks.Single(x => x.Id == id); t.Important = !t.Important; }); }); };
            card.Controls.AddRange(new Control[] { check, detailTitleInput, star });
            var stepRows = new List<Panel>();
            foreach (var step in task.Steps)
            {
                string stepId = step.Id;
                var stepRow = new Panel { BackColor = Color.White, Tag = stepId };
                var toggle = new CheckTaskButton(step.Done, false, "完成步骤 " + step.Text) { BackColor = Color.White, ForeColor = colors.Accent, Name = "step-check" };
                toggle.Click += delegate { DetailChange(delegate { service.ToggleStep(id, stepId); }); };
                var normalFont = DetailFont(10F); var displayFont = step.Done ? DetailFont(10F,FontStyle.Strikeout) : normalFont;
                var text = new TaskInput { Cue = "", Text = step.Text, Multiline = true, WordWrap = true, AcceptsReturn = false, BorderStyle = BorderStyle.None, MaxLength = 2000, BackColor = Color.White, ForeColor = step.Done ? colors.Muted : colors.Text, Font = displayFont, Name = "step-text", AccessibleName = "步骤内容", AccessibleDescription = "直接编辑步骤文字，前方圆圈切换完成状态", Tag = stepId };
                stepEditors.Add(stepId,text);
                text.TextChanged += delegate { toggle.AccessibleName = "完成步骤 " + text.Text; DraftChanged(); detailList.LayoutItems(); };
                text.Enter += delegate { text.Font = normalFont; text.ForeColor = colors.Text; text.BackColor = Color.FromArgb(244,247,255); };
                text.Leave += delegate { text.Font = displayFont; text.ForeColor = step.Done ? colors.Muted : colors.Text; text.BackColor = Color.White; if (!buildingDetails) SaveDetailDraft(false); };
                text.KeyDown += delegate(object sender,KeyEventArgs e) { if(e.KeyCode == Keys.Enter && !text.IsComposing) { e.SuppressKeyPress = true; if(SaveDetailDraft(false)) detailBack.Focus(); } };
                var actions = new VerticalMoreButton("步骤菜单") { BackColor = Color.White, ForeColor = colors.Muted, Name = "step-more" };
                actions.Click += delegate
                {
                    StepMenu(stepId).Show(actions, new Point(0, actions.Height));
                };
                stepRow.Controls.AddRange(new Control[] { toggle, text, actions }); card.Controls.Add(stepRow); stepRows.Add(stepRow);
            }
            var addRow = new Panel { BackColor = Color.White };
            var plus = new FlatButton("＋", "添加步骤") { BackColor = Color.White, ForeColor = colors.Accent, Font = DetailFont(17F) };
            stepInput = new TaskInput { Cue = task.Steps.Count == 0 ? "添加步骤" : "下一步", BorderStyle = BorderStyle.None, MaxLength = 2000, Font = DetailFont(10F), BackColor = Color.White, ForeColor = colors.Accent, AccessibleName = "添加步骤内容" };
            Action addStep = delegate { if (stepInput == null) return; string value = stepInput.Text.Trim(); if (value.Length == 0) { stepInput.Focus(); return; } DetailChange(delegate { service.AddStep(id, value); }); if (stepInput != null) stepInput.Focus(); };
            plus.Click += delegate { addStep(); };
            stepInput.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && !stepInput.IsComposing) { e.SuppressKeyPress = true; addStep(); } };
            addRow.Controls.AddRange(new Control[] { plus, stepInput }); card.Controls.Add(addRow);
            var titleBox = detailTitleInput; var stepBox = stepInput;
            card.Arrange = delegate(int width)
            {
                int titleWidth = Math.Max(D(80), width - D(91));
                int th = Math.Max(D(32), TextRenderer.MeasureText(titleBox.Text.Length == 0 ? "任务" : titleBox.Text, titleBox.Font, new Size(titleWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height + D(4));
                check.SetBounds(D(5), D(12), D(35), D(35)); titleBox.SetBounds(D(46), D(17), titleWidth, th); star.SetBounds(width - D(38), D(12), D(30), D(34));
                int y = Math.Max(D(58), th + D(26));
                foreach (var row in stepRows)
                {
                    var text = (TextBox)row.Controls["step-text"]; int tw = Math.Max(D(50), width - D(98));
                    int textHeight = Math.Max(text.PreferredHeight, TextRenderer.MeasureText(text.Text.Length == 0 ? " " : text.Text, text.Font, new Size(tw, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height + D(3));
                    int rh = Math.Max(D(36),textHeight + D(8)); row.SetBounds(D(12), y, width - D(24), rh);
                    row.Controls["step-check"].SetBounds(0, (rh - D(28)) / 2, D(28), D(28));
                    text.SetBounds(D(35), (rh - textHeight) / 2, tw, textHeight); row.Controls["step-more"].SetBounds(star.Left - row.Left, (rh - D(28)) / 2, star.Width, D(28)); y += rh + D(2);
                }
                addRow.SetBounds(D(12), y, width - D(24), D(41)); plus.SetBounds(0, D(3), D(28), D(33));
                stepBox.SetBounds(D(35), (addRow.Height - stepBox.PreferredHeight) / 2, addRow.Width - D(44), stepBox.PreferredHeight); card.Height = y + D(49);
            };
            return card;
        }
        ModernMenu StepMenu(string stepId)
        {
            var m = NewMenu();
            m.AddCommand("删除步骤",MenuGlyph.Delete,delegate { if(SaveDetailDraft(false,stepId)) RefreshDetails(); },null,true);
            return m;
        }
        DetailCard BuildScheduleCard(Todo task)
        {
            string id = task.Id; var card = new DetailCard { Name = "schedule-detail" };
            string reminderText = ReminderCaption(task);
            string dueText = task.DueDate == null ? "▦　添加截止日期" : "▦　截止日期\n　　" + DateTime.ParseExact(task.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("yyyy年M月d日");
            var remind = new FlatButton(reminderText, "设置提醒") { AlignLeft = true, BackColor = Color.White, ForeColor = task.ReminderUtc == null ? colors.Muted : colors.Accent, Font = DetailFont(10F) };
            activeReminderButton = remind;
            var due = new FlatButton(dueText, "设置截止日期") { AlignLeft = true, BackColor = Color.White, ForeColor = task.DueDate == null ? colors.Muted : colors.Accent, Font = DetailFont(10F) };
            var line = new Panel { BackColor = colors.Line };
            remind.Click += delegate { if (!SaveDetailDraft()) return; var current = service.State.Tasks.Single(t => t.Id == id); using (var dialog = new ScheduleDialog(true, current.ReminderUtc, current.ReminderCountdownMinutes)) if (ShowUtilityDialog(dialog) == DialogResult.OK) DetailChange(delegate { if (dialog.UsesCountdown) service.SetCountdown(id, dialog.DurationAmount, dialog.DurationHours, dialog.CountdownStartedAt); else service.SetReminder(id, dialog.Selected); }); };
            due.Click += delegate { if (!SaveDetailDraft()) return; var current = service.State.Tasks.Single(t => t.Id == id); using (var dialog = new ScheduleDialog(false, current.DueDate)) if (ShowUtilityDialog(dialog) == DialogResult.OK) DetailChange(delegate { service.SetDueDate(id, dialog.Selected.HasValue ? (DateTime?)dialog.Selected.Value.LocalDateTime.Date : null); }); };
            card.Controls.AddRange(new Control[] { remind, line, due });
            card.Arrange = delegate(int width) { int rh = D(65); remind.SetBounds(D(8), D(6), width - D(16), rh); line.SetBounds(D(16), D(6) + rh, width - D(32), 1); due.SetBounds(D(8), D(8) + rh, width - D(16), rh); card.Height = 2 * rh + D(15); };
            return card;
        }
        DetailCard BuildNotesCard(Todo task)
        {
            var card = new DetailCard { Name = "notes-detail" };
            notesInput = new NoteInput { Text = task.Notes, Multiline = true, WordWrap = true, AcceptsReturn = true, ScrollBars = ScrollBars.None, BorderStyle = BorderStyle.None, MaxLength = 20000, BackColor = Color.White, ForeColor = colors.Text, Font = DetailFont(10F), AccessibleName = "任务备注", AccessibleDescription = "点击输入备注，支持多行并自动保存" };
            notesInput.TextChanged += delegate { DraftChanged(); detailList.LayoutItems(); }; notesInput.Leave += delegate { if (!buildingDetails) SaveDetailDraft(false); };
            card.Controls.Add(notesInput); var box = notesInput;
            card.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) box.Focus(); };
            card.Arrange = delegate(int width)
            {
                int textWidth = Math.Max(D(80), width - D(32));
                int contentHeight = TextRenderer.MeasureText(box.Text.Length == 0 ? " " : box.Text, box.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl).Height + D(12);
                int editHeight = Math.Max(D(120), Math.Min(D(600), contentHeight));
                box.ScrollBars = contentHeight > D(600) ? ScrollBars.Vertical : ScrollBars.None;
                box.SetBounds(D(16), D(16), textWidth, editHeight); card.Height = D(32) + editHeight;
            };
            return card;
        }
        void ConfigureReminders()
        {
            reminderTimer.Interval = 1000; reminderTimer.Tick += delegate { PollReminders(); UpdateCountdownCaption(); };
            if (diagnosticPath == null && uiTestDirectory == null) reminderTimer.Start();
        }
        void InitializeNotifications()
        {
            if (notifications != null) return;
            try
            {
                notifications = new WindowsReminderNotifications(Application.ExecutablePath, store.DirectoryPath, OnNotificationActivated, OnNotificationFailed);
            }
            catch (Exception ex) { NotificationError(ex.Message); }
        }
        void NotificationError(string error)
        {
            notificationRetryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
            message = "系统通知未发送：" + error; RenderTasks(); detailMessage.Text = message;
        }
        void OnNotificationActivated(string argument)
        {
            QueueNotificationAction(delegate
            {
                if (IsDisposed) return;
                string id = NotificationPayload.TaskId(argument);
                ShowOnDesktop();
                if (id != null && service.State.Tasks.Any(t => t.Id == id)) OpenDetail(id);
            });
        }
        void OnNotificationFailed(string id, string scheduled, string error)
        {
            QueueNotificationAction(delegate
            {
                if (IsDisposed) return;
                try { ReminderDelivery.Requeue(service, id, scheduled); NotificationError(error); }
                catch (Exception ex) { NotificationError(ex.Message); }
            });
        }
        void QueueNotificationAction(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(action); } catch (InvalidOperationException) { /* The form is shutting down. */ }
        }
        void SendTestNotification()
        {
            if (diagnosticPath != null || uiTestDirectory != null) throw new InvalidOperationException("测试模式不会发送真实系统通知。");
            InitializeNotifications();
            if (notifications == null) throw new InvalidOperationException("系统通知初始化失败，请查看清单底部提示。");
            notifications.SendTest();
        }
        string ReminderCaption(Todo task)
        {
            if (task.ReminderUtc == null) return "◷　提醒我";
            DateTimeOffset target = DateTimeOffset.Parse(task.ReminderUtc);
            if (!task.ReminderCountdownMinutes.HasValue) return "◷　提醒我\n　　" + target.ToLocalTime().ToString("yyyy年M月d日 HH:mm");
            string status;
            if (task.Done || task.Deleted) status = task.Done ? "已完成，不发送提醒" : "已删除，不发送提醒";
            else if (task.ReminderDeliveredUtc != null) status = "已提醒";
            else
            {
                var remaining = target - DateTimeOffset.UtcNow;
                if (remaining.TotalSeconds <= 0) status = "等待发送提醒";
                else if (remaining.TotalSeconds < 60) status = "剩余 " + Math.Ceiling(remaining.TotalSeconds) + " 秒";
                else { int minutes = (int)Math.Ceiling(remaining.TotalMinutes); status = minutes >= 60 ? "剩余 " + minutes / 60 + " 小时 " + minutes % 60 + " 分钟" : "剩余 " + minutes + " 分钟"; }
            }
            return "◷　倒计时提醒\n　　" + status + "（" + target.ToLocalTime().ToString("HH:mm") + "）";
        }
        void UpdateCountdownCaption()
        {
            if (!detailRoot.Visible || detailId == null || activeReminderButton == null || activeReminderButton.IsDisposed) return;
            var task = service.State.Tasks.FirstOrDefault(t => t.Id == detailId);
            if (task != null && task.ReminderCountdownMinutes.HasValue) activeReminderButton.Text = ReminderCaption(task);
        }
        void PollReminders()
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now < notificationRetryAfter || service.DueReminders(now).Count == 0) return;
                InitializeNotifications();
                if (notifications == null) return;
                int sent = ReminderDelivery.Dispatch(service, notifications, now);
                if (sent > 0) { message = "已向 Windows 提交 " + sent + " 项提醒"; RenderTasks(); UpdateCountdownCaption(); }
            }
            catch (Exception ex) { NotificationError(ex.Message); }
        }
        void RunRenderTests()
        {
            var results = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception(name); results.Add("PASS: " + name); };
            try
            {
                Directory.CreateDirectory(uiTestDirectory); service.Settings(s => s.Tasks.Clear());
                Size = new Size(D(380), D(600)); RenderTasks();
                check(menu.Left < ClientSize.Width / 2 && menu is HamburgerButton, "hamburger menu is at the upper left");
                check(Opacity == 1 && undoButton.Parent == null, "opaque window and no bottom-right undo button");
                SavePreview(Path.Combine(uiTestDirectory, "empty.png"));
                input.Text = "准备下周的项目会议"; add.PerformClick(); string id = service.State.Tasks.Last().Id;
                check(service.State.Tasks.Last().Text == "准备下周的项目会议", "add task handler saves input");
                var row = list.Items.Single(c => c.Tag is Todo);
                check(!list.HasOverflow && inputArea.Left == list.Left + row.Bounds.Left && inputArea.Right == list.Left + row.Right, "single-task card and add banner have identical horizontal edges");
                int shortListCardWidth = row.Bounds.Width;
                for (int i = 0; i < 15; i++) service.Add("宽度回归检查 " + i); RenderTasks();
                check(list.HasOverflow && list.Items.All(r => r.Bounds.Width == shortListCardWidth) && inputArea.Width == shortListCardWidth, "overflow does not change card or add banner width");
                service.Settings(s => s.Tasks.RemoveAll(t => t.Id != id)); RenderTasks(); row = list.Items.Single();
                check(Math.Abs(row.TitleBounds.Top + row.TitleBounds.Height / 2 - (row.Bounds.Top + row.Height / 2)) <= 1, "single-line title is vertically centered in its card");
                check(Math.Abs(input.Top + input.Height / 2 - inputArea.Height / 2) <= 1, "new-task input is vertically centered");
                service.AddStep(id, "整理上周进度"); service.AddStep(id, "确认讨论议题"); service.ToggleStep(id, service.State.Tasks[0].Steps[0].Id);
                service.SaveText(id, "准备下周的项目会议", "本次会议重点：\r\n1. 回顾当前项目进度\r\n2. 确认下一阶段负责人\r\n\r\n会议资料提前一天发出。");
                service.SetReminder(id, DateTimeOffset.Now.AddDays(1).Date.AddHours(9)); service.SetDueDate(id, DateTime.Today.AddDays(2));
                service.Change(s => s.Tasks[0].CreatedUtc = new DateTimeOffset(2026, 10, 29, 14, 45, 0, TimeSpan.FromHours(8)).ToUniversalTime().ToString("o"));
                OpenDetail(id); check(detailRoot.Visible && detailId == id, "task opens in an in-window detail page");
                check(!header.Visible && !list.Visible && !inputArea.Visible, "main page is fully hidden behind detail page");
                check(stepInput != null && notesInput != null && detailTitleInput != null, "detail provides steps, notes and title editing");
                check(detailCreated.Text.Contains("2026年10月29日"), "detail footer shows absolute creation date");
                check(!DialogControls(detailRoot).Any(c => c is PinButton) && geometryPin.Parent==header,"task details has no pin button while the main page retains it");
                string inlineStepId=service.State.Tasks[0].Steps[0].Id; bool wasStepDone=service.State.Tasks[0].Steps[0].Done;
                var inlineEditor=stepEditors[inlineStepId]; inlineEditor.Focus();
                Native.SendMessage(inlineEditor.Handle,0x201,new IntPtr(1),new IntPtr(0x00040004)); Native.SendMessage(inlineEditor.Handle,0x202,IntPtr.Zero,new IntPtr(0x00040004));
                check(inlineEditor.Focused && service.State.Tasks[0].Steps[0].Done==wasStepDone,"clicking step text focuses the editor without toggling completion");
                inlineEditor.SelectAll(); inlineEditor.SelectedText="整理上周实验记录"; inlineEditor.SelectionStart=4;
                check(SaveDetailDraft(false),"inline step text saves through the shared detail draft");
                check(new TaskService(store).State.Tasks[0].Steps[0].Text=="整理上周实验记录" && service.State.Tasks[0].Steps[0].Done==wasStepDone,"inline rename persists while preserving completion state");
                check(stepEditors[inlineStepId]==inlineEditor && inlineEditor.Focused && inlineEditor.SelectionStart==4,"saving inline text preserves the editor and caret position");
                SavePreview(Path.Combine(uiTestDirectory,"inline-edit.png"));
                inlineEditor.Text="中文输入完成后的步骤";
                Native.SendMessage(inlineEditor.Handle,0x10D,IntPtr.Zero,IntPtr.Zero);
                check(inlineEditor.IsComposing && !SaveDetailDraft(false) && new TaskService(store).State.Tasks[0].Steps[0].Text=="整理上周实验记录","step drafts wait for IME composition to finish before saving");
                Native.SendMessage(inlineEditor.Handle,0x10E,IntPtr.Zero,IntPtr.Zero);
                Native.SendMessage(inlineEditor.Handle,0x100,new IntPtr((int)Keys.Enter),IntPtr.Zero);
                check(service.State.Tasks[0].Steps[0].Text=="中文输入完成后的步骤" && !inlineEditor.Focused && service.State.Tasks[0].Steps[0].Done==wasStepDone,"Enter finishes inline editing without completing the step");
                var inlineRow=detailList.Items.Single(c => c.Name=="task-detail").Controls.OfType<Panel>().Single(p => (string)p.Tag==inlineStepId);
                ((FlatButton)inlineRow.Controls["step-check"]).PerformClick();
                check(service.State.Tasks[0].Steps[0].Done!=wasStepDone && service.State.Tasks[0].Steps[0].Text=="中文输入完成后的步骤","only the step circle changes completion and retains the edited text");
                inlineRow=detailList.Items.Single(c => c.Name=="task-detail").Controls.OfType<Panel>().Single(p => (string)p.Tag==inlineStepId);
                ((FlatButton)inlineRow.Controls["step-check"]).PerformClick();
                check(DialogControls(detailRoot).Count(c => c is VerticalMoreButton)==service.State.Tasks[0].Steps.Count+1,"steps and task details each have a vertical three-dot menu");
                stepEditors[inlineStepId].Clear();
                check(!SaveDetailDraft(false) && stepEditors[inlineStepId].Text=="" && service.State.Tasks[0].Steps[0].Text=="中文输入完成后的步骤","empty step drafts remain editable and do not erase saved content");
                using(var stepMenu=StepMenu(inlineStepId))
                {
                    check(stepMenu.Items.Count==1 && stepMenu.Items[0].Text=="删除步骤","step menu contains deletion and no legacy edit action");
                    stepMenu.Items[0].PerformClick();
                }
                check(!service.State.Tasks[0].Steps.Any(s => s.Id==inlineStepId),"step menu can delete an empty unsaved step draft");
                service.Undo(); RefreshDetails();
                check(stepEditors.ContainsKey(inlineStepId) && stepEditors[inlineStepId].Text=="中文输入完成后的步骤","undo restores a deleted inline step with its saved text");
                SavePreview(Path.Combine(uiTestDirectory, "detail.png"));
                detailList.ScrollBy(100000); SavePreview(Path.Combine(uiTestDirectory, "notes.png"));
                check(!detailList.HasOverflow || detailList.Items.All(c => c.Right < detailList.RailLeft), "detail scroll rail never overlaps cards");
                notesInput.Text += "\r\n补充：带上纸质记录。"; detailTitleInput.Text = "准备项目会议"; check(SaveDetailDraft(false), "detail draft saves title and notes");
                check(new TaskService(store).State.Tasks[0].Notes.Contains("纸质记录"), "notes persist before returning to list");
                CloseDetail(); check(!detailRoot.Visible, "back returns to task list");
                service.Add("寄送样品"); service.Add("核对本月账单"); service.Add("整理工作资料"); service.Add("预约设备检查"); service.Add("完成会议纪要"); service.Toggle(service.State.Tasks.Last().Id);
                RenderTasks(); SavePreview(Path.Combine(uiTestDirectory, "list.png"));
                for (int i = 0; i < 20; i++) service.Add("测试清单 " + i);
                foreach (var size in new[] { new Size(320, 390), new Size(380, 560), new Size(506, 588) })
                {
                    Size = new Size(D(size.Width), D(size.Height)); LayoutUi(); RenderTasks();
                    check(list.HasOverflow && list.Items.All(c => c.Right < list.RailLeft), "scroll rail sits outside cards at " + size);
                    check(inputArea.Top > list.Bottom, "bottom input remains outside list at " + size);
                    check(list.Items.All(r => inputArea.Left == list.Left + r.Bounds.Left && inputArea.Right == list.Left + r.Right), "add banner aligns with every overflowing task card at " + size);
                }
                SavePreview(Path.Combine(uiTestDirectory, "scroll.png"));
                check(list.Controls.Count == 0, "long task list uses one painted surface with no child windows");
                int layoutPasses = list.LayoutPasses;
                for (int i = 0; i < 60; i++) list.ScrollBy(i % 2 == 0 ? D(32) : -D(32));
                check(list.LayoutPasses == layoutPasses, "scrolling changes an offset without relaying out task rows");
                list.ScrollBy(100000); check(list.Items.Last().Bottom - list.ScrollOffset <= list.Height, "last row remains reachable after scrolling");
                list.ScrollBy(-D(60)); var anchor = list.Items.First(r => r.Bottom > list.ScrollOffset); int anchorY = anchor.Bounds.Top - list.ScrollOffset;
                service.Add("新增后保持浏览位置"); RenderTasks();
                check(list.Items.Single(r => ((Todo)r.Tag).Id == ((Todo)anchor.Tag).Id).Bounds.Top - list.ScrollOffset == anchorY, "adding a task preserves the current viewport anchor");
                string firstTask = ((Todo)list.Items.First().Tag).Id; service.Toggle(firstTask); RenderTasks();
                check(list.Items.Single(r => ((Todo)r.Tag).Id == ((Todo)anchor.Tag).Id).Bounds.Top - list.ScrollOffset == anchorY, "completing an earlier task preserves the current viewport anchor");
                using (var firstFrame = list.RenderScene(Color.Magenta)) using (var secondFrame = list.RenderScene(Color.Lime))
                    check(BitmapSignature(firstFrame) == BitmapSignature(secondFrame), "entire task canvas overwrites old frame pixels");
                int savedOffset = list.ScrollOffset; string savedFrame;
                using (var frame = list.RenderScene(Color.Magenta)) savedFrame = BitmapSignature(frame);
                list.ScrollBy(-D(70)); list.ScrollBy(D(70));
                using (var restoredFrame = list.RenderScene(Color.Lime)) check(list.ScrollOffset == savedOffset && BitmapSignature(restoredFrame) == savedFrame, "scrolling away and back restores an identical complete frame");
                Rectangle originalBounds = Bounds; geometryPin.PerformClick();
                check(service.State.GeometryLocked && geometryPin.IsPinned && !DialogControls(detailRoot).Any(c => c is PinButton), "pin locks geometry from the main page only");
                SetBounds(Left + 40, Top + 40, Width + D(30), Height + D(30));
                check(Bounds == originalBounds, "locked window rejects bounds changes");
                Native.SetWindowPos(Handle, IntPtr.Zero, Left + 25, Top + 25, Width + 25, Height + 25, 0x0014);
                check(Bounds == originalBounds, "locked window rejects native move and resize requests");
                check(new TaskService(store).State.GeometryLocked, "fixed state persists to disk");
                SavePreview(Path.Combine(uiTestDirectory, "pinned.png")); geometryPin.PerformClick();
                SetBounds(Left, Top, Width - D(20), Height); check(Width == originalBounds.Width - D(20), "unpin permits resizing again"); Bounds = originalBounds;
                using (var pointerTest = new TaskCanvas { Size = new Size(D(320), D(180)) })
                {
                    var task = new Todo { Id = "routing", Text = "鼠标路由检查" }; pointerTest.SetTasks(new[] { task }, t => "", "", true);
                    int primary = 0, context = 0; TaskPart lastPart = TaskPart.Body;
                    pointerTest.PrimaryAction += delegate(object sender, TaskPointerEventArgs e) { primary++; lastPart = e.Part; };
                    pointerTest.ContextAction += delegate { context++; };
                    var routeRow = pointerTest.Items.First(); Point body = new Point(routeRow.TitleBounds.Left + 3, routeRow.TitleBounds.Top + 3);
                    pointerTest.DispatchPointer(body, MouseButtons.Right); check(primary == 0 && context == 1, "right click opens context action only");
                    pointerTest.DispatchPointer(body, MouseButtons.Left); check(primary == 1 && context == 1 && lastPart == TaskPart.Body, "left click opens details only");
                    pointerTest.DispatchPointer(new Point(routeRow.CheckBounds.Left + 3, routeRow.CheckBounds.Top + 3), MouseButtons.Right); check(primary == 1 && context == 2, "right click on completion control cannot complete or open details");
                    pointerTest.DispatchPointer(new Point(routeRow.CheckBounds.Left + 3, routeRow.CheckBounds.Top + 3), MouseButtons.Left); check(primary == 2 && context == 2 && lastPart == TaskPart.Check, "left completion click routes only to completion");
                    pointerTest.DispatchPointer(new Point(routeRow.StarBounds.Left + 3, routeRow.StarBounds.Top + 3), MouseButtons.Right); check(primary == 2 && context == 3, "right click on star cannot change importance");
                    pointerTest.DispatchPointer(body, MouseButtons.Middle); check(primary == 2 && context == 3, "middle click triggers neither route");
                }
                bool testNotificationRequested = false;
                var settingsStartup = new StartupService(new MemoryStartupStore(), Application.ExecutablePath);
                using (var prefs = new SettingsDialog(settingsStartup, delegate { testNotificationRequested = true; }))
                {
                    prefs.Show(); prefs.Refresh();
                    check(DialogControls(prefs).Any(c => c.Text == "快捷键"), "settings contains keyboard shortcuts");
                    check(!DialogControls(prefs).Any(c => c.Text.Contains("透明")), "settings has no opacity controls");
                    prefs.TestNotificationButton.PerformClick(); check(testNotificationRequested, "settings test notification invokes only the supplied transport");
                    prefs.StartupToggle.Checked = true; check(settingsStartup.Enabled, "modern startup toggle enables the injected startup service");
                    prefs.StartupToggle.Checked = false; check(!settingsStartup.Enabled, "modern startup toggle can disable startup again");
                    check(prefs is ModernDialog && prefs.FormBorderStyle == FormBorderStyle.None && prefs.Region != null, "settings uses the modern rounded dialog shell");
                    CheckDialogButtonPainting(prefs, check);
                    using (var bitmap = new Bitmap(prefs.Width, prefs.Height)) { prefs.DrawToBitmap(bitmap, new Rectangle(Point.Empty, prefs.Size)); bitmap.Save(Path.Combine(uiTestDirectory, "settings.png")); }
                    prefs.Close();
                }
                foreach (FlatButton button in new[] { menu, hide, add })
                {
                    button.SetHoverForTest(true);
                    using (var first = WidgetPaint.Render(button, Color.Magenta)) using (var second = WidgetPaint.Render(button, Color.Lime)) check(BitmapSignature(first) == BitmapSignature(second), "complete background repaint: " + button.AccessibleName);
                    button.SetHoverForTest(false);
                }
                using (var countdown = new ScheduleDialog(true, null))
                {
                    countdown.Shown += delegate
                    {
                        check(countdown.TopMost && (Native.GetStyle(countdown.Handle, -20) & 8) != 0, "reminder editor has the native topmost style");
                        check(utilityDialogDepth == 1 && !pinTimer.Enabled, "desktop-layer timer is suspended while editing a popup");
                        Pin(); check((Native.GetStyle(countdown.Handle, -20) & 8) != 0, "desktop-layer pin call cannot demote the reminder editor");
                        countdown.SelectCountdown(15, false); countdown.Refresh();
                        using (var bitmap = new Bitmap(countdown.Width, countdown.Height)) { countdown.DrawToBitmap(bitmap, new Rectangle(Point.Empty, countdown.Size)); bitmap.Save(Path.Combine(uiTestDirectory, "countdown-dialog.png")); }
                        countdown.ConfirmButton.PerformClick();
                    };
                    check(ShowUtilityDialog(countdown) == DialogResult.OK && countdown.UsesCountdown && countdown.DurationAmount == 15 && !countdown.DurationHours, "countdown editor confirms selected duration");
                    check(countdown.Selected == TaskService.CountdownTarget(countdown.CountdownStartedAt, 15, false), "countdown starts from the exact confirmation instant");
                }
                check(utilityDialogDepth == 0 && pinTimer.Enabled && !TopMost && (Native.GetStyle(Handle, -20) & 8) == 0, "closing the popup restores the main window to non-topmost mode");
                using (var deadline = new ScheduleDialog(false, null))
                {
                    deadline.Shown += delegate
                    {
                        check(deadline.TopMost && !deadline.CountdownChoice.Visible, "deadline editor stays topmost and has no countdown mode");
                        deadline.SelectAbsolute(new DateTime(2026, 10, 28)); deadline.Refresh(); SaveDialogPreview(deadline, "deadline-dialog.png");
                        deadline.ConfirmButton.PerformClick();
                    };
                    ShowUtilityDialog(deadline);
                    check(deadline.Selected.HasValue && deadline.Selected.Value.LocalDateTime.Date == new DateTime(2026, 10, 28), "modern deadline picker preserves a date independently of reminder time");
                }
                var expectedReminder = DateTime.Now.AddDays(1).Date.AddHours(9).AddMinutes(30);
                using (var absoluteEditor = new ScheduleDialog(true, null))
                {
                    absoluteEditor.Shown += delegate
                    {
                        var timeEditor = DialogControls(absoluteEditor).OfType<TextBox>().Single(t => t.AccessibleName == "提醒时间");
                        timeEditor.Text = "99:99"; absoluteEditor.ConfirmButton.PerformClick();
                        check(absoluteEditor.Visible && absoluteEditor.DialogResult == DialogResult.None && absoluteEditor.ErrorMessage.Text.Contains("有效"), "invalid typed time keeps the reminder editor open with an error");
                        absoluteEditor.SelectAbsolute(DateTime.Now.AddMinutes(-10)); absoluteEditor.ConfirmButton.PerformClick();
                        check(absoluteEditor.Visible && absoluteEditor.ErrorMessage.Text.Contains("未来"), "past absolute reminder remains rejected in the modern editor");
                        absoluteEditor.SelectAbsolute(expectedReminder);
                        check(absoluteEditor.ErrorMessage.Text == "", "editing the schedule clears an outdated validation message");
                        var dateField = DialogControls(absoluteEditor).OfType<DialogDateField>().Single(); dateField.PerformClick();
                        check(dateField.ActivePopup != null && dateField.ActivePopup.Visible && absoluteEditor.Visible, "modern date field opens a calendar without closing the reminder editor");
                        var calendarHost = dateField.ActivePopup.Items.OfType<ToolStripControlHost>().Single();
                        check(((MonthCalendar)calendarHost.Control).SelectionStart.Date == expectedReminder.Date, "calendar starts on the displayed reminder date");
                        using(var bitmap=new Bitmap(dateField.ActivePopup.Width,dateField.ActivePopup.Height)) { dateField.ActivePopup.DrawToBitmap(bitmap,dateField.ActivePopup.ClientRectangle); bitmap.Save(Path.Combine(uiTestDirectory,"calendar-popup.png")); }
                        dateField.ActivePopup.Close();
                        absoluteEditor.Refresh(); SaveDialogPreview(absoluteEditor, "absolute-dialog.png"); absoluteEditor.ConfirmButton.PerformClick();
                    };
                    check(ShowUtilityDialog(absoluteEditor) == DialogResult.OK && !absoluteEditor.UsesCountdown && absoluteEditor.Selected.Value.LocalDateTime == expectedReminder, "modern date and time fields confirm the exact requested local minute");
                }
                using (var presetEditor = new ScheduleDialog(true, null))
                {
                    presetEditor.Shown += delegate
                    {
                        presetEditor.CountdownChoice.PerformClick();
                        check(presetEditor.CountdownChoice.Checked && (presetEditor.CountdownChoice.AccessibilityObject.State & AccessibleStates.Checked) != 0, "painted reminder mode exposes the selected radio state");
                        var absoluteMode=DialogControls(presetEditor).OfType<DialogChoice>().Single(c => c.Text == "日期和时间");
                        presetEditor.CountdownChoice.Focus(); Native.SendMessage(presetEditor.CountdownChoice.Handle,0x100,new IntPtr((int)Keys.Right),IntPtr.Zero);
                        check(absoluteMode.Checked && !presetEditor.CountdownChoice.Checked && presetEditor.Visible, "arrow key selects the other reminder mode without submitting");
                        Native.SendMessage(absoluteMode.Handle,0x100,new IntPtr((int)Keys.Left),IntPtr.Zero);
                        check(presetEditor.CountdownChoice.Checked && !absoluteMode.Checked, "arrow navigation preserves exclusive reminder mode selection");
                        presetEditor.SelectCountdown(25, false);
                        var number = DialogControls(presetEditor).OfType<DialogNumberField>().Single(); number.Editor.Text = "0"; presetEditor.ConfirmButton.PerformClick();
                        check(presetEditor.Visible && presetEditor.ErrorMessage.Text.Contains("整数"), "invalid countdown text does not close or schedule a reminder");
                        DialogControls(presetEditor).OfType<DialogButton>().Single(b => b.Text == "5 分钟").PerformClick();
                        check(presetEditor.DurationAmount == 5 && !presetEditor.DurationHours && presetEditor.CountdownChoice.Checked, "modern five-minute preset switches and fills countdown mode");
                        DialogControls(presetEditor).OfType<DialogButton>().Single(b => b.Text == "1 小时").PerformClick();
                        check(presetEditor.DurationAmount == 1 && presetEditor.DurationHours, "modern hour preset sets the duration and unit together");
                        var unitField = DialogControls(presetEditor).OfType<DialogUnitField>().Single(); unitField.PerformClick();
                        check(unitField.ActivePopup != null && unitField.ActivePopup.Visible && unitField.ActivePopup.Items.Count == 2, "modern unit selector opens minute and hour commands");
                        var unitPopup=unitField.ActivePopup; unitPopup.Items[0].PerformClick(); if(unitPopup.Visible) unitPopup.Close();
                        check(!presetEditor.DurationHours && presetEditor.DurationAmount == 1, "unit popup changes to minutes without changing the typed quantity");
                        presetEditor.SelectCountdown(1,true);
                        check(!DialogControls(presetEditor).OfType<DialogDateField>().Single().Visible, "countdown mode hides the absolute date controls");
                        presetEditor.ConfirmButton.PerformClick();
                    };
                    check(ShowUtilityDialog(presetEditor) == DialogResult.OK && presetEditor.Selected == presetEditor.CountdownStartedAt.ToUniversalTime().AddHours(1), "modern hour preset schedules one hour from confirmation");
                }
                using (var clearEditor = new ScheduleDialog(true, DateTimeOffset.Now.AddHours(2).ToString("o")))
                {
                    clearEditor.Shown += delegate { DialogControls(clearEditor).OfType<DialogButton>().Single(b => b.Text == "清除提醒").PerformClick(); };
                    check(ShowUtilityDialog(clearEditor) == DialogResult.OK && clearEditor.Cleared && !clearEditor.Selected.HasValue, "modern clear action explicitly removes the reminder");
                }
                using (var cancelEditor = new ScheduleDialog(true, null))
                {
                    cancelEditor.Shown += delegate { cancelEditor.CancelButton.PerformClick(); };
                    check(ShowUtilityDialog(cancelEditor) == DialogResult.Cancel && !cancelEditor.Cleared && !cancelEditor.Selected.HasValue, "modern cancel action leaves the schedule unchanged");
                }
                using(var closeEditor = new ScheduleDialog(true, null))
                {
                    closeEditor.Shown += delegate { DialogControls(closeEditor).OfType<DialogButton>().Single(b => b.AccessibleName == "关闭").PerformClick(); };
                    check(ShowUtilityDialog(closeEditor) == DialogResult.Cancel && !closeEditor.Selected.HasValue, "fully painted close button cancels the modal editor");
                }
                check(typeof(FloatingDialog).IsAssignableFrom(typeof(SettingsDialog)) && typeof(FloatingDialog).IsAssignableFrom(typeof(ScheduleDialog)), "settings and reminder editors share floating-dialog behavior");
                service.SetCountdown(id, 25, false, DateTimeOffset.Now); OpenDetail(id); UpdateCountdownCaption();
                check(activeReminderButton.Text.Contains("不发送提醒"), "completed tasks do not display an active countdown");
                service.Toggle(id); RefreshDetails(); UpdateCountdownCaption();
                check(activeReminderButton.Text.Contains("剩余") && activeReminderButton.Text.Contains("倒计时"), "task details display the remaining countdown");
                SavePreview(Path.Combine(uiTestDirectory, "countdown-detail.png")); CloseDetail();
                service.Add("记录今天的想法"); string noteId = service.State.Tasks.Last().Id; OpenDetail(noteId);
                detailList.ScrollBy(100000); detailBack.Focus();
                var noteEditor = (NoteInput)notesInput;
                check(noteEditor.PlaceholderVisible && noteEditor.Text == "", "empty note uses a visual placeholder without stored text");
                check(detailList.Items.Single(c => c.Name == "notes-detail").Controls.Count == 1, "note card has a single direct input surface");
                check(noteEditor.PlaceholderColor.GetBrightness() > 0.6F, "note placeholder is light gray");
                SavePreview(Path.Combine(uiTestDirectory, "notes-empty.png"));
                noteEditor.Focus(); check(!noteEditor.PlaceholderVisible && noteEditor.Focused && noteEditor.SelectionStart == 0, "focusing empty notes removes the cue and places the caret at the start");
                noteEditor.SelectedText = "中文备注第一行\r\n第二行"; check(SaveDetailDraft(false), "direct multiline note entry saves");
                detailBack.Focus(); check(!noteEditor.PlaceholderVisible, "nonempty note has no placeholder after focus leaves");
                check(new TaskService(store).State.Tasks.Single(t => t.Id == noteId).Notes == "中文备注第一行\r\n第二行", "note text persists without any placeholder prefix");
                SavePreview(Path.Combine(uiTestDirectory, "notes-entered.png"));
                noteEditor.Clear(); check(SaveDetailDraft(false) && noteEditor.PlaceholderVisible, "clearing notes restores the placeholder after focus leaves");
                check(new TaskService(store).State.Tasks.Single(t => t.Id == noteId).Notes == "", "clearing notes saves an empty value");
                CloseDetail(); OpenDetail(noteId); detailList.ScrollBy(100000); detailBack.Focus();
                check(((NoteInput)notesInput).PlaceholderVisible, "placeholder returns when reopening an empty note"); CloseDetail();
                service.Add("现代菜单交互检查"); string menuId = service.State.Tasks.Last().Id; RenderTasks(); list.ScrollBy(-100000);
                using (var taskMenu = (ModernMenu)TaskMenu(menuId))
                {
                    check(taskMenu.Renderer is ModernMenuRenderer && taskMenu.Items.OfType<ToolStripMenuItem>().All(i => i is ModernMenuItem), "task context menu uses the modern renderer and command items");
                    check(taskMenu.Items.OfType<ModernMenuItem>().Single(i => i.Glyph == MenuGlyph.Delete).Destructive, "task deletion has a distinct destructive presentation");
                    taskMenu.Show(this, new Point(D(20), D(235))); taskMenu.Items[1].Select(); taskMenu.Refresh();
                    check(taskMenu.Items.OfType<ModernMenuItem>().All(i => i.Width >= i.GetPreferredSize(Size.Empty).Width - D(4)), "task popup width accommodates every command label");
                    SaveMenuPreview(taskMenu, "task-menu.png"); taskMenu.Close();
                }
                using (var actions = TaskMenu(menuId)) actions.Items.Cast<ToolStripItem>().Single(i => i.Text == "标记为重要").PerformClick();
                check(service.State.Tasks.Single(t => t.Id == menuId).Important && !detailRoot.Visible, "modern context command marks importance without opening details");
                using (var actions = TaskMenu(menuId)) actions.Items.Cast<ToolStripItem>().Single(i => i.Text == "标记为已完成").PerformClick();
                check(service.State.Tasks.Single(t => t.Id == menuId).Done, "modern context command completes the selected task");
                RunCompletionAndDeleteUiTests(check, menuId);
                service.Settings(s => s.GeometryLocked = true); UpdateGeometryPin();
                using (var applicationMenu = BuildAppMenu())
                {
                    var undo = applicationMenu.Items.OfType<ModernMenuItem>().Single(i => i.Glyph == MenuGlyph.Undo);
                    check(undo.Text == "撤销上一步" && undo.ShortcutKeyDisplayString == "Ctrl+Z", "app menu places shortcut hints separately from command text");
                    check(!applicationMenu.Items.OfType<ModernMenuItem>().Single(i => i.Glyph == MenuGlyph.Position).Enabled, "app menu disables resetting position while pinned");
                    applicationMenu.Show(menu, new Point(0, menu.Height + D(4))); applicationMenu.Items[0].Select(); applicationMenu.Refresh();
                    check(undo.Width >= undo.GetPreferredSize(Size.Empty).Width - D(4), "app popup reserves enough width for the shortcut column");
                    check(applicationMenu.Region != null && !applicationMenu.Region.IsVisible(0, 0), "modern popup has clipped rounded corners");
                    check(applicationMenu.Items.OfType<ModernMenuItem>().All(i => i.Height >= D(34)), "menu rows have comfortable pointer targets");
                    SaveMenuPreview(applicationMenu, "app-menu.png"); applicationMenu.Close();
                }
                service.Settings(s => s.GeometryLocked = false); UpdateGeometryPin();
                RenderTasks(); SavePreview(Path.Combine(uiTestDirectory, "aligned-list.png"));
                RunListUiTests(check);
                results.Add("PASS: isolated render checks complete; no desktop input automation");
            }
            catch (Exception ex) { results.Add("FAIL: " + ex); Environment.ExitCode = 1; }
            finally { File.WriteAllLines(Path.Combine(uiTestDirectory, "render-test.txt"), results); Exit(); }
        }
        void SaveMenuPreview(ContextMenuStrip popup, string filename)
        {
            using (var bitmap = new Bitmap(Width, Height)) using (var menuBitmap = new Bitmap(popup.Width, popup.Height))
            {
                DrawToBitmap(bitmap, ClientRectangle); popup.DrawToBitmap(menuBitmap, popup.ClientRectangle);
                if(list.Visible) using(var scene=list.RenderScene(Color.Magenta)) using(var graphics=Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(scene,list.Location);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var location = PointToClient(popup.Location); graphics.TranslateTransform(location.X, location.Y);
                    if (popup.Region != null) graphics.SetClip(popup.Region, System.Drawing.Drawing2D.CombineMode.Intersect);
                    graphics.DrawImageUnscaled(menuBitmap, Point.Empty);
                }
                bitmap.Save(Path.Combine(uiTestDirectory, filename));
            }
        }
        static IEnumerable<Control> DialogControls(Control parent)
        { foreach(Control child in parent.Controls) { yield return child; foreach(var descendant in DialogControls(child)) yield return descendant; } }
        void CheckDialogButtonPainting(SettingsDialog dialog, Action<bool,string> check)
        {
            var originalDefault=dialog.AcceptButton;
            using(var probe=new DialogButtonPaintProbe())
            {
                probe.SetBounds(D(20),D(12),D(190),D(38)); probe.Font=dialog.Font; dialog.Controls.Add(probe); probe.BringToFront(); probe.Focus(); dialog.AcceptButton=probe;
                foreach(DialogButtonKind kind in Enum.GetValues(typeof(DialogButtonKind)))
                {
                    probe.Kind=kind; probe.CalendarIcon=kind==DialogButtonKind.Secondary; probe.Text=probe.CalendarIcon ? "2026年10月29日" : "完成";
                    bool complete=true, cleanCorners=true;
                    string initial; using(var bitmap=probe.PaintOver(Color.Magenta)) initial=BitmapSignature(bitmap);
                    Action[] transitions={delegate { probe.EnterPointer(); },delegate { probe.PressPointer(MouseButtons.Left); },delegate { probe.ReleasePointer(MouseButtons.Left); },delegate { probe.LeavePointer(); }};
                    foreach(var transition in transitions)
                    {
                        transition(); probe.NotifyDefault(true);
                        Native.SendMessage(probe.Handle,0x128,new IntPtr(0x00030001),IntPtr.Zero); probe.Invalidate(); probe.Update();
                        using(var a=probe.PaintOver(Color.Magenta)) using(var b=probe.PaintOver(Color.Lime)) complete &= BitmapSignature(a)==BitmapSignature(b);
                        using(var bitmap=new Bitmap(probe.Width,probe.Height))
                        {
                            probe.DrawToBitmap(bitmap,probe.ClientRectangle); int background=dialog.BackColor.ToArgb();
                            cleanCorners &= bitmap.GetPixel(0,0).ToArgb()==background && bitmap.GetPixel(bitmap.Width-1,0).ToArgb()==background && bitmap.GetPixel(0,bitmap.Height-1).ToArgb()==background && bitmap.GetPixel(bitmap.Width-1,bitmap.Height-1).ToArgb()==background;
                        }
                    }
                    check(complete,"button fully repaints prior frame pixels across pointer/default states: "+kind);
                    check(cleanCorners,"button corners remain the parent background after native UI-state updates: "+kind);
                    using(var restored=probe.PaintOver(Color.Lime)) check(BitmapSignature(restored)==initial,"button returns to the identical normal frame after hover and press: "+kind);
                }
                int clicks=0; probe.Click+=delegate { clicks++; };
                probe.PressPointer(MouseButtons.Right); probe.InvokeClick(); probe.ReleasePointer(MouseButtons.Right); check(clicks==0,"painted dialog button ignores right-click activation");
                probe.KeyClick(Keys.Enter); probe.KeyClick(Keys.Space); check(clicks==2,"painted dialog button preserves Enter and Space activation");
                probe.Enabled=false; probe.PerformClick(); check(clicks==2,"disabled painted dialog button cannot activate"); probe.Enabled=true;
                probe.AccessibilityObject.DoDefaultAction(); check(clicks==3,"painted dialog button retains the accessibility default action");
                dialog.AcceptButton=originalDefault;
            }
        }
        sealed class DialogButtonPaintProbe : DialogButton
        {
            public DialogButtonPaintProbe() : base("绘制检查") { }
            protected override bool ShowFocusCues { get { return true; } }
            public void EnterPointer() { OnMouseEnter(EventArgs.Empty); }
            public void LeavePointer() { OnMouseLeave(EventArgs.Empty); }
            public void PressPointer(MouseButtons button) { OnMouseDown(new MouseEventArgs(button,1,Width/2,Height/2,0)); }
            public void ReleasePointer(MouseButtons button) { OnMouseUp(new MouseEventArgs(button,1,Width/2,Height/2,0)); }
            public void InvokeClick() { OnClick(EventArgs.Empty); }
            public void KeyClick(Keys key) { OnKeyDown(new KeyEventArgs(key)); OnKeyUp(new KeyEventArgs(key)); }
            public Bitmap PaintOver(Color initial)
            {
                var bitmap=new Bitmap(Width,Height); using(var screen=CreateGraphics()) bitmap.SetResolution(screen.DpiX,screen.DpiY);
                using(var graphics=Graphics.FromImage(bitmap)) { graphics.Clear(initial); OnPaint(new PaintEventArgs(graphics,ClientRectangle)); } return bitmap;
            }
        }
        void SaveDialogPreview(Form dialog, string filename)
        { using(var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, dialog.ClientRectangle); bitmap.Save(Path.Combine(uiTestDirectory,filename)); } }
    }
}

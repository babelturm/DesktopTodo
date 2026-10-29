using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DesktopTodo
{
    static class CompletionTests
    {
        public static void Run(Action<bool,string> check, string directory)
        {
            Directory.CreateDirectory(directory);
            var legacy = new AppState();
            legacy.Tasks.Add(new Todo { Id="old-trash", Text="旧回收站任务", ListId="month", Deleted=true, CreatedUtc=DateTimeOffset.UtcNow.ToString("o"), Notes="保留备注" });
            string raw=Store.Encode(legacy).Replace("\"Deleted\":true", "\"Deleted\" : true"); File.WriteAllText(Path.Combine(directory,"tasks.json"),raw);
            using(var store=new Store(directory))
            {
                var service=new TaskService(store); var task=service.State.Tasks.Single();
                check(task.Done && !task.Deleted && task.ListId=="month" && task.Notes=="保留备注", "v2 recycle migration retains list, title and details as completed");
                service.Settings(s=>s.Lists.Single(l=>l.Id=="month").CompletedExpanded=true);
                check(File.ReadAllText(store.FilePath+".pre-v2.0.3.bak")==raw,"migration backup accepts whitespace in legacy JSON and retains exact bytes");
                service.RenameList("month","本月计划"); service.Settings(s=>s.Lists.Single(l=>l.Id=="month").CompletedExpanded=false); service.Undo();
                check(!service.State.Lists.Single(l=>l.Id=="month").CompletedExpanded, "undoing a task/list action preserves the current completed-fold preference");
                service.Add("保留任务");
                service.AddStep("old-trash","保留步骤"); service.Delete("old-trash");
                check(!Store.Encode(service.State).Contains("old-trash"),"confirmed permanent deletion removes the complete task object");
                while(service.CanUndo) service.Undo();
                check(!service.State.Tasks.Any(t=>t.Id=="old-trash"),"no earlier undo snapshot can resurrect permanent deletion");
                service.Add("写入失败时保留"); string id=service.State.Tasks.Last().Id;
                string frozen=Store.Encode(service.State); Directory.CreateDirectory(store.FilePath+".tmp"); bool failed=false;
                try {service.Delete(id);} catch(IOException) {failed=true;} catch(UnauthorizedAccessException) {failed=true;}
                check(failed && Store.Encode(service.State)==frozen && new TaskService(store).State.Tasks.Any(t=>t.Id==id),"failed permanent delete leaves memory and saved task intact");
                Directory.Delete(store.FilePath+".tmp");
                service.Undo(); check(!service.State.Tasks.Any(t=>t.Id==id),"failed permanent delete does not consume or corrupt the previous undo history");
            }
        }
    }
    sealed partial class TodoForm
    {
        void DriveDeleteDialog(Action launch, Action<DeleteTaskDialog> inspect)
        {
            bool seen=false;
            using(var timer=new System.Windows.Forms.Timer {Interval=30})
            {
                timer.Tick+=delegate
                {
                    var dialog=Application.OpenForms.OfType<DeleteTaskDialog>().FirstOrDefault(); if(dialog==null) return;
                    timer.Stop(); seen=true; inspect(dialog);
                };
                timer.Start(); try {launch();} finally {timer.Stop();}
            }
            if(!seen) throw new InvalidOperationException("Delete command did not open its confirmation dialog.");
        }
        void RunCompletionAndDeleteUiTests(Action<bool,string> check,string id)
        {
            CloseDetail(); string categoryId=service.State.SelectedListId;
            service.Settings(s=>s.Lists.Single(l=>l.Id==categoryId).CompletedExpanded=false); RenderTasks();
            check(list.CompletedCount>0 && !list.Items.Any(r=>r.Task.Done) && !list.CompletedBounds.IsEmpty,"completed header remains visible while completed task rows are collapsed");
            list.ScrollBy(100000); SavePreview(Path.Combine(uiTestDirectory,"completed-collapsed.png"));
            list.ToggleCompleted(); list.ScrollBy(100000);
            check(list.Items.Any(r=>r.Task.Id==id && r.Task.Done) && list.Items.Where(r=>!r.Task.Done).All(r=>r.Bottom<list.CompletedBounds.Top),"expanded completed tasks follow pending tasks and the fold header");
            check(list.Items.Where(r=>r.Task.Done).All(r=>r.Bounds.Top>list.CompletedBounds.Bottom) && list.Items.All(r=>r.Right<list.RailLeft),"completed rows and header retain their own layout and scrollbar gutter");
            check(list.AccessibilityObject.GetChildCount()==list.Items.Count()+1,"completed toggle is represented in the accessible child sequence");
            int pending=list.Items.Count(r=>!r.Task.Done); var accessible=list.AccessibilityObject.GetChild(pending);
            check(accessible.Name=="已完成 "+list.CompletedCount && accessible.State==AccessibleStates.Expanded,"completed header exposes its current state to accessibility tools");
            accessible.DoDefaultAction(); check(!list.CompletedExpanded && !list.Items.Any(r=>r.Task.Done),"accessible completed toggle collapses the section");
            list.Focus(); Native.SendMessage(list.Handle,0x100,new IntPtr((int)Keys.Space),IntPtr.Zero);
            check(list.CompletedExpanded && list.Items.Any(r=>r.Task.Id==id),"Space on the focused completed header expands the section");
            list.ScrollBy(100000);
            SavePreview(Path.Combine(uiTestDirectory,"completed-expanded.png"));
            using(var a=list.RenderScene(Color.Magenta)) using(var b=list.RenderScene(Color.Lime)) check(BitmapSignature(a)==BitmapSignature(b),"expanded completed section fully repaints old frame pixels");
            for(int i=0;i<16;i++) { list.ToggleCompleted(); list.ScrollBy(i%2==0 ? -D(35) : D(35)); }
            check(list.CompletedExpanded && list.Items.Any(r=>r.Task.Id==id),"repeated folding and scrolling retains the completed task");
            list.ScrollToTask(id); var row=list.Items.Single(r=>r.Task.Id==id);
            list.DispatchPointer(new Point(row.CheckBounds.Left+3,row.CheckBounds.Top-list.ScrollOffset+3),MouseButtons.Left);
            check(!service.State.Tasks.Single(t=>t.Id==id).Done && list.Items.Single(r=>r.Task.Id==id).Bounds.Top<list.CompletedBounds.Top,"clicking a completed circle returns the task to pending above the fold");
            Run(delegate {service.Toggle(id);},"已完成");
            using(var hidden=(ModernMenu)TaskMenu(id)) check(!hidden.DispatchCommandKey(Keys.Delete),"Delete shortcut is inactive while its context menu is closed");
            string before=Store.Encode(service.State);
            using(var actions=(ModernMenu)TaskMenu(id))
            {
                check(actions.DeleteCommand.Text=="删除任务" && actions.DeleteCommand.ShortcutKeyDisplayString=="Delete" && actions.DeleteCommand.Destructive && actions.DeleteCommand.ShortcutKeys==Keys.None,"delete command is labeled without registering a global text-editing shortcut");
                actions.Show(list,new Point(D(10),D(10)));
                DriveDeleteDialog(delegate
                {
                    var method=typeof(ModernMenu).GetMethod("ProcessCmdKey",System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    object[] args={Message.Create(actions.Handle,0x100,new IntPtr((int)Keys.Delete),IntPtr.Zero),Keys.Delete};
                    check((bool)method.Invoke(actions,args),"Delete through the menu command-key handler opens confirmation");
                },delegate(DeleteTaskDialog dialog)
                {
                    check(dialog.TopMost && utilityDialogDepth==1 && !pinTimer.Enabled,"delete confirmation stays above windows and suspends desktop-layer refresh");
                    check(dialog.AcceptButton==dialog.CancelAction && dialog.CancelButton==dialog.CancelAction && dialog.ActiveControl==dialog.CancelAction,"delete confirmation defaults Enter and Esc to cancel");
                    check(dialog.ConfirmButton.Kind==DialogButtonKind.Danger && !actions.Visible,"context menu closes before the red delete confirmation opens");
                    SaveDialogPreview(dialog,"delete-confirmation.png"); dialog.CancelAction.PerformClick();
                });
            }
            check(Store.Encode(service.State)==before && new TaskService(store).State.Tasks.Any(t=>t.Id==id),"cancelling a keyboard deletion preserves the entire task and saved state");
            using(var actions=(ModernMenu)TaskMenu(id))
                DriveDeleteDialog(delegate {actions.DeleteCommand.PerformClick();},delegate(DeleteTaskDialog dialog) {dialog.ConfirmButton.PerformClick();});
            check(!service.State.Tasks.Any(t=>t.Id==id) && !list.Items.Any(r=>r.Task.Id==id),"confirmed context deletion permanently removes the task from data and canvas");
            service.Undo(); RenderTasks(); check(!service.State.Tasks.Any(t=>t.Id==id),"ordinary undo cannot revive a confirmed permanent deletion");
            input.Text="详情页删除测试"; add.PerformClick(); string detailTask=service.State.Tasks.Last().Id; OpenDetail(detailTask); notesInput.Text="未保存的备注";
            using(var actions=(ModernMenu)TaskMenu(detailTask))
                DriveDeleteDialog(delegate {actions.DeleteCommand.PerformClick();},delegate(DeleteTaskDialog dialog) {dialog.CancelAction.PerformClick();});
            check(detailRoot.Visible && service.State.Tasks.Single(t=>t.Id==detailTask).Notes=="未保存的备注","cancelling from details retains the page and first saves its draft");
            using(var actions=(ModernMenu)TaskMenu(detailTask))
                DriveDeleteDialog(delegate {actions.DeleteCommand.PerformClick();},delegate(DeleteTaskDialog dialog) {dialog.ConfirmButton.PerformClick();});
            check(!detailRoot.Visible && detailId==null && !service.State.Tasks.Any(t=>t.Id==detailTask),"confirmed deletion from details returns to the task list without a stale editor");
            using(var longDialog=new DeleteTaskDialog(new string('长',2000)))
            { longDialog.Shown+=delegate {check(longDialog.ConfirmButton.Parent.Bottom<=longDialog.ClientSize.Height && longDialog.CancelAction.Left>longDialog.ConfirmButton.Right,"long task titles keep both confirmation actions separate and reachable"); longDialog.CancelAction.PerformClick();}; ShowUtilityDialog(longDialog); }
        }
    }
}

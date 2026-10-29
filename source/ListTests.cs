using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DesktopTodo
{
    static class ListTests
    {
        public static void Run(Action<bool,string> check,string directory)
        {
            Directory.CreateDirectory(directory); var json=new JavaScriptSerializer();
            string oldId=Guid.NewGuid().ToString("N"),oldStep=Guid.NewGuid().ToString("N"); string created=DateTimeOffset.UtcNow.AddDays(-2).ToString("o");
            string legacy=json.Serialize(new {Version=1,Tasks=new[]{new {Id=oldId,Text="旧版任务",Done=true,Deleted=true,Important=true,CreatedUtc=created,Steps=new[]{new {Id=oldStep,Text="旧版步骤",Done=true}},Notes="旧版备注",DueDate="2026-12-01",ReminderUtc=created,ReminderDeliveredUtc=created}},GeometryLocked=true,Width=380,Height=560});
            File.WriteAllText(Path.Combine(directory,"tasks.json"),legacy);
            using(var store=new Store(directory))
            {
                var service=new TaskService(store); var old=service.State.Tasks.Single();
                check(service.State.Version==2 && service.State.Lists.Count==3 && old.ListId==ListSchema.DefaultId,"v1 data gains three starter lists and assigns existing tasks to the default");
                check(old.Id==oldId && old.Steps.Single().Id==oldStep && old.Steps[0].Done && old.Done && !old.Deleted && old.Notes=="旧版备注" && old.ReminderDeliveredUtc==created && service.State.GeometryLocked,"v1 recycle items become completed while retaining all details, delivery and preferences");
                check(File.ReadAllText(store.FilePath)==legacy,"loading a legacy file does not rewrite it before a successful save");
                service.SelectList("month");
                check(File.ReadAllText(store.FilePath+".pre-v2.bak")==legacy && Store.Decode(File.ReadAllText(store.FilePath)).Version==2,"first v2 save preserves an exact permanent v1 backup");
                check(File.ReadAllText(store.FilePath+".pre-v2.0.3.bak")==legacy,"recycle migration preserves an exact original backup before saving");
                string research=service.CreateList(" 课题研究 "); service.Add("新列表的任务"); string newId=service.State.Tasks.Last().Id;
                check(service.State.Lists.Single(l => l.Id==research).Name=="课题研究" && service.State.SelectedListId==research && service.State.Tasks.Last().ListId==research,"create selects a trimmed list and new tasks belong to it");
                service.RenameList(research,"研究计划");
                check(new TaskService(store).State.Lists.Single(l => l.Id==research).Name=="研究计划" && service.State.Tasks.Last().ListId==research,"rename persists without changing list identity or membership");
                foreach(string invalid in new[]{" ","今日待办",new string('名',41),"多行\n名称"})
                { string before=Store.Encode(service.State); bool rejected=false; try { service.CreateList(invalid); } catch(ArgumentException) { rejected=true; } check(rejected && Store.Encode(service.State)==before,"invalid or duplicate list name cannot change state: "+invalid.Replace("\n","/")); }
                string english=service.CreateList("Work"); bool duplicate=false; try { service.RenameList(english,"work"); service.CreateList("WORK"); } catch(ArgumentException) { duplicate=true; }
                check(duplicate,"list names are unique regardless of Latin letter case");
                service.MoveTask(oldId,"month"); old=service.State.Tasks.Single(t => t.Id==oldId);
                check(old.ListId=="month" && old.Done && !old.Deleted && old.Important && old.CreatedUtc==created && old.Notes=="旧版备注" && old.Steps.Single().Id==oldStep,"moving a migrated task preserves completion, details and timestamp");
                var now=DateTimeOffset.UtcNow; service.SetReminder(newId,now.AddMinutes(-1)); service.SelectList("year");
                check(service.DueReminders(now).Any(t => t.Id==newId && t.ListId==research),"reminders are evaluated across every list regardless of the selected view");
                service.MoveTask(newId,"month"); service.MarkReminderDelivered(newId,service.State.Tasks.Single(t => t.Id==newId).ReminderUtc,now); service.Undo();
                check(service.State.Tasks.Single(t => t.Id==newId).ListId==research && service.State.Tasks.Single(t => t.Id==newId).ReminderDeliveredUtc!=null && service.State.SelectedListId=="year","undoing a move restores membership and preserves delivered reminders and current view");
                int count=service.State.Tasks.Count; service.DeleteList(research,"month");
                check(!service.State.Lists.Any(l => l.Id==research) && service.State.Tasks.Count==count && service.State.Tasks.All(t => service.State.Lists.Any(l => l.Id==t.ListId)),"deleting a list merges every task without loss or dangling membership");
                service.Undo(); check(service.State.Lists.Any(l => l.Id==research) && service.State.Tasks.Single(t => t.Id==newId).ListId==research,"undo restores a deleted list and original task classifications");
                bool badTarget=false; string frozen=Store.Encode(service.State); try { service.MoveTask(newId,"missing"); } catch(ArgumentException) { badTarget=true; }
                check(badTarget && Store.Encode(service.State)==frozen,"missing move target is rejected without partial changes");
                bool sameTarget=false; try { service.DeleteList(research,research); } catch(ArgumentException) { sameTarget=true; } check(sameTarget,"a list cannot merge into itself");
                check(File.ReadAllText(store.FilePath+".pre-v2.bak")==legacy,"later saves never overwrite the permanent legacy backup");
                service.Settings(s => { s.Lists=new List<TodoList>{new TodoList{Id="only",Name="唯一列表"}}; s.SelectedListId="only"; foreach(var t in s.Tasks) t.ListId="only"; });
                bool lastRejected=false; try { service.DeleteList("only","only"); } catch(ArgumentException) { lastRejected=true; } check(lastRejected && service.State.Lists.Count==1,"the last remaining list cannot be deleted");
                string beforeFailure=Store.Encode(service.State); Directory.CreateDirectory(store.FilePath+".tmp"); bool failed=false;
                try { service.CreateList("写入失败不应出现的列表"); } catch(IOException) { failed=true; } catch(UnauthorizedAccessException) { failed=true; }
                check(failed && Store.Encode(service.State)==beforeFailure && !service.State.Lists.Any(l=>l.Name=="写入失败不应出现的列表"),"failed list save does not publish an uncommitted list or selection");
            }
            var invalidState=new AppState(); invalidState.Tasks.Add(new Todo{Id="test",Text="悬空任务",ListId="missing"}); bool orphanRejected=false;
            try { Store.Decode(Store.Encode(invalidState)); } catch(InvalidDataException) { orphanRejected=true; } check(orphanRejected,"corrupt v2 membership is rejected instead of silently reassigned");
            bool missingLists=false; try { Store.Decode("{\"Version\":2,\"Tasks\":[]}"); } catch(InvalidDataException) { missingLists=true; } check(missingLists,"v2 files must contain their list definitions");
            var selected=new AppState{SelectedListId="gone"}; check(Store.Decode(Store.Encode(selected)).SelectedListId==ListSchema.DefaultId,"missing last-selected list falls back to an existing list");
            var startupStore=new MemoryStartupStore{Value="\"C:\\old\\DesktopTodo.exe\""}; var startup=new StartupService(startupStore,@"C:\new\DesktopTodo.exe"); startup.UpdatePathIfEnabled();
            check(startupStore.Value=="\"C:\\new\\DesktopTodo.exe\"","an enabled startup entry is updated to the upgraded executable path");
            startup.SetEnabled(false); startup.UpdatePathIfEnabled(); check(!startup.Enabled && startupStore.Value==null,"upgrading does not enable a disabled startup entry");
        }
    }
    sealed partial class TodoForm
    {
        void RunListUiTests(Action<bool,string> check)
        {
            CloseDetail(); SwitchList("month");
            check(title.Text=="本月目标" && !list.Items.Any() && list.CompletedCount==0,"switching list filters both rows and completed count");
            input.Text="完成本月材料检测"; add.PerformClick(); string taskId=service.State.Tasks.Last().Id;
            check(service.State.Tasks.Last().ListId=="month" && list.Items.Single().Task.Id==taskId,"add banner creates tasks in the selected list");
            Run(delegate {service.Toggle(taskId);},"完成");
            check(!list.Items.Any() && list.CompletedCount==1 && !list.CompletedExpanded,"completed items are collapsed below pending tasks within their own list");
            list.ToggleCompleted();
            check(list.Items.Single().Task.Id==taskId && list.CompletedExpanded,"expanding completed shows the selected list's finished task");
            SwitchList("year"); check(list.CompletedCount==0,"completed counts do not leak across lists"); SwitchList("month");
            check(list.CompletedExpanded && new TaskService(store).State.Lists.Single(l=>l.Id=="month").CompletedExpanded,"each list remembers its completed section across switches and restart");
            Run(delegate {service.Toggle(taskId);},"恢复为待办");
            string research=null;
            using(var create=new ListNameDialog("新建列表","",name=>service.ValidateListName(name)))
            {
                create.Shown+=delegate
                {
                    create.ConfirmButton.PerformClick(); check(create.Visible && create.ErrorMessage.Text.Length>0,"new-list dialog rejects an empty name without closing");
                    create.NameField.Editor.Text="今日待办"; create.ConfirmButton.PerformClick(); check(create.Visible && create.ErrorMessage.Text.Contains("同名"),"new-list dialog reports duplicate names");
                    create.NameField.Editor.Text="课题研究"; create.Refresh(); SaveDialogPreview(create,"new-list.png"); create.ConfirmButton.PerformClick();
                };
                check(ShowUtilityDialog(create)==DialogResult.OK && create.SelectedName=="课题研究","new-list dialog returns the validated name");
                Run(delegate {research=service.CreateList(create.SelectedName);},"已创建列表"); RenderTasks();
            }
            using(var rename=new ListNameDialog("重命名列表","课题研究",name=>service.ValidateListName(name,research)))
            { rename.Shown+=delegate {rename.NameField.Editor.Text="科研计划";rename.ConfirmButton.PerformClick();}; if(ShowUtilityDialog(rename)==DialogResult.OK) Run(delegate {service.RenameList(research,rename.SelectedName);},"已重命名"); }
            check(title.Text=="科研计划" && new TaskService(store).State.Lists.Single(l=>l.Id==research).Name=="科研计划","rename updates the visible header and saved list");
            string longName=new string('研',40); int oldWidth=Width; Run(delegate {service.RenameList(research,longName);},"长名称检查");
            check(title.Text==longName && title.AccessibleName.Contains(longName) && Width==oldWidth && title.Right<=ClientSize.Width,"long list names keep their full accessible name without widening the widget");
            Run(delegate {service.RenameList(research,"科研计划");},"已恢复名称");
            using(var many=new ModernMenu(DeviceDpiScale()))
            {
                for(int i=0;i<36;i++) many.AddCommand("较长的示例列表名称 "+(i+1),MenuGlyph.List,delegate{});
                many.Show(title,new Point(0,title.Height)); check(many.Width<=Screen.FromControl(this).WorkingArea.Width && many.Height<=Screen.FromControl(this).WorkingArea.Height,"many-list popup stays within the screen working area"); many.Close();
            }
            using(var cancelled=new ListNameDialog("新建列表","不会创建的列表",name=>service.ValidateListName(name)))
            { int total=service.State.Lists.Count; cancelled.Shown+=delegate {cancelled.CancelButton.PerformClick();}; check(ShowUtilityDialog(cancelled)==DialogResult.Cancel && service.State.Lists.Count==total,"cancelling list creation leaves stored lists unchanged"); }
            SwitchList("month");
            using(var picker=new ListPickerDialog("移动到列表",service.State.Lists,"month","选择任务所属列表。","移动"))
            { picker.Shown+=delegate {picker.ListButtons[research].PerformClick(); picker.Refresh(); SaveDialogPreview(picker,"move-task.png"); picker.ConfirmButton.PerformClick();}; if(ShowUtilityDialog(picker)==DialogResult.OK) CommitTaskMove(taskId,picker.SelectedId); }
            check(service.State.SelectedListId=="month" && !list.Items.Any() && service.State.Tasks.Single(t=>t.Id==taskId).ListId==research,"moving from a context view removes the row from its old list");
            OpenDetail(taskId); check(service.State.SelectedListId==research && detailList.Items.Any(c=>c.Name=="list-detail"),"opening a cross-list task selects its list and exposes classification in details");
            detailTitleInput.Text="完成年度研究计划"; CommitTaskMove(taskId,"year");
            check(detailRoot.Visible && service.State.SelectedListId=="year" && service.State.Tasks.Single(t=>t.Id==taskId).Text=="完成年度研究计划","moving from details first saves edits and keeps the moved task open");
            CloseDetail(); check(title.Text=="年度计划" && list.Items.Single().Task.Id==taskId,"return from moved task shows its new list");
            SwitchList(research); for(int i=0;i<16;i++) service.Add("研究任务 "+(i+1)); RenderTasks(); list.ScrollBy(100000); SwitchList("year");
            check(list.ScrollOffset==0 && list.Items.Single().Task.Id==taskId,"list switch resets the old list scroll offset");
            check(new TaskService(store).State.SelectedListId=="year","selected list survives a restart");
            SwitchList("today"); OnNotificationActivated("task="+taskId); Application.DoEvents();
            check(detailRoot.Visible && detailId==taskId && service.State.SelectedListId=="year","notification activation opens the exact task and its list from another list"); CloseDetail();
            using(var selector=BuildListMenu())
            { selector.Show(title,new Point(0,title.Height+D(4))); selector.Items[0].Select(); selector.Refresh(); SaveMenuPreview(selector,"list-selector.png"); selector.Close(); }
            SavePreview(Path.Combine(uiTestDirectory,"year-list.png"));
            SwitchList(research); int before=service.State.Tasks.Count;
            using(var merge=new ListPickerDialog("合并并删除列表",service.State.Lists.Where(l=>l.Id!=research).ToList(),null,"其中任务将全部移入所选列表。","合并并删除"))
            {
                merge.Shown+=delegate {check(!merge.ConfirmButton.Enabled,"delete-list confirmation requires an explicit receiving list"); merge.ListButtons["month"].PerformClick(); merge.ConfirmButton.PerformClick();};
                if(ShowUtilityDialog(merge)==DialogResult.OK) Run(delegate {service.DeleteList(research,merge.SelectedId);},"已合并");
            }
            check(service.State.Tasks.Count==before && !service.State.Lists.Any(l=>l.Id==research) && service.State.SelectedListId=="month","delete-list UI merges tasks and selects the destination");
            Run(delegate {service.Undo();},"已撤销"); check(service.State.Lists.Any(l=>l.Id==research) && service.State.Tasks.Count(t=>t.ListId==research)==16,"undo restores a list deleted through the UI workflow");
            SwitchList("today"); SavePreview(Path.Combine(uiTestDirectory,"today-list.png"));
        }
    }
}

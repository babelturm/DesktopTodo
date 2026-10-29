using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DesktopTodo
{
    public class TodoList { public string Id { get; set; } public string Name { get; set; } public bool CompletedExpanded { get; set; } }
    static class ListSchema
    {
        public const string DefaultId="today";
        public static List<TodoList> Defaults()
        { return new List<TodoList> { new TodoList {Id=DefaultId,Name="今日待办"}, new TodoList {Id="month",Name="本月目标"}, new TodoList {Id="year",Name="年度计划"} }; }
        public static void UpgradeAndValidate(AppState state,bool hasLists)
        {
            if(state.Version==1)
            { state.Lists=Defaults(); state.SelectedListId=DefaultId; foreach(var task in state.Tasks) if(task!=null) task.ListId=DefaultId; state.Version=2; }
            else if(!hasLists) throw new InvalidDataException("清单缺少列表信息。");
            if(state.Lists==null || state.Lists.Count==0) throw new InvalidDataException("至少需要保留一个列表。");
            var ids=new HashSet<string>(); var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var list in state.Lists)
                if(list==null || string.IsNullOrWhiteSpace(list.Id) || !ids.Add(list.Id) || string.IsNullOrWhiteSpace(list.Name) || list.Name.Length>40 || list.Name.Any(char.IsControl) || !names.Add(list.Name.Trim())) throw new InvalidDataException("列表名称或标识无效。");
            if(state.Tasks.Any(t => t!=null && !ids.Contains(t.ListId))) throw new InvalidDataException("任务所属列表不存在，文件已保留。");
            if(!ids.Contains(state.SelectedListId)) state.SelectedListId=state.Lists[0].Id;
        }
    }
    public sealed partial class TaskService
    {
        public string ValidateListName(string name,string exceptId=null)
        {
            name=(name??"").Trim();
            if(name.Length==0 || name.Length>40 || name.Any(char.IsControl)) throw new ArgumentException("列表名称需为 1 至 40 个字符，不能换行。");
            if(State.Lists.Any(l => l.Id!=exceptId && string.Equals(l.Name,name,StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已有同名列表，请换一个名称。");
            return name;
        }
        public string CreateList(string name)
        {
            name=ValidateListName(name); string id=Guid.NewGuid().ToString("N");
            Change(s => { s.Lists.Add(new TodoList {Id=id,Name=name}); s.SelectedListId=id; }); return id;
        }
        public void RenameList(string id,string name)
        {
            name=ValidateListName(name,id); var list=State.Lists.Single(l => l.Id==id); if(list.Name==name) return;
            Change(s => s.Lists.Single(l => l.Id==id).Name=name);
        }
        public void SelectList(string id)
        {
            if(!State.Lists.Any(l => l.Id==id)) throw new ArgumentException("该列表已不存在。");
            if(State.SelectedListId!=id) Settings(s => s.SelectedListId=id);
        }
        public void MoveTask(string taskId,string targetId,bool selectTarget=false)
        {
            if(!State.Lists.Any(l => l.Id==targetId)) throw new ArgumentException("目标列表已不存在。");
            var task=State.Tasks.Single(t => t.Id==taskId);
            if(task.ListId==targetId) { if(selectTarget) SelectList(targetId); return; }
            Change(s => { s.Tasks.Single(t => t.Id==taskId).ListId=targetId; if(selectTarget) s.SelectedListId=targetId; });
        }
        public void DeleteList(string id,string targetId)
        {
            if(State.Lists.Count<=1) throw new ArgumentException("至少需要保留一个列表。");
            if(id==targetId || !State.Lists.Any(l => l.Id==id) || !State.Lists.Any(l => l.Id==targetId)) throw new ArgumentException("请选择其他列表接收任务。");
            Change(s => { foreach(var task in s.Tasks.Where(t => t.ListId==id)) task.ListId=targetId; s.Lists.RemoveAll(l => l.Id==id); if(s.SelectedListId==id) s.SelectedListId=targetId; });
        }
    }
    class ListTitleButton : FlatButton
    {
        bool hover,dragged; Point down;
        public bool AllowDrag=true;
        public event EventHandler DragRequested;
        public ListTitleButton() : base("","切换列表") { }
        protected override void OnMouseEnter(EventArgs e) { hover=true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover=false; base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down=e.Location; dragged=false; base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragged=false; }
        protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode==Keys.Enter || e.KeyCode==Keys.Space) dragged=false; base.OnKeyDown(e); }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if(!dragged && AllowDrag && e.Button==MouseButtons.Left && (Math.Abs(e.X-down.X)>SystemInformation.DragSize.Width/2 || Math.Abs(e.Y-down.Y)>SystemInformation.DragSize.Height/2))
            { dragged=true; if(DragRequested!=null) DragRequested(this,EventArgs.Empty); }
            base.OnMouseMove(e);
        }
        protected override void OnClick(EventArgs e) { if(!dragged) base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics; float scale=g.DpiX/96F; g.SmoothingMode=SmoothingMode.AntiAlias;
            if(hover) using(var path=Shapes.Round(new RectangleF(0,0,Width-1,Height-1),6*scale)) using(var brush=new SolidBrush(Color.FromArgb(18,255,255,255))) g.FillPath(brush,path);
            TextRenderer.DrawText(g,Text,Font,new Rectangle(0,0,Math.Max(1,Width-(int)(32*scale)),Height),ForeColor,TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using(var pen=new Pen(ForeColor,1.6F*scale)) { float x=Width-25*scale,y=Height/2F; g.DrawLines(pen,new[]{new PointF(x,y-3*scale),new PointF(x+6*scale,y+3*scale),new PointF(x+12*scale,y-3*scale)}); }
            if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g,new Rectangle(3,3,Width-6,Height-6),ForeColor,Color.Transparent);
        }
    }
    class ListNameDialog : ModernDialog
    {
        public readonly DialogTextField NameField;
        public readonly DialogButton ConfirmButton;
        public readonly Label ErrorMessage;
        public string SelectedName { get; private set; }
        public ListNameDialog(string title,string current,Func<string,string> validate) : base(title,460,320)
        {
            int cw=(int)Math.Round(ContentWidth/UiScale),ww=(int)Math.Round(ClientSize.Width/UiScale);
            var card=Card(0,112); LabelAt(card,"列表名称",16,12,cw-32,25,10,true);
            NameField=new DialogTextField(UiScale); Place(card,NameField,16,52,cw-32,40); NameField.Editor.MaxLength=40; NameField.Editor.AccessibleName="列表名称"; NameField.Editor.Text=current??"";
            LabelAt(Body,"例如：今日待办、本月目标、年度计划",24,118,cw,23,8.5F,false,Muted);
            ErrorMessage=LabelAt(Body,"",24,145,cw,28,8.5F,false,Color.Firebrick);
            ConfirmButton=new DialogButton("保存",DialogButtonKind.Primary); Place(Footer,ConfirmButton,ww-114,20,90,36);
            var cancel=new DialogButton("取消") {DialogResult=DialogResult.Cancel}; Place(Footer,cancel,ww-216,20,90,36); AcceptButton=ConfirmButton; CancelButton=cancel;
            NameField.Editor.TextChanged+=delegate { ErrorMessage.Text=""; };
            ConfirmButton.Click+=delegate { try { SelectedName=validate(NameField.Editor.Text); DialogResult=DialogResult.OK; Close(); } catch(Exception ex) { ErrorMessage.Text=ex.Message; NameField.Editor.Focus(); } };
            Shown+=delegate { NameField.Editor.Focus(); NameField.Editor.SelectAll(); };
        }
    }
    class ListPickerDialog : ModernDialog
    {
        public string SelectedId { get; private set; }
        public readonly DialogButton ConfirmButton;
        public readonly Dictionary<string,DialogButton> ListButtons=new Dictionary<string,DialogButton>();
        readonly ToolTip fullNames=new ToolTip();
        public ListPickerDialog(string title,IList<TodoList> lists,string currentId,string description,string confirmText) : base(title,460,Math.Min(560,228+lists.Count*48))
        {
            int cw=(int)Math.Round(ContentWidth/UiScale),ww=(int)Math.Round(ClientSize.Width/UiScale);
            LabelAt(Body,description,24,0,cw,48,9,false,Muted);
            foreach(var list in lists)
            {
                string id=list.Id; var button=new DialogButton(list.Name) {AlignTextLeft=true,AccessibleName=list.Name};
                Place(Body,button,24,58+ListButtons.Count*48,cw,40); fullNames.SetToolTip(button,list.Name); ListButtons.Add(id,button);
                button.Click+=delegate { SelectedId=id; foreach(var pair in ListButtons) { pair.Value.Kind=pair.Key==id ? DialogButtonKind.Primary : DialogButtonKind.Secondary; pair.Value.Invalidate(); } ConfirmButton.Enabled=true; };
            }
            Body.AutoScrollMinSize=new Size(0,D(70+lists.Count*48));
            var cancel=new DialogButton("取消") {DialogResult=DialogResult.Cancel}; Place(Footer,cancel,ww-246,20,90,36);
            ConfirmButton=new DialogButton(confirmText,DialogButtonKind.Primary) {Enabled=false}; Place(Footer,ConfirmButton,ww-144,20,120,36);
            ConfirmButton.Click+=delegate { if(SelectedId!=null) { DialogResult=DialogResult.OK; Close(); } }; AcceptButton=ConfirmButton; CancelButton=cancel;
            if(currentId!=null && ListButtons.ContainsKey(currentId)) { SelectedId=currentId; ListButtons[currentId].Kind=DialogButtonKind.Primary; ConfirmButton.Enabled=true; }
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if(disposing) fullNames.Dispose(); }
    }
    sealed partial class TodoForm
    {
        void SwitchList(string id)
        {
            if(!SaveDetailDraft() || !CloseDetail()) return;
            try { service.SelectList(id); message=null; RenderTasks(); } catch(Exception ex) { message=ex.Message; RenderTasks(); }
        }
        ModernMenu BuildListMenu()
        {
            var m=NewMenu();
            foreach(var category in service.State.Lists)
            {
                string id=category.Id; int count=service.State.Tasks.Count(t => t.ListId==id && !t.Done && !t.Deleted);
                m.AddCommand(category.Name,id==service.State.SelectedListId ? MenuGlyph.Complete : MenuGlyph.List,delegate { SwitchList(id); },count.ToString());
            }
            m.AddDivider(); m.AddCommand("新建列表",MenuGlyph.Plus,delegate { CreateListFromUi(); });
            m.AddCommand("重命名当前列表",MenuGlyph.Edit,delegate { RenameCurrentList(); });
            var remove=m.AddCommand("删除当前列表",MenuGlyph.Delete,delegate { DeleteCurrentList(); },null,true); remove.Enabled=service.State.Lists.Count>1;
            return m;
        }
        void CreateListFromUi()
        {
            if(!SaveDetailDraft()) return;
            using(var dialog=new ListNameDialog("新建列表","",name => service.ValidateListName(name)))
                if(ShowUtilityDialog(dialog)==DialogResult.OK) { if(Run(delegate { service.CreateList(dialog.SelectedName); },"已创建列表")) RenderTasks(); }
        }
        void RenameCurrentList()
        {
            if(!SaveDetailDraft()) return; string id=service.State.SelectedListId;
            using(var dialog=new ListNameDialog("重命名列表",service.State.Lists.Single(l => l.Id==id).Name,name => service.ValidateListName(name,id)))
                if(ShowUtilityDialog(dialog)==DialogResult.OK) Run(delegate { service.RenameList(id,dialog.SelectedName); },"列表已重命名");
        }
        void DeleteCurrentList()
        {
            if(!SaveDetailDraft() || service.State.Lists.Count<2) return;
            string id=service.State.SelectedListId; var current=service.State.Lists.Single(l => l.Id==id); int count=service.State.Tasks.Count(t => t.ListId==id);
            using(var dialog=new ListPickerDialog("合并并删除列表",service.State.Lists.Where(l => l.Id!=id).ToList(),null,"“"+current.Name+"”的 "+count+" 项任务将移到所选列表，含已完成和回收站任务。","合并并删除"))
                if(ShowUtilityDialog(dialog)==DialogResult.OK) { if(Run(delegate { service.DeleteList(id,dialog.SelectedId); },"列表已合并，任务已保留")) RenderTasks(); }
        }
        void MoveTaskToList(string id)
        {
            if(!SaveDetailDraft()) return; var task=service.State.Tasks.Single(t => t.Id==id);
            using(var dialog=new ListPickerDialog("移动到列表",service.State.Lists,task.ListId,"当前列表："+service.State.Lists.Single(l => l.Id==task.ListId).Name+"。请选择目标列表。","移动"))
                if(ShowUtilityDialog(dialog)==DialogResult.OK) CommitTaskMove(id,dialog.SelectedId);
        }
        void CommitTaskMove(string id,string targetId)
        {
            if(!SaveDetailDraft()) return;
            bool inDetail=detailRoot.Visible && detailId==id;
            if(Run(delegate { service.MoveTask(id,targetId,inDetail); },"已移至“"+service.State.Lists.Single(l => l.Id==targetId).Name+"”") && inDetail) RefreshDetails();
        }
        DetailCard BuildTaskListCard(Todo task)
        {
            string id=task.Id; var card=new DetailCard {Name="list-detail",BackColor=Color.White};
            var change=new DialogButton("列表　"+service.State.Lists.Single(l => l.Id==task.ListId).Name,DialogButtonKind.Quiet) {AlignTextLeft=true,ChevronIcon=true,AccessibleName="更改任务所属列表",Font=DetailFont(10F)};
            change.Click+=delegate { MoveTaskToList(id); }; card.Controls.Add(change);
            card.Arrange=width => { change.SetBounds(D(8),D(5),width-D(16),D(40)); card.Height=D(50); }; return card;
        }
    }
}

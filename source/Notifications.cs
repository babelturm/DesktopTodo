using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace DesktopTodo
{
    // Native multiline EDIT controls do not support EM_SETCUEBANNER.
    // Paint a cue without ever inserting placeholder text into the note value.
    class NoteInput : TextBox
    {
        public bool PlaceholderVisible { get { return TextLength == 0 && !Focused; } }
        public Color PlaceholderColor { get { return Color.FromArgb(162, 167, 178); } }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0xF && m.Msg != 0x317 && m.Msg != 0x318) return;
            if (!PlaceholderVisible) return;
            if (m.Msg == 0xF)
            { using (var g = Graphics.FromHwnd(Handle)) DrawPlaceholder(g); }
            else if ((m.Msg == 0x317 || m.Msg == 0x318) && m.WParam != IntPtr.Zero)
            { using (var g = Graphics.FromHdc(m.WParam)) DrawPlaceholder(g); }
        }
        void DrawPlaceholder(Graphics graphics)
        {
            var origin = GetPositionFromCharIndex(0);
            TextRenderer.DrawText(graphics, "添加备注", Font, new Rectangle(origin, new Size(Math.Max(1, ClientSize.Width - origin.X), ClientSize.Height)), PlaceholderColor,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Top);
        }
    }

    interface IReminderNotifications { void Send(Todo task); }

    static class NotificationSubmission
    {
        public static void Submit(Func<NotificationSetting> readSetting, Action show)
        {
            NotificationSetting? setting = null;
            try { setting = readSetting(); }
            catch (Exception ex)
            {
                // An identity-less desktop app may not have a settings record until
                // its first Show. Only defer this specific missing-record result;
                // other permission/service errors and Show failures must propagate.
                if (ex.HResult != unchecked((int)0x80070490)) throw;
            }
            if (setting.HasValue && setting.Value != NotificationSetting.Enabled)
                throw new InvalidOperationException("请在 Windows 设置的通知中允许‘桌面待办’。");
            show();
        }
    }

    static class ReminderDelivery
    {
        public static int Dispatch(TaskService service, IReminderNotifications notifications, DateTimeOffset now)
        {
            int sent = 0;
            foreach (var task in service.DueReminders(now).Take(5))
            {
                notifications.Send(task);
                service.MarkReminderDelivered(task.Id, task.ReminderUtc, now); sent++;
            }
            return sent;
        }
        public static void Requeue(TaskService service, string id, string scheduled)
        {
            var task = service.State.Tasks.FirstOrDefault(t => t.Id == id);
            if (task != null && task.ReminderUtc == scheduled && task.ReminderDeliveredUtc != null)
                service.Settings(s => s.Tasks.Single(t => t.Id == id).ReminderDeliveredUtc = null);
        }
    }

    static class NotificationPayload
    {
        public static string TaskId(string argument)
        {
            Guid id;
            return argument != null && argument.StartsWith("task=", StringComparison.Ordinal) && Guid.TryParseExact(argument.Substring(5), "N", out id)
                ? id.ToString("N") : null;
        }
        static string Clean(string value)
        {
            var result = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { result.Append(c); result.Append(value[++i]); }
                else result.Append(XmlConvert.IsXmlChar(c) ? c : ' ');
            }
            return result.ToString();
        }
        public static string Build(string title, string text, string argument)
        {
            var document = new System.Xml.XmlDocument();
            var toast = document.CreateElement("toast"); document.AppendChild(toast);
            toast.SetAttribute("launch", argument); toast.SetAttribute("duration", "long");
            var visual = document.CreateElement("visual"); toast.AppendChild(visual);
            var binding = document.CreateElement("binding"); binding.SetAttribute("template", "ToastGeneric"); visual.AppendChild(binding);
            foreach (var value in new[] { title, text }) { var line = document.CreateElement("text"); line.InnerText = Clean(value); binding.AppendChild(line); }
            return document.OuterXml;
        }
        public static ToastNotification Create(string title, string text, string argument, string tag)
        {
            var content = new Windows.Data.Xml.Dom.XmlDocument(); content.LoadXml(Build(title, text, argument));
            return new ToastNotification(content) { Tag = tag, Group = "reminders", ExpirationTime = DateTimeOffset.Now.AddDays(3) };
        }
    }

    sealed class NotificationRegistryValue
    {
        public string Path, Name, Value;
        public NotificationRegistryValue(string path, string name, string value) { Path = path; Name = name; Value = value; }
    }
    static class NotificationRegistration
    {
        public const string AppId = "DesktopTodo.Local.Reminders";
        public const string ActivatorClsid = "D21794C0-98A6-4E03-A937-A37F13B3EEB8";
        public static List<NotificationRegistryValue> Plan(string executable, string icon)
        {
            string root = @"Software\Classes\AppUserModelId\" + AppId;
            return new List<NotificationRegistryValue> {
                new NotificationRegistryValue(root, "DisplayName", "桌面待办"),
                new NotificationRegistryValue(root, "IconUri", icon),
                new NotificationRegistryValue(root, "CustomActivator", "{" + ActivatorClsid + "}"),
                new NotificationRegistryValue(@"Software\Classes\CLSID\{" + ActivatorClsid + @"}\LocalServer32", "", "\"" + executable + "\" --toast-activate")
            };
        }
        public static void Apply(IEnumerable<NotificationRegistryValue> values)
        {
            foreach (var value in values)
                using (var key = Registry.CurrentUser.CreateSubKey(value.Path)) key.SetValue(value.Name, value.Value, RegistryValueKind.String);
        }
    }

    [ComVisible(true), Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationActivationCallback
    {
        [PreserveSig] int Activate([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string argument, IntPtr data, uint dataCount);
    }
    [ComVisible(true), Guid(NotificationRegistration.ActivatorClsid), ClassInterface(ClassInterfaceType.None)]
    public sealed class NotificationActivator : INotificationActivationCallback
    {
        readonly Action<string> activated;
        public NotificationActivator(Action<string> activated) { this.activated = activated; }
        public int Activate(string appId, string argument, IntPtr data, uint dataCount)
        {
            try { if (appId == NotificationRegistration.AppId && (argument == "test" || NotificationPayload.TaskId(argument) != null)) activated(argument); return 0; }
            catch { return unchecked((int)0x80004005); }
        }
    }
    [ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INotificationClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool value);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class NotificationClassFactory : INotificationClassFactory
    {
        readonly NotificationActivator activator;
        public NotificationClassFactory(Action<string> activated) { activator = new NotificationActivator(activated); }
        public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110);
            IntPtr unknown = Marshal.GetIUnknownForObject(activator);
            try { return Marshal.QueryInterface(unknown, ref iid, out instance); }
            finally { Marshal.Release(unknown); }
        }
        public int LockServer(bool value) { return 0; }
    }

    sealed class WindowsReminderNotifications : IReminderNotifications, IDisposable
    {
        readonly NotificationClassFactory factory;
        readonly ToastNotifier notifier;
        readonly Action<string, string, string> failed;
        readonly Dictionary<string, ToastNotification> pending = new Dictionary<string, ToastNotification>();
        uint classCookie;
        bool disposed;
        public WindowsReminderNotifications(string executable, string directory, Action<string> activated, Action<string, string, string> failed)
        {
            this.failed = failed;
            string iconPath = NotificationIcon.Write(directory);
            NotificationRegistration.Apply(NotificationRegistration.Plan(executable, iconPath));
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(NotificationRegistration.AppId));
            factory = new NotificationClassFactory(activated);
            var clsid = new Guid(NotificationRegistration.ActivatorClsid);
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref clsid, factory, 4, 1, out classCookie));
            try { notifier = ToastNotificationManager.CreateToastNotifier(NotificationRegistration.AppId); }
            catch { CoRevokeClassObject(classCookie); classCookie = 0; throw; }
        }
        public void Send(Todo task)
        {
            SendCore("待办提醒", task.Text, "task=" + task.Id, task.Id.Substring(0, 16), task.Id, task.ReminderUtc);
        }
        public void SendTest() { SendCore("测试通知", "这是桌面待办的 Windows 系统通知。", "test", "test", null, null); }
        void SendCore(string title, string text, string argument, string tag, string id, string scheduled)
        {
            if (disposed) throw new ObjectDisposedException("WindowsReminderNotifications");
            var toast = NotificationPayload.Create(title, text, argument, tag);
            toast.Failed += delegate(ToastNotification sender, ToastFailedEventArgs args)
            { if (!disposed) failed(id, scheduled, "Windows 拒绝了通知：" + args.ErrorCode.Message); };
            // Keep event delegates alive while notifications can still be displayed.
            pending[tag] = toast;
            foreach (var expired in pending.Where(p => p.Value.ExpirationTime < DateTimeOffset.Now).Select(p => p.Key).ToArray()) pending.Remove(expired);
            NotificationSubmission.Submit(() => notifier.Setting, () => notifier.Show(toast));
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (classCookie != 0) { CoRevokeClassObject(classCookie); classCookie = 0; }
            pending.Clear(); GC.KeepAlive(factory);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
        [DllImport("ole32.dll")] static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.Interface)] INotificationClassFactory factory, uint context, uint flags, out uint cookie);
        [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace DesktopTodo
{
    static class NotificationTests
    {
        sealed class RecordingNotifications : IReminderNotifications
        {
            public int Sent; public bool Reject;
            public void Send(Todo task) { if (Reject) throw new InvalidOperationException("通知被关闭"); Sent++; }
        }
        public static void Run(Action<bool, string> check, TaskService service, string id, DateTimeOffset now)
        {
            int submitted = 0;
            NotificationSubmission.Submit(() => NotificationSetting.Enabled, () => submitted++);
            check(submitted == 1, "enabled Windows notifications submit normally");
            NotificationSubmission.Submit(delegate { throw new COMException("First-run record missing", unchecked((int)0x80070490)); }, () => submitted++);
            check(submitted == 2, "missing first-run settings record does not block the first Show");
            foreach (var blocked in new[] { NotificationSetting.DisabledForApplication, NotificationSetting.DisabledForUser, NotificationSetting.DisabledByGroupPolicy, NotificationSetting.DisabledByManifest })
            {
                bool blockedSubmission = false;
                try { NotificationSubmission.Submit(() => blocked, () => submitted++); } catch (InvalidOperationException) { blockedSubmission = true; }
                check(blockedSubmission && submitted == 2, "known disabled notification setting blocks submission: " + blocked);
            }
            bool lookupFailure = false;
            try { NotificationSubmission.Submit(delegate { throw new COMException("Access denied", unchecked((int)0x80070005)); }, () => submitted++); }
            catch (COMException ex) { lookupFailure = ex.HResult == unchecked((int)0x80070005); }
            check(lookupFailure && submitted == 2, "other settings errors are preserved and never treated as first-run registration");
            bool showFailure = false;
            try { NotificationSubmission.Submit(delegate { throw new COMException("Record missing", unchecked((int)0x80070490)); }, delegate { throw new COMException("Show failed", unchecked((int)0x80070490)); }); }
            catch (COMException ex) { showFailure = ex.HResult == unchecked((int)0x80070490); }
            check(showFailure, "a failure from actual Show is propagated instead of reported as success");
            var xml = new XmlDocument();
            string text = "中文 & <样品> \"引用\" 🌱\n第二行";
            xml.LoadXml(NotificationPayload.Build("待办提醒", text, "task=" + id));
            check(xml.SelectNodes("/toast/visual/binding/text")[1].InnerText == text, "toast XML preserves Chinese, XML characters and emoji");
            check(xml.DocumentElement.GetAttribute("launch") == "task=" + id && xml.SelectSingleNode("/toast/visual/binding").Attributes["template"].Value == "ToastGeneric", "toast activation points at the matching task");
            xml.LoadXml(NotificationPayload.Build("提醒", "control\u0001\ud800 end", "test"));
            check(xml.SelectNodes("/toast/visual/binding/text")[1].InnerText == "control   end", "toast sanitizes invalid XML control characters and lone surrogates");
            check(NotificationPayload.TaskId("task=" + id) == id && NotificationPayload.TaskId("task=../../other") == null && NotificationPayload.TaskId(null) == null, "activation accepts only an exact task GUID");
            var toast = NotificationPayload.Create("待办提醒", text, "task=" + id, id.Substring(0, 16));
            check(toast.Tag == id.Substring(0, 16) && toast.Group == "reminders" && toast.ExpirationTime > now, "native Windows toast object has a stable task tag and expiry");
            bool failedEventAttached = false;
            toast.Failed += delegate { failedEventAttached = true; };
            check(!failedEventAttached, "native WinRT failure event attaches without sending a real notification");
            var plan = NotificationRegistration.Plan(@"C:\带空格的路径\DesktopTodo.exe", @"C:\data\icon.png");
            check(plan.Single(p => p.Name == "DisplayName").Value == "桌面待办" && plan.Single(p => p.Name == "CustomActivator").Value == "{" + NotificationRegistration.ActivatorClsid + "}", "notification identity has its own name and COM activator");
            check(plan.Single(p => p.Name == "").Value == "\"C:\\带空格的路径\\DesktopTodo.exe\" --toast-activate", "COM launch path is quoted and handles spaces");
            check(plan.All(p => p.Path.StartsWith(@"Software\Classes\", StringComparison.Ordinal) && !p.Path.Contains("\\Run")), "notification registration is scoped to app classes and does not add startup entries");
            int activations = 0; string argument = null;
            var activator = new NotificationActivator(a => { activations++; argument = a; });
            activator.Activate(NotificationRegistration.AppId, "task=" + id, IntPtr.Zero, 0);
            check(activations == 1 && argument == "task=" + id, "notification click reaches its exact task callback");
            activator.Activate("Another.App", "task=" + id, IntPtr.Zero, 0); activator.Activate(NotificationRegistration.AppId, "bad argument", IntPtr.Zero, 0);
            check(activations == 1, "activation rejects a different app and malformed arguments");
            activator.Activate(NotificationRegistration.AppId, "test", IntPtr.Zero, 0); check(activations == 2 && argument == "test", "test notification activation is independent of task data");
            var factory = new NotificationClassFactory(a => { activations++; });
            Guid callbackId = typeof(INotificationActivationCallback).GUID; IntPtr instance;
            int hr = factory.CreateInstance(IntPtr.Zero, ref callbackId, out instance);
            check(hr == 0 && instance != IntPtr.Zero, "COM class factory exposes the native activation interface");
            if (instance != IntPtr.Zero) Marshal.Release(instance);
            hr = factory.CreateInstance(new IntPtr(1), ref callbackId, out instance); check(hr < 0 && instance == IntPtr.Zero, "COM class factory rejects aggregation");
            Guid unknownId = Guid.NewGuid(); hr = factory.CreateInstance(IntPtr.Zero, ref unknownId, out instance); check(hr < 0 && instance == IntPtr.Zero, "COM class factory rejects unsupported interfaces");
            // Process-local COM integration test: no registry writes and no toast display.
            Guid testClsid = Guid.NewGuid(); uint cookie;
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(ref testClsid, factory, 4, 1, out cookie));
            try
            {
                Guid factoryId = typeof(INotificationClassFactory).GUID; IntPtr factoryPointer;
                Marshal.ThrowExceptionForHR(CoGetClassObject(ref testClsid, 4, IntPtr.Zero, ref factoryId, out factoryPointer));
                try
                {
                    var nativeFactory = (INotificationClassFactory)Marshal.GetObjectForIUnknown(factoryPointer);
                    Marshal.ThrowExceptionForHR(nativeFactory.CreateInstance(IntPtr.Zero, ref callbackId, out instance));
                    try { var nativeCallback = (INotificationActivationCallback)Marshal.GetObjectForIUnknown(instance); int before = activations; nativeCallback.Activate(NotificationRegistration.AppId, "task=" + id, IntPtr.Zero, 0); check(activations == before + 1, "Windows COM registration routes an activation through the native class factory"); }
                    finally { Marshal.Release(instance); }
                }
                finally { Marshal.Release(factoryPointer); }
            }
            finally { CoRevokeClassObject(cookie); GC.KeepAlive(factory); }
            service.SetReminder(id, now.AddSeconds(-1)); var fake = new RecordingNotifications { Reject = true }; bool rejected = false;
            try { ReminderDelivery.Dispatch(service, fake, now); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && service.DueReminders(now).Any(t => t.Id == id), "failed system submission does not mark the reminder delivered");
            fake.Reject = false; int sent = ReminderDelivery.Dispatch(service, fake, now);
            check(sent == 1 && fake.Sent == 1 && !service.DueReminders(now).Any(t => t.Id == id), "successful submission is persisted once");
            check(ReminderDelivery.Dispatch(service, fake, now.AddSeconds(1)) == 0 && fake.Sent == 1, "next timer tick does not submit the same reminder again");
            string scheduled = service.State.Tasks.Single(t => t.Id == id).ReminderUtc;
            ReminderDelivery.Requeue(service, id, scheduled); check(service.DueReminders(now).Any(t => t.Id == id), "asynchronous Windows failure requeues the same reminder");
            ReminderDelivery.Dispatch(service, fake, now); service.SetReminder(id, now.AddMinutes(10));
            service.MarkReminderDelivered(id, service.State.Tasks.Single(t => t.Id == id).ReminderUtc, now);
            ReminderDelivery.Requeue(service, id, scheduled); check(service.State.Tasks.Single(t => t.Id == id).ReminderDeliveredUtc != null, "a stale failure cannot reset a newly scheduled reminder");
        }
        public static int RunWindowsIntegration(string directory)
        {
            Directory.CreateDirectory(directory);
            string appId = "DesktopTodo.Integration." + Guid.NewGuid().ToString("N");
            string registryPath = @"Software\Classes\AppUserModelId\" + appId;
            var log = new List<string>(); int result = 0; bool showAttempted = false;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
                { key.SetValue("DisplayName", "桌面待办静默诊断"); key.SetValue("CustomActivator", "{" + Guid.NewGuid() + "}"); }
                var notifier = ToastNotificationManager.CreateToastNotifier(appId);
                bool reproduced = false;
                try { var initial = notifier.Setting; log.Add("Initial settings: " + initial); }
                catch (Exception ex) { reproduced = ex.HResult == unchecked((int)0x80070490); log.Add("Initial settings HRESULT: 0x" + ex.HResult.ToString("X8")); if (!reproduced) throw; }
                if (!reproduced) throw new InvalidOperationException("Fresh-identity regression was not reproduced; test cannot establish the fix.");
                var xml = new Windows.Data.Xml.Dom.XmlDocument();
                xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>桌面待办静默诊断</text></binding></visual><audio silent='true'/></toast>");
                var toast = new ToastNotification(xml) { SuppressPopup = true, Tag = "first-run", Group = "diagnostics", ExpirationTime = DateTimeOffset.Now.AddMinutes(1) };
                NotificationSubmission.Submit(() => notifier.Setting, delegate { showAttempted = true; notifier.Show(toast); });
                log.Add("PASS: production submission accepts the first notification despite the missing settings record");
                bool found = false;
                for (int i = 0; i < 30; i++)
                {
                    Thread.Sleep(100);
                    var history = ToastNotificationManager.History.GetHistory(appId);
                    if (history.Any(t => t.Tag == "first-run" && t.Group == "diagnostics")) { found = true; break; }
                }
                if (!found) throw new InvalidOperationException("Windows notification history did not receive the submitted notification.");
                log.Add("PASS: Windows notification history contains the exact temporary test tag and group");
                if (notifier.Setting != NotificationSetting.Enabled) throw new InvalidOperationException("Post-submission setting is not Enabled.");
                log.Add("PASS: notification setting becomes readable after first submission");
            }
            catch (Exception ex) { log.Add("FAIL: " + ex.GetType().Name + " / 0x" + ex.HResult.ToString("X8") + " / " + ex.Message); result = 1; }
            finally
            {
                if (showAttempted) try { ToastNotificationManager.History.Remove("first-run", "diagnostics", appId); if (ToastNotificationManager.History.GetHistory(appId).Any(t => t.Tag == "first-run" && t.Group == "diagnostics")) throw new IOException("Temporary test notification remains."); log.Add("PASS: temporary silent notification removed"); }
                    catch (Exception ex) { log.Add("FAIL: history cleanup: " + ex.Message); result = 1; }
                try { Registry.CurrentUser.DeleteSubKey(registryPath, false); using (var key = Registry.CurrentUser.OpenSubKey(registryPath)) if (key != null) throw new IOException("Temporary identity remains."); log.Add("PASS: temporary identity removed; production identity and settings unchanged"); }
                catch (Exception ex) { log.Add("FAIL: identity cleanup: " + ex.Message); result = 1; }
                File.WriteAllLines(Path.Combine(directory, "notification-integration-test.txt"), log);
            }
            return result;
        }
        [DllImport("ole32.dll")] static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.Interface)] INotificationClassFactory factory, uint context, uint flags, out uint cookie);
        [DllImport("ole32.dll")] static extern int CoGetClassObject(ref Guid clsid, uint context, IntPtr reserved, ref Guid iid, out IntPtr instance);
        [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);
    }
}

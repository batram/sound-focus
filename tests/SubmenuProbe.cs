using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

static class SubmenuProbe
{
    static object Call(object value, string method) { return value.GetType().GetMethod(method).Invoke(value, null); }
    public static int Run(Assembly assembly, object watcher, StreamWriter log)
    {
        Type program = assembly.GetType("SoundFocus.Program");
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        using (Form owner = new Form { Text = "SoundFocus silent stream owner" })
        using (ContextMenuStrip context = new ContextMenuStrip())
        {
            owner.Show(); Application.DoEvents();
            program.GetField("watcher", flags).SetValue(null, watcher);
            program.GetField("menu", flags).SetValue(null, context);
            Call(watcher, "Start");
            watcher.GetType().GetMethod("WaitReady").Invoke(watcher, new object[] { 3000 });
            try
            {
                using (Process child = Process.Start(new ProcessStartInfo {
                    FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--play 0 6500 0",
                    UseShellExecute = false, CreateNoWindow = true }))
                {
                    Thread.Sleep(500);
                    if (Contains(watcher, child.Id)) throw new Exception("Quiet label appeared before three seconds");
                    Thread.Sleep(3000);
                    if (!Contains(watcher, child.Id)) throw new Exception("Silent active stream missing");
                    program.GetMethod("BuildMenu", flags).Invoke(null, new object[] { null, null });
                    ToolStripMenuItem submenu = Find(context);
                    if (submenu.Tag != null) throw new Exception("Submenu loaded before opening");
                    context.Show(owner, new System.Drawing.Point(10, 10));
                    submenu.ShowDropDown();
                    Stopwatch timeout = Stopwatch.StartNew();
                    while (submenu.DropDownItems[0].Text == "Loading…" && timeout.ElapsedMilliseconds < 4000)
                    { Application.DoEvents(); Thread.Sleep(10); }
                    bool found = false;
                    foreach (ToolStripItem item in submenu.DropDownItems)
                    {
                        log.WriteLine(item.Text + " enabled=" + item.Enabled);
                        if (item.Text.StartsWith("AudioProbe.exe — Active, quiet for "))
                        {
                            if (!item.Enabled) throw new Exception("Owner window not clickable");
                            found = true;
                        }
                    }
                    if (!found) throw new Exception("Grouped owner missing from submenu");
                    foreach (object entry in (IEnumerable)Call(watcher, "History"))
                        if ((int)entry.GetType().GetField("Pid").GetValue(entry) == Process.GetCurrentProcess().Id)
                            throw new Exception("Silent stream polluted audible history");
                    context.Close();
                    program.GetMethod("BuildMenu", flags).Invoke(null, new object[] { null, null });
                    if (!submenu.IsDisposed) throw new Exception("Old submenu not disposed");
                    submenu = Find(context);
                    program.GetMethod("PopulateOtherAudioSessions", flags).Invoke(null, new object[] { submenu });
                    program.GetMethod("BuildMenu", flags).Invoke(null, new object[] { null, null });
                    Application.DoEvents();
                    if (!child.WaitForExit(10000)) { child.Kill(); throw new Exception("Silent helper did not exit"); }
                    Thread.Sleep(200);
                    if (Contains(watcher, child.Id)) throw new Exception("Expired session remained in snapshot");
                    log.WriteLine("PASS: quiet grace, active silence, parent grouping, clickable owner, unchanged history, lazy loading, rebuild/disposal, session removal");
                    return 0;
                }
            }
            finally { ((IDisposable)watcher).Dispose(); }
        }
    }
    static ToolStripMenuItem Find(ContextMenuStrip context)
    {
        foreach (ToolStripItem item in context.Items)
            if (item.Text == "Other audio sessions") return (ToolStripMenuItem)item;
        throw new Exception("Submenu missing");
    }
    static bool Contains(object watcher, int pid)
    {
        object task = Call(watcher, "OtherSessionsAsync");
        if (!((Task)task).Wait(2000)) throw new Exception("Snapshot timed out");
        foreach (object item in (IEnumerable)task.GetType().GetProperty("Result").GetValue(task, null))
            if ((int)item.GetType().GetField("Pid").GetValue(item) == pid &&
                (bool)item.GetType().GetField("Active").GetValue(item)) return true;
        return false;
    }
}

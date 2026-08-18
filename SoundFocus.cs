// SoundFocus - global hotkey that jumps to the window of whatever app is making sound.
// Press again within the cycle timeout to move to the next noisy app.
//
// Build: build.ps1   Run: SoundFocus.exe [--hotkey alt+shift+d] [--list]
using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace SoundFocus
{
    #region Core Audio COM

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice(string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notify);
        [PreserveSig] int RegisterDuckNotification(string sessionId, IntPtr notify);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notify);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    class VirtualDesktopManagerComObject { }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
    }

    #endregion

    class Noisy
    {
        public int Pid;
        public string Name;
        public long FirstSeen;   // ordering key, so the cycle order stays stable
        public long LastAudible;
    }

    // Background poller: walks the audio sessions of every active render endpoint and
    // records which processes actually pushed non-silent samples recently.
    class AudioWatcher
    {
        const int RENDER = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;
        const int SESSION_ACTIVE = 1, SESSION_EXPIRED = 2;

        readonly object gate = new object();
        readonly Dictionary<int, Noisy> noisy = new Dictionary<int, Noisy>();
        readonly List<IAudioSessionControl> sessions = new List<IAudioSessionControl>();

        public float Threshold = 0.0002f;
        public int MemoryMs = 5000;      // how long an app stays "recently noisy"
        public int PollMs = 150;
        public int RescanMs = 2000;

        long lastScan;
        int ticks;

        List<IAudioSessionManager2> managers;   // one per active render endpoint
        long lastDeviceScan;
        const int DeviceRescanMs = 15000;

        public void Start()
        {
            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void Loop()
        {
            while (true)
            {
                try { Tick(); }
                catch { lastScan = 0; }
                if (++ticks % 5 == 0) Smtc.Refresh();   // now-playing titles, ~every 750 ms
                Thread.Sleep(PollMs);
            }
        }

        public void Tick()
        {
            long now = Environment.TickCount;
            if (lastScan == 0 || now - lastScan > RescanMs)
            {
                RescanSessions();
                lastScan = now;
            }

            foreach (IAudioSessionControl s in sessions)
            {
                try
                {
                    int state;
                    if (s.GetState(out state) != 0) continue;

                    // An expired session means its endpoint went away underneath us -
                    // headphones plugged in, default device switched - and the apps have
                    // moved to an endpoint we are not watching. Re-enumerate immediately
                    // rather than waiting out the endpoint interval.
                    if (state == SESSION_EXPIRED) { managers = null; lastScan = 0; continue; }
                    if (state != SESSION_ACTIVE) continue;

                    IAudioMeterInformation meter = s as IAudioMeterInformation;
                    if (meter == null) continue;
                    float peak;
                    if (meter.GetPeakValue(out peak) != 0 || peak < Threshold) continue;

                    IAudioSessionControl2 c2 = s as IAudioSessionControl2;
                    if (c2 == null) continue;
                    if (c2.IsSystemSoundsSession() == 0) continue;  // S_OK means it IS system sounds
                    int pid;
                    if (c2.GetProcessId(out pid) != 0 || pid <= 0) continue;

                    Remember(pid);
                }
                catch { lastScan = 0; }
            }
        }

        // Diagnostic dump of every session on every active render endpoint.
        public string Dump()
        {
            RescanSessions();
            lastScan = Environment.TickCount;
            StringBuilder sb = new StringBuilder();
            sb.Append(LastScanNote + ", sessions: " + sessions.Count + "\r\n");
            sb.Append(DumpDevices());
            foreach (IAudioSessionControl s in sessions)
            {
                try
                {
                    int state = -1; s.GetState(out state);
                    float peak = -1;
                    IAudioMeterInformation m = s as IAudioMeterInformation;
                    if (m != null) m.GetPeakValue(out peak);
                    IAudioSessionControl2 c2 = s as IAudioSessionControl2;
                    int pid = -1; int sys = -1;
                    string disp = null, sid = null, iid2 = null;
                    if (c2 != null)
                    {
                        c2.GetProcessId(out pid); sys = c2.IsSystemSoundsSession();
                        c2.GetDisplayName(out disp);
                        c2.GetSessionIdentifier(out sid);
                        c2.GetSessionInstanceIdentifier(out iid2);
                    }
                    sb.Append("  state=" + state + " peak=" + peak.ToString("0.00000") +
                              " pid=" + pid + " (" + ProcTree.NameOf(pid) + ")" +
                              " sysSounds=" + sys + " meter=" + (m != null) +
                              "\r\n      display=[" + disp + "]\r\n      sid=[" + sid +
                              "]\r\n      inst=[" + iid2 + "]\r\n");
                }
                catch (Exception ex) { sb.Append("  <error " + ex.Message + ">\r\n"); }
            }
            return sb.ToString();
        }

        void Remember(int pid)
        {
            int owner = ProcTree.OwningWindowProcess(pid);
            long now = Environment.TickCount;
            lock (gate)
            {
                Noisy n;
                if (!noisy.TryGetValue(owner, out n))
                {
                    n = new Noisy();
                    n.Pid = owner;
                    n.Name = ProcTree.NameOf(owner);
                    n.FirstSeen = now;
                    noisy[owner] = n;
                }
                else if (now - n.LastAudible > MemoryMs)
                {
                    n.FirstSeen = now;   // went quiet and came back: treat as fresh
                }
                n.LastAudible = now;
            }
        }

        public string LastScanNote = "";
        public int ActiveDevices = -1;   // -1 until the first scan

        // Every render endpoint in any state (1=active 2=disabled 4=not present 8=unplugged)
        public string DumpDevices()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                IMMDeviceEnumerator de = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                IMMDeviceCollection all;
                int hr = de.EnumAudioEndpoints(RENDER, 15, out all);
                if (hr != 0) return "  endpoint enum failed hr=0x" + hr.ToString("X8") + "\r\n";
                int n;
                all.GetCount(out n);
                sb.Append("  render endpoints (any state): " + n + "\r\n");
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (all.Item(i, out d) != 0) continue;
                    int st; string id;
                    d.GetState(out st);
                    d.GetId(out id);
                    sb.Append("    state=" + st + "  " + id + "\r\n");
                }
            }
            catch (Exception ex) { sb.Append("  <device dump error " + ex.Message + ">\r\n"); }
            return sb.ToString();
        }

        void RescanSessions()
        {
            sessions.Clear();
            ProcTree.Invalidate();

            // Endpoints change when hardware is plugged or unplugged, which is rare, while
            // sessions come and go whenever an app starts playing. Rebuilding the endpoint
            // list and its session managers every couple of seconds was most of this
            // program's idle cost, so only the session list is refreshed that often.
            if (managers == null || Environment.TickCount - lastDeviceScan > DeviceRescanMs)
                if (!RescanDevices()) return;

            foreach (IAudioSessionManager2 mgr in managers)
            {
                try
                {
                    IAudioSessionEnumerator se;
                    if (mgr.GetSessionEnumerator(out se) != 0) continue;
                    int sc;
                    se.GetCount(out sc);
                    for (int j = 0; j < sc; j++)
                    {
                        IAudioSessionControl s;
                        if (se.GetSession(j, out s) == 0 && s != null) sessions.Add(s);
                    }
                }
                catch { managers = null; }   // endpoint went away: re-enumerate next tick
            }
        }

        bool RescanDevices()
        {
            managers = new List<IAudioSessionManager2>();
            lastDeviceScan = Environment.TickCount;

            IMMDeviceEnumerator devEnum = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDeviceCollection devices;
            int hr = devEnum.EnumAudioEndpoints(RENDER, DEVICE_STATE_ACTIVE, out devices);
            if (hr != 0)
            {
                LastScanNote = "EnumAudioEndpoints hr=0x" + hr.ToString("X8");
                managers = null;
                return false;
            }
            int count;
            devices.GetCount(out count);
            ActiveDevices = count;
            LastScanNote = "active render devices: " + count;

            Guid iid = typeof(IAudioSessionManager2).GUID;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    IMMDevice dev;
                    if (devices.Item(i, out dev) != 0) continue;
                    object o;
                    if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != 0) continue;
                    managers.Add((IAudioSessionManager2)o);
                }
                catch { }
            }
            return true;
        }

        // Processes audible within MemoryMs, oldest-first, so the cycle order is stable.
        public List<Noisy> Current()
        {
            long now = Environment.TickCount;
            List<Noisy> result = new List<Noisy>();
            lock (gate)
            {
                List<int> dead = new List<int>();
                foreach (KeyValuePair<int, Noisy> kv in noisy)
                {
                    if (now - kv.Value.LastAudible <= MemoryMs) result.Add(kv.Value);
                    else if (now - kv.Value.LastAudible > 60000) dead.Add(kv.Key);
                }
                foreach (int d in dead) noisy.Remove(d);
            }
            result.Sort(delegate(Noisy a, Noisy b) { return a.FirstSeen.CompareTo(b.FirstSeen); });
            return result;
        }
    }

    #region Process tree / window helpers

    static class ProcTree
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PROCESSENTRY32
        {
            public int dwSize; public int cntUsage; public int th32ProcessID;
            public IntPtr th32DefaultHeapID; public int th32ModuleID; public int cntThreads;
            public int th32ParentProcessID; public int pcPriClassBase; public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(int flags, int pid);
        [DllImport("kernel32.dll")] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static Dictionary<int, int> parent;
        static Dictionary<int, string> names;

        public static void Invalidate() { parent = null; names = null; }

        static void Load()
        {
            if (parent != null) return;
            parent = new Dictionary<int, int>();
            names = new Dictionary<int, string>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return;
            try
            {
                PROCESSENTRY32 e = new PROCESSENTRY32();
                e.dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(snap, ref e))
                {
                    do
                    {
                        parent[e.th32ProcessID] = e.th32ParentProcessID;
                        names[e.th32ProcessID] = e.szExeFile;
                    } while (Process32Next(snap, ref e));
                }
            }
            finally { CloseHandle(snap); }
        }

        public static string NameOf(int pid)
        {
            Load();
            string n;
            return names.TryGetValue(pid, out n) ? n : ("pid " + pid);
        }

        // Audio often comes from a helper process with no UI (Chrome/Edge audio service,
        // Electron utility processes). Walk up the parent chain to the nearest ancestor
        // that actually owns a visible window.
        public static int OwningWindowProcess(int pid)
        {
            Load();
            int cur = pid;
            for (int depth = 0; depth < 6; depth++)
            {
                if (Win.FindWindowForPid(cur) != IntPtr.Zero) return cur;
                int p;
                if (!parent.TryGetValue(cur, out p) || p <= 0 || p == cur) break;
                cur = p;
            }
            return pid;
        }
    }

    static class Win
    {
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

        public static void Raise(IntPtr h) { SetForegroundWindow(h); }
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(int a, int b, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
        [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

        const uint GW_OWNER = 4;
        const int DWMWA_CLOAKED = 14;

        // The COM object is apartment-bound and both the poller thread and the UI thread
        // ask about windows, so keep one per thread.
        [ThreadStatic] static IVirtualDesktopManager vdm;
        [ThreadStatic] static bool vdmTried;

        static IVirtualDesktopManager Vdm()
        {
            if (!vdmTried)
            {
                vdmTried = true;
                try { vdm = (IVirtualDesktopManager)new VirtualDesktopManagerComObject(); }
                catch { vdm = null; }
            }
            return vdm;
        }

        // True when the window lives on a virtual desktop other than the active one.
        public static bool OnOtherDesktop(IntPtr h)
        {
            IVirtualDesktopManager m = Vdm();
            if (m == null) return false;
            int onCurrent;
            if (m.IsWindowOnCurrentVirtualDesktop(h, out onCurrent) != 0) return false;
            return onCurrent == 0;
        }

        // Windows on other virtual desktops are DWM-cloaked, and so are suspended UWP
        // ghost windows. Only the latter should be filtered out, so a cloaked window is
        // rejected only when it claims to be on the desktop we are already looking at.
        static bool IsGhost(IntPtr h)
        {
            int cloaked;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0) return false;
            if (cloaked == 0) return false;
            return !OnOtherDesktop(h);
        }

        // All top-level windows of a process, in Z-order (front-most first).
        public static List<IntPtr> WindowsForPid(int pid)
        {
            List<IntPtr> found = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                int wpid;
                GetWindowThreadProcessId(h, out wpid);
                if (wpid != pid) return true;
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if (GetWindowTextLength(h) == 0) return true;
                if (IsGhost(h)) return true;
                found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public static IntPtr FindWindowForPid(int pid)
        {
            List<IntPtr> all = WindowsForPid(pid);
            return all.Count > 0 ? all[0] : IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags;
            public uint time; public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }

        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        const uint KEYEVENTF_KEYUP = 0x2;
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        static bool AnyModifierHeld()
        {
            int[] keys = { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN };
            foreach (int k in keys) if ((GetAsyncKeyState(k) & 0x8000) != 0) return true;
            return false;
        }

        static void Key(List<INPUT> list, int vk, bool up)
        {
            INPUT i = new INPUT();
            i.type = 1; // INPUT_KEYBOARD
            i.ki.wVk = (ushort)vk;
            i.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
            list.Add(i);
        }

        // Types a chord into whatever is focused. Waits for the physical modifiers of the
        // triggering hotkey to be released first - otherwise the user still holding
        // Ctrl+Alt turns an injected Alt+Shift+D into Ctrl+Alt+Shift+D.
        public static bool SendChord(int mods, int vk, IntPtr expectWindow, int expectPid)
        {
            long deadline = Environment.TickCount + 1500;
            while (AnyModifierHeld() && Environment.TickCount < deadline) Thread.Sleep(15);
            if (AnyModifierHeld()) return false;

            // never inject blind: the intended window must really be in front by now
            IntPtr fg = GetForegroundWindow();
            int fgPid;
            GetWindowThreadProcessId(fg, out fgPid);
            if (fg != expectWindow && fgPid != expectPid) return false;

            List<INPUT> seq = new List<INPUT>();
            if ((mods & 0x1) != 0) Key(seq, VK_MENU, false);
            if ((mods & 0x2) != 0) Key(seq, VK_CONTROL, false);
            if ((mods & 0x4) != 0) Key(seq, VK_SHIFT, false);
            Key(seq, vk, false);
            Key(seq, vk, true);
            if ((mods & 0x4) != 0) Key(seq, VK_SHIFT, true);
            if ((mods & 0x2) != 0) Key(seq, VK_CONTROL, true);
            if ((mods & 0x1) != 0) Key(seq, VK_MENU, true);

            INPUT[] arr = seq.ToArray();
            return SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT))) == arr.Length;
        }

        // Blocks briefly until the window is actually foreground (a virtual-desktop
        // switch is not instant), so callers know whether it is safe to type into it.
        public static bool WaitForForeground(IntPtr h, int timeoutMs)
        {
            long deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (GetForegroundWindow() == h) return true;
                Thread.Sleep(25);
            }
            return GetForegroundWindow() == h;
        }

        public static string TitleOf(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static void Focus(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9 /*SW_RESTORE*/);

            int fgPid;
            IntPtr fg = GetForegroundWindow();
            int fgThread = GetWindowThreadProcessId(fg, out fgPid);
            int me = GetCurrentThreadId();
            bool attached = fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(h);
                SetForegroundWindow(h);
            }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }

            // Activating a window on another virtual desktop is what actually switches
            // desktops, but SetForegroundWindow alone often only flashes the taskbar.
            // SwitchToThisWindow does the desktop hop reliably.
            if (GetForegroundWindow() != h)
            {
                SwitchToThisWindow(h, true);
                if (GetForegroundWindow() != h) SetForegroundWindow(h);
            }
        }
    }

    #endregion

    #region Now-playing metadata (SMTC)

    // Windows' media transport layer knows the *title* of what is playing, which is the
    // only thing that can tell two windows of one process apart (a browser routes every
    // tab's audio through one process, so the audio session PID cannot).
    //
    // Reached by reflection: consuming WinRT at compile time needs a Windows SDK winmd,
    // which this machine does not have, but the runtime projection is present.
    static class Smtc
    {
        static object mgr;
        static bool broken;
        static readonly object gate = new object();
        static List<string[]> cache = new List<string[]>();   // { appId, title, artist }

        const string WinMdSuffix = ", Windows.Media.Control, ContentType=WindowsRuntime";

        static object Await(object asyncOp, Type resultType)
        {
            Type ext = Type.GetType("System.WindowsRuntimeSystemExtensions, System.Runtime.WindowsRuntime, " +
                                    "Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
            if (ext == null) return null;
            MethodInfo asTask = null;
            foreach (MethodInfo m in ext.GetMethods())
            {
                if (m.Name != "AsTask") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType.Name == "IAsyncOperation`1") { asTask = m; break; }
            }
            if (asTask == null) return null;
            object task = asTask.MakeGenericMethod(resultType).Invoke(null, new object[] { asyncOp });
            Task t = task as Task;
            if (t == null || !t.Wait(3000)) return null;
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        // Called from the poller thread only.
        public static void Refresh()
        {
            if (broken) return;
            try
            {
                if (mgr == null)
                {
                    Type mgrType = Type.GetType("Windows.Media.Control." +
                                                "GlobalSystemMediaTransportControlsSessionManager" + WinMdSuffix);
                    if (mgrType == null) { broken = true; return; }
                    object op = mgrType.GetMethod("RequestAsync", BindingFlags.Public | BindingFlags.Static)
                                       .Invoke(null, null);
                    mgr = Await(op, mgrType);
                    if (mgr == null) { broken = true; return; }
                }

                Type propsType = Type.GetType("Windows.Media.Control." +
                                              "GlobalSystemMediaTransportControlsSessionMediaProperties" + WinMdSuffix);
                List<string[]> fresh = new List<string[]>();
                object sessions = mgr.GetType().GetMethod("GetSessions").Invoke(mgr, null);
                foreach (object s in (IEnumerable)sessions)
                {
                    try
                    {
                        object info = s.GetType().GetMethod("GetPlaybackInfo").Invoke(s, null);
                        object status = info.GetType().GetProperty("PlaybackStatus").GetValue(info, null);
                        if (status.ToString() != "Playing") continue;

                        string app = (string)s.GetType().GetProperty("SourceAppUserModelId").GetValue(s, null);
                        object props = Await(s.GetType().GetMethod("TryGetMediaPropertiesAsync").Invoke(s, null),
                                             propsType);
                        if (props == null) continue;
                        string title = (string)props.GetType().GetProperty("Title").GetValue(props, null);
                        string artist = (string)props.GetType().GetProperty("Artist").GetValue(props, null);
                        fresh.Add(new string[] { app, title, artist });
                    }
                    catch { }
                }
                lock (gate) cache = fresh;
            }
            catch { mgr = null; }
        }

        public static List<string[]> Current()
        {
            lock (gate) return new List<string[]>(cache);
        }

        // What this exe reports it is playing, if anything.
        public static string TitleFor(string exeName)
        {
            string exe = (exeName ?? "").ToLowerInvariant();
            if (exe.EndsWith(".exe")) exe = exe.Substring(0, exe.Length - 4);
            if (exe.Length == 0) return null;
            foreach (string[] m in Current())
                if ((m[0] ?? "").ToLowerInvariant().IndexOf(exe) >= 0) return m[1];
            return null;
        }

        // Of several windows belonging to one process, the one whose title best matches
        // what that app reports it is playing. IntPtr.Zero when nothing matches well.
        public static IntPtr Pick(List<IntPtr> candidates, string exeName)
        {
            string exe = (exeName ?? "").ToLowerInvariant();
            if (exe.EndsWith(".exe")) exe = exe.Substring(0, exe.Length - 4);
            if (exe.Length == 0) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            double bestScore = 0;
            foreach (string[] m in Current())
            {
                string app = (m[0] ?? "").ToLowerInvariant();
                if (app.IndexOf(exe) < 0) continue;
                foreach (IntPtr h in candidates)
                {
                    string title = Win.TitleOf(h);
                    double score = Math.Max(Score(title, m[1]), Score(title, m[2]) * 0.8);
                    if (score > bestScore) { bestScore = score; best = h; }
                }
            }
            return bestScore >= 0.5 ? best : IntPtr.Zero;
        }

        static readonly char[] Split = { ' ', '-', '–', '—', '|', ':', ',', '.',
                                         '(', ')', '[', ']', '"', '\'', '/' };

        static double Score(string windowTitle, string media)
        {
            if (string.IsNullOrEmpty(media) || string.IsNullOrEmpty(windowTitle)) return 0;
            string w = windowTitle.ToLowerInvariant();
            string m = media.ToLowerInvariant();
            if (w.IndexOf(m) >= 0) return 1.0;

            // partial credit: how much of the media title survives in the window title
            string[] words = m.Split(Split, StringSplitOptions.RemoveEmptyEntries);
            int hit = 0, total = 0;
            foreach (string word in words)
            {
                if (word.Length < 4) continue;
                total++;
                if (w.IndexOf(word) >= 0) hit++;
            }
            return total == 0 ? 0 : (double)hit / total;
        }
    }

    #endregion

    #region Tab hunting (UI Automation)

    // Tabs are not windows, so Win32 cannot reach them - but browsers expose their tab
    // strip through UI Automation, and Firefox gives the playing tab a child button
    // named "Mute tab". That is a direct signal: it does not depend on the tab title,
    // and it works for background tabs, which title matching cannot see.
    static class TabHunter
    {
        // Localised button label on an audible tab. A muted tab says "Unmute tab" and is
        // deliberately not matched - a muted tab is not making any sound.
        public static string[] MuteLabels = { "mute tab" };

        public class Hit
        {
            public IntPtr Window;
            public AutomationElement Tab;
            public string Title;
        }

        // Runs the search on its own MTA thread with a hard timeout: UI Automation talks
        // to another process, and a busy browser must never wedge the hotkey.
        public static Hit Find(List<IntPtr> windows, string mediaTitle, int timeoutMs)
        {
            Hit result = null;
            Thread t = new Thread(delegate()
            {
                try { result = Search(windows, mediaTitle); }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join(timeoutMs);
            return result;
        }

        public static string LastTiming = "";

        // Locating the tab strip means a bounded walk of the window's chrome, ~60 ms per
        // window, and it dominates a scan once the bulk fetch removed the rest. A window
        // keeps its tab strip for its whole life, so remember it - including the fact that
        // a window has none, which is what makes non-browser windows cheap to skip.
        class StripEntry
        {
            public AutomationElement Strip;   // null: this window has no tab strip
            public long Stamp;
        }

        static readonly Dictionary<IntPtr, StripEntry> stripCache = new Dictionary<IntPtr, StripEntry>();
        const int StripTtlMs = 60000;

        static bool TryCachedStrip(IntPtr hwnd, out AutomationElement strip)
        {
            strip = null;
            lock (stripCache)
            {
                StripEntry e;
                if (!stripCache.TryGetValue(hwnd, out e)) return false;
                if (Environment.TickCount - e.Stamp > StripTtlMs) { stripCache.Remove(hwnd); return false; }
                strip = e.Strip;
                return true;
            }
        }

        static void RememberStrip(IntPtr hwnd, AutomationElement strip)
        {
            StripEntry e = new StripEntry();
            e.Strip = strip;
            e.Stamp = Environment.TickCount;
            lock (stripCache)
            {
                if (stripCache.Count > 64) stripCache.Clear();   // closed windows, do not grow forever
                stripCache[hwnd] = e;
            }
        }

        static void ForgetStrip(IntPtr hwnd)
        {
            lock (stripCache) stripCache.Remove(hwnd);
        }

        static Hit Search(List<IntPtr> windows, string mediaTitle)
        {
            Hit byTitle = null;
            StringBuilder timing = new StringBuilder();
            try
            {
                foreach (IntPtr hwnd in windows)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    double t0 = 0;
                    AutomationElement win;
                    try { win = AutomationElement.FromHandle(hwnd); }
                    catch { continue; }
                    if (win == null) continue;

                    AutomationElement strip;
                    bool cachedStrip = TryCachedStrip(hwnd, out strip);
                    if (!cachedStrip)
                    {
                        strip = FindTabStrip(win, 0);
                        RememberStrip(hwnd, strip);
                    }
                    double t1 = sw.Elapsed.TotalMilliseconds;
                    if (strip == null)
                    {
                        timing.Append("      window: strip " + (t1 - t0).ToString("N1") + " ms" +
                                      (cachedStrip ? " (cached)" : "") + ", none\r\n");
                        continue;
                    }

                    // One cross-process call for the whole tab strip: names and control
                    // types of every tab and of their children (the mute button). Walking
                    // it live instead costs a round trip per node, which with a few dozen
                    // tabs is what made opening the menu feel slow.
                    int tabCount = 0;
                    Hit found = null;
                    CacheRequest cr = new CacheRequest();
                    cr.Add(AutomationElement.NameProperty);
                    cr.Add(AutomationElement.ControlTypeProperty);
                    cr.TreeScope = TreeScope.Element | TreeScope.Descendants;
                    cr.TreeFilter = Automation.ControlViewCondition;
                    cr.AutomationElementMode = AutomationElementMode.Full;   // keep it selectable

                    AutomationElement cached;
                    try
                    {
                        using (cr.Activate()) { cached = strip.GetUpdatedCache(cr); }
                    }
                    catch
                    {
                        // stale cached element (window rebuilt its chrome): re-find once
                        ForgetStrip(hwnd);
                        strip = FindTabStrip(win, 0);
                        RememberStrip(hwnd, strip);
                        if (strip == null) continue;
                        using (cr.Activate()) { cached = strip.GetUpdatedCache(cr); }
                    }
                    double t2 = sw.Elapsed.TotalMilliseconds;

                    foreach (AutomationElement tab in cached.CachedChildren)
                    {
                        string name;
                        try
                        {
                            if (tab.Cached.ControlType != ControlType.TabItem) continue;
                            name = tab.Cached.Name;
                        }
                        catch { continue; }
                        tabCount++;

                        if (IsAudibleCached(tab, name))
                        {
                            found = NewHit(hwnd, tab, name);
                            break;
                        }

                        // weaker fallback for browsers that expose no audio indicator
                        if (byTitle == null && !string.IsNullOrEmpty(mediaTitle) &&
                            name != null && name.ToLowerInvariant().IndexOf(mediaTitle.ToLowerInvariant()) >= 0)
                            byTitle = NewHit(hwnd, tab, name);
                    }

                    timing.Append("      window: strip " + (t1 - t0).ToString("N1") + " ms" +
                                  (cachedStrip ? " (cached)" : "") + ", fetch " +
                                  (t2 - t1).ToString("N1") + " ms, " + tabCount + " tabs scanned " +
                                  (sw.Elapsed.TotalMilliseconds - t2).ToString("N1") + " ms\r\n");
                    if (found != null) return found;
                }
            }
            finally { LastTiming = timing.ToString(); }
            return byTitle;
        }

        static Hit NewHit(IntPtr hwnd, AutomationElement tab, string title)
        {
            Hit h = new Hit();
            h.Window = hwnd;
            h.Tab = tab;
            h.Title = CleanTitle(title);
            return h;
        }

        // Chromium appends its own status to the accessible name, e.g.
        //   "Some Video - YouTube - Audio playing - Memory usage - 392 MB"
        // Useful as a signal, noise in a menu. Only trailing known markers are dropped,
        // so a real title containing similar words survives.
        static readonly string[] Suffixes = { "audio playing", "audio muting", "memory usage" };

        static string CleanTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            string[] parts = title.Split(new string[] { " - " }, StringSplitOptions.None);
            int keep = parts.Length;
            for (int i = parts.Length - 1; i > 0; i--)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                bool drop = p.EndsWith(" mb") || p.EndsWith(" kb") || p.EndsWith(" gb");
                foreach (string s in Suffixes) if (p == s) drop = true;
                if (!drop) break;
                keep = i;
            }
            return keep == parts.Length ? title : string.Join(" - ", parts, 0, keep);
        }

        // Reads only from the cache filled by the single bulk fetch: no round trips.
        static bool IsAudibleCached(AutomationElement tab, string tabName)
        {
            try
            {
                // Chromium also spells the state into the tab's own name
                if (tabName != null && tabName.ToLowerInvariant().IndexOf("audio playing") >= 0)
                    return true;

                foreach (AutomationElement c in tab.CachedChildren)
                {
                    string n = c.Cached.Name;
                    if (n == null) continue;
                    n = n.ToLowerInvariant();
                    foreach (string label in MuteLabels)
                        if (n == label) return true;
                    if (n.IndexOf("audio playing") >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        // The tab strip sits near the top of the chrome tree; a bounded walk keeps us out
        // of the page content, whose accessibility tree can be enormous.
        static AutomationElement FindTabStrip(AutomationElement element, int depth)
        {
            if (depth > 6) return null;
            try
            {
                // One call per level with the control type cached, rather than a round
                // trip per sibling through a TreeWalker.
                CacheRequest cr = new CacheRequest();
                cr.Add(AutomationElement.ControlTypeProperty);
                cr.TreeScope = TreeScope.Element;
                cr.AutomationElementMode = AutomationElementMode.Full;

                AutomationElementCollection children;
                using (cr.Activate())
                    children = element.FindAll(TreeScope.Children, Condition.TrueCondition);

                foreach (AutomationElement c in children)
                    if (c.Cached.ControlType == ControlType.Tab) return c;
                foreach (AutomationElement c in children)
                {
                    AutomationElement found = FindTabStrip(c, depth + 1);
                    if (found != null) return found;
                }
            }
            catch { }
            return null;
        }

        public static bool Activate(Hit hit)
        {
            if (hit == null || hit.Tab == null) return false;
            bool ok = false;
            Thread t = new Thread(delegate()
            {
                try
                {
                    object pattern;
                    if (hit.Tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                    {
                        ((SelectionItemPattern)pattern).Select();
                        ok = true;
                    }
                    else if (hit.Tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        ok = true;
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            t.Join(1500);
            return ok;
        }
    }

    #endregion

    class HotkeyWindow : NativeWindow, IDisposable
    {
        // id of the hotkey that fired: 1 = jump to sound, 2 = go back
        public event Action<int> Pressed;
        const int WM_HOTKEY = 0x0312;

        readonly List<int> registered = new List<int>();

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public bool Register(int id, int mods, int vk)
        {
            if (!RegisterHotKey(Handle, id, mods, vk)) return false;
            registered.Add(id);
            return true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && Pressed != null) Pressed((int)m.WParam);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            foreach (int id in registered) UnregisterHotKey(Handle, id);
            DestroyHandle();
        }
    }

    static class Program
    {
        static AudioWatcher watcher;
        static NotifyIcon tray;
        static IntPtr lastFocused = IntPtr.Zero;
        static long lastPress;
        const int CycleResetMs = 4000;   // press again within this window to advance

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        // exe name -> chord to type once that app is focused, e.g. an in-browser
        // "jump to the tab that is playing" extension shortcut
        static readonly Dictionary<string, int[]> sendKeys = new Dictionary<string, int[]>();
        static bool useTabs = true;
        static ContextMenuStrip menu;
        static string hotkeySpec = "";
        static string returnSpec = "";

        // The icon ships inside the exe twice: as a Win32 resource so Explorer shows it,
        // and as an embedded resource read here. The embedded copy keeps all its frames,
        // so the tray gets the real 16px artwork instead of a 32px one squashed down.
        static Icon AppIcon()
        {
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SoundFocus.ico");
                if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            return SystemIcons.Application;
        }

        [STAThread]
        static int Main(string[] argv)
        {
            string hotkey = "alt+shift+d";
            string returnHotkey = "alt+shift+f";
            bool listOnly = false, debug = false, testTab = false, showMenu = false;
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == "--hotkey" && i + 1 < argv.Length) hotkey = argv[++i];
                else if (argv[i] == "--return-hotkey" && i + 1 < argv.Length) returnHotkey = argv[++i];
                else if (argv[i] == "--send" && i + 1 < argv.Length)
                {
                    string spec = argv[++i];
                    int eq = spec.IndexOf('=');
                    int m2, v2;
                    if (eq <= 0 || !ParseHotkey(spec.Substring(eq + 1), out m2, out v2))
                    {
                        MessageBox.Show("Could not parse --send " + spec +
                                        "\nExpected e.g.  --send firefox.exe=alt+shift+d", "SoundFocus");
                        return 2;
                    }
                    sendKeys[spec.Substring(0, eq).ToLowerInvariant()] = new int[] { m2, v2 };
                }
                else if (argv[i] == "--list") listOnly = true;
                else if (argv[i] == "--debug") debug = true;
                else if (argv[i] == "--no-tab") useTabs = false;
                else if (argv[i] == "--test-tab") testTab = true;
                else if (argv[i] == "--menu") showMenu = true;
                else if (argv[i] == "--mute-label" && i + 1 < argv.Length)
                    TabHunter.MuteLabels = new string[] { argv[++i].ToLowerInvariant() };
                else if (argv[i] == "--help" || argv[i] == "-h")
                {
                    MessageBox.Show("SoundFocus [--hotkey alt+shift+d] [--return-hotkey alt+shift+f]\n" +
                        "           [--send exe=chord] [--list] [--menu] [--debug]\n\n" +
                        "Jumps to the window, or browser tab, currently making sound.\n" +
                        "Press the hotkey repeatedly to cycle through all noisy apps.\n\n" +
                        "The return hotkey goes back to where you were before that jump,\n" +
                        "and toggles between the two from then on.\n\n" +
                        "--send types a chord into the app once it is focused, for in-app\n" +
                        "navigation the OS cannot do, e.g.:\n" +
                        "  --send example.exe=ctrl+alt+j",
                        "SoundFocus");
                    return 0;
                }
            }

            watcher = new AudioWatcher();

            if (debug)
            {
                AttachConsole(-1);
                for (int i = 0; i < 6; i++) { Console.Write(watcher.Dump()); Thread.Sleep(250); }
                return 0;
            }

            // exactly what the tray menu would list right now, without opening it
            if (showMenu)
            {
                AttachConsole(-1);
                for (int i = 0; i < 6; i++) { try { watcher.Tick(); } catch { } Thread.Sleep(150); }
                Smtc.Refresh();
                Console.Write("Playing now\r\n");
                Stopwatch sw = Stopwatch.StartNew();
                List<Target> ts = ResolveTargets();
                double cold = sw.Elapsed.TotalMilliseconds;
                if (ts.Count == 0) Console.Write("  " + EmptyReason() + "\r\n");
                foreach (Target t in ts) Console.Write("  " + t.Label + "\r\n");

                sw = Stopwatch.StartNew();
                ResolveTargetsUncached();
                double rescan = sw.Elapsed.TotalMilliseconds;
                string rescanTiming = TabHunter.LastTiming;

                sw = Stopwatch.StartNew();
                ResolveTargets();
                double warm = sw.Elapsed.TotalMilliseconds;

                Console.Write("\r\nresolve: " + cold.ToString("N1") + " ms first scan, " +
                              rescan.ToString("N1") + " ms rescan (tab strips cached), " +
                              warm.ToString("N1") + " ms from target cache\r\n");
                Console.Write(rescanTiming);
                Console.Write("the tray menu reads the target cache, kept warm in the background\r\n");
                return 0;
            }

            // selects the playing tab but deliberately does NOT raise the window,
            // so the tab-activation path can be checked without stealing focus
            if (testTab)
            {
                AttachConsole(-1);
                for (int i = 0; i < 6; i++) { try { watcher.Tick(); } catch { } Thread.Sleep(150); }
                Smtc.Refresh();
                foreach (Noisy n in watcher.Current())
                {
                    List<IntPtr> cands = Win.WindowsForPid(n.Pid);
                    TabHunter.Hit hit = TabHunter.Find(cands, Smtc.TitleFor(n.Name), 3000);
                    if (hit == null) { Console.Write(n.Name + ": no tab found\r\n"); continue; }
                    bool ok = TabHunter.Activate(hit);
                    Console.Write(n.Name + ": activate [" + hit.Title + "] -> " + ok + "\r\n");
                }
                return 0;
            }

            if (listOnly)
            {
                // sample for ~1.5s so short quiet passages do not hide an app
                for (int i = 0; i < 10; i++) { try { watcher.Tick(); } catch { } Thread.Sleep(150); }
                Smtc.Refresh();
                List<Noisy> now = watcher.Current();
                StringBuilder sb = new StringBuilder("Now playing (per Windows media transport):\r\n");
                List<string[]> media = Smtc.Current();
                if (media.Count == 0) sb.Append("  (none reported)\r\n");
                foreach (string[] m in media)
                    sb.Append("  " + m[0] + ": [" + m[1] + "] / [" + m[2] + "]\r\n");

                sb.Append("\r\nMaking sound:\r\n");
                if (now.Count == 0) sb.Append("  " + EmptyReason() + "\r\n");
                foreach (Noisy n in now)
                {
                    List<IntPtr> cands = Win.WindowsForPid(n.Pid);
                    IntPtr pick = cands.Count > 1 ? Smtc.Pick(cands, n.Name) : IntPtr.Zero;
                    sb.Append("  " + n.Name + " (pid " + n.Pid + "), " + cands.Count + " window(s):\r\n");
                    foreach (IntPtr h in cands)
                        sb.Append("    " + (h == pick ? "-> " : "   ") + Win.TitleOf(h) +
                                  (Win.OnOtherDesktop(h) ? "  [other desktop]" : "") + "\r\n");
                    if (cands.Count > 1 && pick == IntPtr.Zero)
                        sb.Append("    (no title match - all of them stay in the cycle)\r\n");

                    TabHunter.Hit hit = TabHunter.Find(cands, Smtc.TitleFor(n.Name), 3000);
                    sb.Append("    tab: " + (hit == null ? "<none found>"
                              : "[" + hit.Title + "] in window [" + Win.TitleOf(hit.Window) + "]") + "\r\n");
                }
                // GUI subsystem app: borrow the launching console so --list is scriptable
                if (AttachConsole(-1)) Console.Write(sb.ToString());
                else MessageBox.Show(sb.ToString(), "SoundFocus --list");
                return 0;
            }

            int mods, vk;
            if (!ParseHotkey(hotkey, out mods, out vk))
            {
                MessageBox.Show("Could not parse hotkey: " + hotkey, "SoundFocus");
                return 2;
            }

            // A --send chord identical to the global hotkey would be caught by our own
            // RegisterHotKey instead of reaching the app: an endless self-trigger loop.
            foreach (KeyValuePair<string, int[]> kv in new Dictionary<string, int[]>(sendKeys))
            {
                if (kv.Value[0] == mods && kv.Value[1] == vk)
                {
                    sendKeys.Remove(kv.Key);
                    MessageBox.Show("Ignoring --send " + kv.Key + "=" + hotkey +
                                    ": it is the global hotkey itself, so it would only " +
                                    "retrigger SoundFocus instead of reaching the app.",
                                    "SoundFocus");
                }
            }

            int backMods, backVk;
            if (!ParseHotkey(returnHotkey, out backMods, out backVk))
            {
                MessageBox.Show("Could not parse --return-hotkey: " + returnHotkey, "SoundFocus");
                return 2;
            }

            HotkeyWindow hk = hkWindow = new HotkeyWindow();
            if (!hk.Register(1, mods, vk))
            {
                MessageBox.Show("Hotkey " + hotkey + " is already taken by another app.", "SoundFocus");
                return 3;
            }
            if (!hk.Register(2, backMods, backVk))
            {
                MessageBox.Show("Return hotkey " + returnHotkey + " is already taken by another app.\n" +
                                "SoundFocus will run without it; pick another with --return-hotkey.",
                                "SoundFocus");
                returnHotkey = "";
            }
            hk.Pressed += delegate(int id)
            {
                if (id == 1) OnHotkey(null, null);
                else if (id == 2) OnReturn();
            };

            watcher.Start();
            StartPrewarm();

            hotkeySpec = hotkey;
            returnSpec = returnHotkey;
            menu = new ContextMenuStrip();
            menu.Opening += BuildMenu;

            tray = new NotifyIcon();
            tray.Icon = AppIcon();
            tray.Text = Trim("SoundFocus - " + hotkey);
            // Deliberately not tray.ContextMenuStrip: see ShowTrayMenu
            tray.MouseUp += delegate(object s, MouseEventArgs me)
            {
                if (me.Button == MouseButtons.Left) OnHotkey(null, null);
                else if (me.Button == MouseButtons.Right) ShowTrayMenu();
            };
            tray.Visible = true;

            Application.Run();

            tray.Visible = false;
            hk.Dispose();
            return 0;
        }

        // One place that decides what is worth jumping to; both the hotkey and the tray
        // menu work off this list, so they can never disagree about what is playing.
        class Target
        {
            public IntPtr Window;
            public Noisy Owner;
            public TabHunter.Hit Tab;

            public string Label
            {
                get
                {
                    string what = Tab != null ? Tab.Title : Win.TitleOf(Window);
                    if (string.IsNullOrEmpty(what)) what = "(untitled window)";
                    if (what.Length > 55) what = what.Substring(0, 54) + "…";
                    return Owner.Name + "  —  " + what;
                }
            }
        }

        static readonly object targetGate = new object();
        static List<Target> targetCache = new List<Target>();
        static long targetStamp;
        const int TargetTtlMs = 2000;
        const int PrewarmMs = 1500;

        // Keeps the target list warm while anything is audible, so the cost is paid in the
        // background instead of in the 200 ms after a right-click. Idle when silent.
        static void StartPrewarm()
        {
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    // Unconditionally, and bypassing the read cache: when nothing is
                    // audible this costs nothing, and it keeps the menu's snapshot at
                    // most one interval old, including the moment audio starts.
                    try { RefreshTargets(); }
                    catch { }
                    Thread.Sleep(PrewarmMs);
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        // Resolving means talking to other processes over UI Automation, which is far too
        // slow to do while a menu is opening. The poller keeps this warm in the background,
        // so opening the menu or hitting the hotkey normally just reads the last result.
        static List<Target> ResolveTargets()
        {
            lock (targetGate)
            {
                if (targetStamp != 0 && Environment.TickCount - targetStamp < TargetTtlMs)
                    return targetCache;
            }
            return RefreshTargets();
        }

        static List<Target> RefreshTargets()
        {
            List<Target> fresh = ResolveTargetsUncached();
            lock (targetGate)
            {
                targetCache = fresh;
                targetStamp = Environment.TickCount;
            }
            return fresh;
        }

        // What the menu uses: whatever is already known, never a scan. Opening a menu must
        // not wait on other processes - a menu that appears half a second after the click
        // reads as a click that did nothing.
        static List<Target> TargetsNow()
        {
            List<Target> snapshot;
            lock (targetGate) snapshot = targetCache;
            return snapshot;
        }

        static List<Target> ResolveTargetsUncached()
        {
            List<Target> targets = new List<Target>();
            foreach (Noisy n in watcher.Current())
            {
                List<IntPtr> cands = Win.WindowsForPid(n.Pid);
                if (cands.Count == 0) continue;

                // Best case: the app has tabs and one of them is flagged as playing.
                // That pins down both the window and the tab, background tabs included.
                TabHunter.Hit hit = null;
                if (useTabs)
                    hit = TabHunter.Find(cands, Smtc.TitleFor(n.Name), 2000);

                if (hit != null)
                {
                    Target t = new Target();
                    t.Window = hit.Window; t.Owner = n; t.Tab = hit;
                    targets.Add(t);
                    continue;
                }

                if (cands.Count > 1)
                {
                    // several windows, no tab signal: let the now-playing title decide
                    IntPtr pick = Smtc.Pick(cands, n.Name);
                    if (pick != IntPtr.Zero)
                    {
                        cands.Clear();
                        cands.Add(pick);
                    }
                    // no match either: keep them all, so the cycle can still reach the right one
                }

                foreach (IntPtr h in cands)
                {
                    Target t = new Target();
                    t.Window = h; t.Owner = n;
                    targets.Add(t);
                }
            }
            return targets;
        }

        static IntPtr returnTo = IntPtr.Zero;
        static string returnTitle = "";

        // Where the user was before SoundFocus moved them. Cycling between noisy windows
        // must not overwrite it: the point is to get back to what they were actually
        // doing, not to the previous thing SoundFocus jumped to.
        static void RememberOrigin()
        {
            IntPtr fg = Win.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            lock (targetGate)
                foreach (Target t in targetCache)
                    if (t.Window == fg) return;

            returnTo = fg;
            returnTitle = Win.TitleOf(fg);
        }

        static void OnReturn()
        {
            if (returnTo == IntPtr.Zero || !Win.IsWindow(returnTo))
            {
                tray.ShowBalloonTip(1500, "SoundFocus", "Nowhere to go back to yet.", ToolTipIcon.Info);
                return;
            }
            IntPtr back = returnTo;
            // Going back makes where we are now the place to come back to, so the same
            // key toggles between the two.
            IntPtr here = Win.GetForegroundWindow();
            Win.Focus(back);
            if (here != IntPtr.Zero && here != back)
            {
                returnTo = here;
                returnTitle = Win.TitleOf(here);
            }
        }

        static void GoTo(Target t)
        {
            RememberOrigin();
            lastFocused = t.Window;

            // select the tab before raising the window, so it comes up already showing it
            bool tabDone = t.Tab != null && TabHunter.Activate(t.Tab);
            Win.Focus(t.Window);

            // only if we could not reach the tab ourselves, hand off to an in-app shortcut
            int[] chord;
            if (!tabDone && sendKeys.TryGetValue((t.Owner.Name ?? "").ToLowerInvariant(), out chord))
            {
                if (Win.WaitForForeground(t.Window, 1200))
                    Win.SendChord(chord[0], chord[1], t.Window, t.Owner.Pid);
            }
        }

        static void OnHotkey(object sender, EventArgs e)
        {
            List<Target> targets = ResolveTargets();
            if (targets.Count == 0)
            {
                tray.ShowBalloonTip(1500, "SoundFocus", watcher.ActiveDevices == 0
                    ? "No active playback device." : "Nothing is making sound.", ToolTipIcon.Info);
                return;
            }

            List<IntPtr> wins = new List<IntPtr>();
            foreach (Target t in targets) wins.Add(t.Window);

            long now = Environment.TickCount;
            int idx = 0;
            int cur = wins.IndexOf(Win.GetForegroundWindow());
            if (cur >= 0)
                idx = (cur + 1) % wins.Count;                       // already on a noisy window: go to next
            else if (now - lastPress < CycleResetMs && wins.Contains(lastFocused))
                idx = (wins.IndexOf(lastFocused) + 1) % wins.Count; // rapid re-press: keep cycling

            lastPress = now;
            GoTo(targets[idx]);

            if (targets.Count > 1)
                tray.Text = Trim("SoundFocus - " + targets[idx].Owner.Name +
                                 " (" + (idx + 1) + "/" + targets.Count + ")");
        }

        // "nothing is playing" and "you have no working speakers" look identical from
        // the outside, and the second one is worth saying out loud.
        static string EmptyReason()
        {
            return watcher.ActiveDevices == 0 ? "(no active playback device)" : "(nothing)";
        }

        // NotifyIcon.Text throws above 63 characters
        static string Trim(string s)
        {
            return s.Length <= 63 ? s : s.Substring(0, 62) + "…";
        }

        static HotkeyWindow hkWindow;

        // A tray menu misbehaves unless its owner is the foreground window: letting
        // NotifyIcon show the menu itself means the first right-click only activates us
        // and no menu appears, and the second one works. Showing it by hand after taking
        // the foreground makes the first click behave like every later one.
        //
        // The dummy message posted afterwards is the documented companion to this: without
        // it the menu can refuse to close when you click away from it.
        static void ShowTrayMenu()
        {
            if (hkWindow != null) Win.Raise(hkWindow.Handle);
            menu.Show(Cursor.Position);
            if (hkWindow != null) Win.PostMessage(hkWindow.Handle, 0x0000 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
        }

        // Rebuilt on every open: the list is only true at the moment it is shown.
        static void BuildMenu(object sender, CancelEventArgs e)
        {
            menu.Items.Clear();

            ToolStripMenuItem header = new ToolStripMenuItem("Playing now");
            header.Enabled = false;
            menu.Items.Add(header);

            List<Target> targets = TargetsNow();
            if (targets.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem(EmptyReason());
                none.Enabled = false;
                menu.Items.Add(none);
            }
            foreach (Target t in targets)
            {
                Target captured = t;
                ToolStripMenuItem item = new ToolStripMenuItem(t.Label,
                    null, delegate(object s2, EventArgs e2) { GoTo(captured); });
                if (t.Tab != null) item.ToolTipText = "Jump to this tab";
                menu.Items.Add(item);
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Jump to sound (" + hotkeySpec + ")",
                null, delegate { OnHotkey(null, null); }));

            if (returnTo != IntPtr.Zero && Win.IsWindow(returnTo))
            {
                string what = returnTitle;
                if (string.IsNullOrEmpty(what)) what = "previous window";
                if (what.Length > 45) what = what.Substring(0, 44) + "…";
                menu.Items.Add(new ToolStripMenuItem(
                    "Back to " + what + (returnSpec.Length > 0 ? "  (" + returnSpec + ")" : ""),
                    null, delegate { OnReturn(); }));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { Application.Exit(); }));
        }

        static bool ParseHotkey(string spec, out int mods, out int vk)
        {
            mods = 0; vk = 0;
            string[] parts = spec.ToLowerInvariant().Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p == "ctrl" || p == "control") mods |= 0x2;
                else if (p == "alt") mods |= 0x1;
                else if (p == "shift") mods |= 0x4;
                else if (p == "win") mods |= 0x8;
                else if (p.Length == 1) vk = char.ToUpperInvariant(p[0]);
                else if (p.StartsWith("f") && p.Length <= 3)
                {
                    int fn;
                    if (!int.TryParse(p.Substring(1), out fn) || fn < 1 || fn > 24) return false;
                    vk = 0x70 + fn - 1;
                }
                else return false;
            }
            return vk != 0;
        }
    }
}

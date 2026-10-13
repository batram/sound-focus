using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Real Core Audio integration probe. Run on a private desktop: the tiny owner window
// lets us verify helper -> parent attribution without affecting the user's focus.
class AudioProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormat
    {
        public ushort Format, Channels;
        public uint Rate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr header, int size);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool PlaySound(string path, IntPtr module, uint flags);

    static void Check(int code) { if (code != 0) throw new Exception("waveOut error " + code); }
    static void Play(int soundMs, int leadMs, int tailMs)
    {
        const int rate = 48000;
        int frames = (soundMs + leadMs + tailMs) * rate / 1000;
        short[] samples = new short[frames];
        for (int i = leadMs * rate / 1000; i < (leadMs + soundMs) * rate / 1000; i++)
            samples[i] = (short)(900 * Math.Sin(2 * Math.PI * 660 * i / rate));
        IntPtr data = Marshal.AllocHGlobal(frames * 2), header = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHeader)));
        IntPtr stream = IntPtr.Zero;
        try
        {
            Marshal.Copy(samples, 0, data, frames);
            WaveFormat format = new WaveFormat { Format = 1, Channels = 1, Rate = rate, BytesPerSecond = rate * 2, BlockAlign = 2, Bits = 16 };
            Check(waveOutOpen(out stream, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0));
            Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)(frames * 2) }, header, false);
            int size = Marshal.SizeOf(typeof(WaveHeader));
            Check(waveOutPrepareHeader(stream, header, size));
            Check(waveOutWrite(stream, header, size));
            Stopwatch timeout = Stopwatch.StartNew();
            while ((((WaveHeader)Marshal.PtrToStructure(header, typeof(WaveHeader))).Flags & 1) == 0)
            {
                if (timeout.ElapsedMilliseconds > soundMs + leadMs + tailMs + 10000) throw new Exception("Playback timed out");
                Thread.Sleep(2);
            }
            Check(waveOutUnprepareHeader(stream, header, size));
        }
        finally
        {
            if (stream != IntPtr.Zero) waveOutClose(stream);
            Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(header);
        }
    }

    static object Invoke(object watcher, string method) { return watcher.GetType().GetMethod(method).Invoke(watcher, null); }
    static DateTime LastObserved(object watcher, int pid)
    {
        foreach (object entry in (IEnumerable)Invoke(watcher, "History"))
            if ((int)entry.GetType().GetField("Pid").GetValue(entry) == pid)
                return (DateTime)entry.GetType().GetField("Observed").GetValue(entry);
        return DateTime.MinValue;
    }
    static bool WaitObserved(object watcher, int pid, DateTime since)
    {
        Stopwatch wait = Stopwatch.StartNew();
        do
        {
            if (LastObserved(watcher, pid) >= since) return true;
            Thread.Sleep(10);
        } while (wait.ElapsedMilliseconds < 1200);
        return false;
    }
    static void Helper(int sound, int lead, int tail)
    {
        using (Process child = Process.Start(new ProcessStartInfo {
            FileName = Assembly.GetExecutingAssembly().Location,
            Arguments = "--play " + sound + " " + lead + " " + tail,
            UseShellExecute = false, CreateNoWindow = true }))
        {
            if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Helper timed out"); }
            if (child.ExitCode != 0) throw new Exception("Helper failed: " + child.ExitCode);
        }
    }
    static void Measure(object watcher, StreamWriter log, string label, int milliseconds)
    {
        using (Process p = Process.GetCurrentProcess())
        {
            p.Refresh(); double before = p.TotalProcessorTime.TotalMilliseconds;
            int handles = p.HandleCount; long memory = p.PrivateMemorySize64;
            int gen0 = GC.CollectionCount(0);
            Thread.Sleep(milliseconds);
            p.Refresh();
            log.WriteLine(label + ": CPUms=" + (p.TotalProcessorTime.TotalMilliseconds - before).ToString("F1") +
                " overMs=" + milliseconds + " handles=" + handles + "->" + p.HandleCount +
                " privateBytes=" + memory + "->" + p.PrivateMemorySize64 + " gen0=" + (GC.CollectionCount(0) - gen0));
        }
    }

    static bool SystemSound(object watcher, string report, out bool powerShell)
    {
        string path = report + ".wav";
        const int frames = 12000, rate = 48000;
        using (BinaryWriter wav = new BinaryWriter(File.Create(path)))
        {
            wav.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wav.Write(36 + frames * 2);
            wav.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wav.Write(16);
            wav.Write((short)1); wav.Write((short)1); wav.Write(rate); wav.Write(rate * 2);
            wav.Write((short)2); wav.Write((short)16);
            wav.Write(System.Text.Encoding.ASCII.GetBytes("data")); wav.Write(frames * 2);
            for (int i = 0; i < frames; i++) wav.Write((short)(900 * Math.Sin(2 * Math.PI * 660 * i / rate)));
        }
        try
        {
            DateTime before = DateTime.Now;
            if (!PlaySound(path, IntPtr.Zero, 0x20000 | 0x200000)) throw new Exception("System sound playback failed");
            bool system = WaitObserved(watcher, 0, before);
            string script = "[System.Media.SoundPlayer]::new('" + path.Replace("'", "''") + "').PlaySync()";
            before = DateTime.Now;
            using (Process child = Process.Start(new ProcessStartInfo {
                FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
                Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }))
            {
                if (!child.WaitForExit(10000)) { child.Kill(); throw new Exception("PowerShell sound timed out"); }
                if (child.ExitCode != 0) throw new Exception("PowerShell sound failed");
            }
            powerShell = WaitObserved(watcher, Process.GetCurrentProcess().Id, before);
            return system;
        }
        finally { File.Delete(path); }
    }

    [STAThread]
    static int Main(string[] argv)
    {
        if (argv.Length > 0 && argv[0] == "--play")
        {
            Play(int.Parse(argv[1]), int.Parse(argv[2]), int.Parse(argv[3])); return 0;
        }
        using (StreamWriter log = new StreamWriter(argv[1]))
        {
            log.AutoFlush = true;
            try
            {
                Assembly assembly = Assembly.LoadFrom(Path.GetFullPath(argv[0]));
                if (argv.Length > 2 && argv[2] == "trace")
                    assembly.GetType("SoundFocus.Log").GetMethod("Open").Invoke(null, new object[] { argv[1] + ".trace" });
                object watcher = Activator.CreateInstance(assembly.GetType("SoundFocus.AudioWatcher"), true);
                bool candidate = watcher is IDisposable;
                using (Form owner = new Form { Text = "SoundFocus audio probe owner" })
                {
                    owner.Show(); Application.DoEvents();
                    Invoke(watcher, "Start"); Thread.Sleep(1200);
                    log.WriteLine("Testing " + argv[0] + " parentPid=" + Process.GetCurrentProcess().Id);
                    Measure(watcher, log, "idle-before", 6000);
                    int pid = Process.GetCurrentProcess().Id;
                    int requiredMisses = 0;
                    foreach (int duration in new int[] { 20, 50, 100, 571 })
                    {
                        int caught = 0;
                        for (int trial = 0; trial < 8; trial++)
                        {
                            Thread.Sleep(47 + trial * 31);
                            DateTime before = DateTime.Now;
                            Helper(duration, 0, 0);
                            if (WaitObserved(watcher, pid, before)) caught++;
                        }
                        log.WriteLine("fresh helper " + duration + "ms: " + caught + "/8");
                        if (duration >= 100) requiredMisses += 8 - caught;
                    }
                    int delayed = 0;
                    for (int trial = 0; trial < 5; trial++)
                    {
                        DateTime before = DateTime.Now;
                        Helper(100, 1300 + trial * 17, 200);
                        if (WaitObserved(watcher, pid, before)) delayed++;
                    }
                    log.WriteLine("already-active stream, 100ms after silence: " + delayed + "/5");
                    requiredMisses += 5 - delayed;
                    DateTime silentBefore = LastObserved(watcher, pid);
                    Helper(0, 1500, 0); Thread.Sleep(200);
                    bool silentPassed = LastObserved(watcher, pid) == silentBefore;
                    log.WriteLine("silent stream excluded: " + silentPassed);
                    // Snapshot must not mutate after later observations.
                    object snapshot = null;
                    foreach (object item in (IEnumerable)Invoke(watcher, "History"))
                        if ((int)item.GetType().GetField("Pid").GetValue(item) == pid) snapshot = item;
                    DateTime frozen = snapshot == null ? DateTime.MinValue : (DateTime)snapshot.GetType().GetField("Observed").GetValue(snapshot);
                    Helper(571, 0, 0); Thread.Sleep(200);
                    bool immutable = snapshot != null && (DateTime)snapshot.GetType().GetField("Observed").GetValue(snapshot) == frozen;
                    log.WriteLine("history snapshot immutable: " + immutable);
                    Thread.Sleep(5500);
                    bool expired = true;
                    foreach (object item in (IEnumerable)Invoke(watcher, "Current"))
                        if ((int)item.GetType().GetField("Pid").GetValue(item) == pid) expired = false;
                    log.WriteLine("quiet owner expires from current: " + expired);
                    if (!expired) requiredMisses++;
                    DateTime resumedAt = DateTime.Now;
                    Helper(100, 0, 0);
                    bool resumed = WaitObserved(watcher, pid, resumedAt);
                    log.WriteLine("owner resumes after expiry: " + resumed);
                    if (!resumed) requiredMisses++;
                    if (candidate) log.WriteLine(Invoke(watcher, "Dump"));
                    Measure(watcher, log, "idle-after", 6000);
                    if (candidate)
                    {
                        bool powerShell;
                        bool systemSound = SystemSound(watcher, argv[1], out powerShell);
                        log.WriteLine("system sound retained: " + systemSound);
                        if (!systemSound) requiredMisses++;
                        log.WriteLine("PowerShell SoundPlayer helper attributed to owner: " + powerShell);
                        if (!powerShell) requiredMisses++;
                        for (int batch = 0; batch < 2; batch++)
                        {
                            for (int i = 0; i < 40; i++) Helper(0, 10, 0);
                            Thread.Sleep(500);
                            log.WriteLine("churn batch=" + batch);
                            log.WriteLine(Invoke(watcher, "Dump"));
                            Measure(watcher, log, "post-churn", 2000);
                        }
                        log.WriteLine(Invoke(watcher, "Dump"));
                        ((IDisposable)watcher).Dispose();
                        ((IDisposable)watcher).Dispose();
                        log.WriteLine("shutdown + repeated dispose: passed");
                    }
                    log.WriteLine("RESULT requiredMisses=" + requiredMisses + " silent=" + silentPassed + " immutable=" + immutable);
                    return candidate && (requiredMisses != 0 || !silentPassed || !immutable) ? 1 : 0;
                }
            }
            catch (Exception ex) { log.WriteLine(ex); return 2; }
        }
    }
}

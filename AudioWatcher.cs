using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SoundFocus
{
    // Callback interfaces are implemented by managed objects, not imported COM objects.
    [ComVisible(true), Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(IntPtr session);
    }

    [ComVisible(true), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged(IntPtr name, IntPtr context);
        [PreserveSig] int OnIconPathChanged(IntPtr path, IntPtr context);
        [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
        [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, IntPtr context);
        [PreserveSig] int OnGroupingParamChanged(IntPtr group, IntPtr context);
        [PreserveSig] int OnStateChanged(int state);
        [PreserveSig] int OnSessionDisconnected(int reason);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioPropertyKey { public Guid Format; public uint Id; }

    [ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioDeviceNotification
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, AudioPropertyKey key);
    }

    static class AudioClock
    {
        public static long Now { get { return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency; } }
    }

    // One MTA owns every audio RCW, registration and release. Callbacks only enqueue;
    // process/window metadata has a separate worker and never delays meter readings.
    class AudioWatcher : IDisposable
    {
        public float Threshold = 0.0002f;
        public int MemoryMs = 5000, PollMs = 150, QuietPollMs = 50, BurstPollMs = 25;
        public int BurstMs = 750, RescanMs = 30000;
        public string LastScanNote = "starting";
        public int ActiveDevices = -1;
        public event Action Changed;

        sealed class Endpoint
        {
            public string Id;
            public IAudioSessionManager2 Manager;
            public CreatedSink Sink;
            public IntPtr SinkPointer;
        }
        sealed class Session
        {
            public string Key;
            public Endpoint Endpoint;
            public IAudioSessionControl Control;
            public IAudioMeterInformation Meter;
            public StateSink Sink;
            public IntPtr SinkPointer;
            public int Pid, State;
            public bool System, Shared, Removed, MetadataQueued;
            public long BurstUntil, NextSample, LastHeard, ObservedTicks;
            public int TraceSamples;
            public List<ProcessIdentity> Chain;
            public Observation Owner;
        }
        sealed class Observation
        {
            public int Pid;
            public string Name, Title, TabTitle;
            public DateTime Started, Observed;
            public long FirstSeen, LastAudible;
            public bool System, InCurrent;
        }
        sealed class Notice
        {
            public int Kind, State;
            public Endpoint Endpoint;
            public Session Session;
            public IntPtr Pointer;
            public long At;
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class CreatedSink : IAudioSessionNotification
        {
            readonly AudioWatcher owner;
            readonly Endpoint endpoint;
            internal CreatedSink(AudioWatcher owner, object endpoint) { this.owner = owner; this.endpoint = (Endpoint)endpoint; }
            public int OnSessionCreated(IntPtr session)
            {
                // Raw pointer ownership is explicit: one AddRef here, one Release on
                // the worker even for a stale endpoint, duplicate or shutdown race.
                Interlocked.Increment(ref owner.callbacks);
                try
                {
                    if (Volatile.Read(ref owner.accepting) == 0) return 0;
                    Marshal.AddRef(session);
                    owner.notices.Enqueue(new Notice { Kind = 1, Endpoint = endpoint, Pointer = session, At = AudioClock.Now });
                    owner.wake.Set();
                }
                catch { Interlocked.Increment(ref owner.errors); }
                finally { Interlocked.Decrement(ref owner.callbacks); }
                return 0;
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class StateSink : IAudioSessionEvents
        {
            readonly AudioWatcher owner;
            readonly Session session;
            internal StateSink(AudioWatcher owner, object session) { this.owner = owner; this.session = (Session)session; }
            public int OnStateChanged(int state) { owner.Post(new Notice { Kind = 2, Session = session, State = state, At = AudioClock.Now }); return 0; }
            public int OnSessionDisconnected(int reason) { owner.Post(new Notice { Kind = 3, Session = session }); return 0; }
            public int OnDisplayNameChanged(IntPtr n, IntPtr c) { return 0; }
            public int OnIconPathChanged(IntPtr n, IntPtr c) { return 0; }
            public int OnSimpleVolumeChanged(float v, bool m, IntPtr c) { return 0; }
            public int OnChannelVolumeChanged(uint n, IntPtr v, uint ch, IntPtr c) { return 0; }
            public int OnGroupingParamChanged(IntPtr g, IntPtr c) { return 0; }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class DeviceSink : IAudioDeviceNotification
        {
            readonly AudioWatcher owner;
            internal DeviceSink(AudioWatcher owner) { this.owner = owner; }
            void Dirty() { owner.Post(new Notice { Kind = 4 }); }
            public int OnDeviceStateChanged(string id, uint s) { Dirty(); return 0; }
            public int OnDeviceAdded(string id) { Dirty(); return 0; }
            public int OnDeviceRemoved(string id) { Dirty(); return 0; }
            public int OnDefaultDeviceChanged(int f, int r, string id) { if (f == 0) Dirty(); return 0; }
            public int OnPropertyValueChanged(string id, AudioPropertyKey k) { return 0; }
        }

        readonly object gate = new object();
        readonly Dictionary<string, Endpoint> endpoints = new Dictionary<string, Endpoint>();
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly List<Observation> observations = new List<Observation>();
        readonly List<Observation> history = new List<Observation>();
        readonly ConcurrentQueue<Notice> notices = new ConcurrentQueue<Notice>();
        readonly ConcurrentQueue<Session> metadata = new ConcurrentQueue<Session>();
        readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        readonly AutoResetEvent wake = new AutoResetEvent(false), metadataWake = new AutoResetEvent(false);
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        Thread worker, metadataWorker;
        IMMDeviceEnumerator enumerator;
        DeviceSink deviceSink;
        IntPtr deviceSinkPointer;
        volatile bool stopping;
        int accepting = 1, callbacks, disposed;
        bool devicesDirty;
        long nextReconcile, peakReads, wakes, discoveries, retired, deviceScans, errors;

        public void Start()
        {
            if (worker != null) return;
            metadataWorker = new Thread(MetadataLoop) { IsBackground = true, Name = "SoundFocus metadata" };
            metadataWorker.SetApartmentState(ApartmentState.MTA);
            worker = new Thread(Loop) { IsBackground = true, Name = "SoundFocus audio" };
            worker.SetApartmentState(ApartmentState.MTA);
            metadataWorker.Start();
            worker.Start();
        }

        public bool WaitReady(int milliseconds) { return ready.WaitOne(milliseconds); }

        void Post(Notice notice)
        {
            Interlocked.Increment(ref callbacks);
            try
            {
                if (Volatile.Read(ref accepting) == 0) return;
                notices.Enqueue(notice);
                wake.Set();
            }
            catch { Interlocked.Increment(ref errors); }
            finally { Interlocked.Decrement(ref callbacks); }
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        // Release precisely one acquired RCW reference. FinalRelease would invalidate
        // the retained session when enumeration and a callback report the same RCW.
        static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        void Failure(string operation, Exception ex)
        {
            Interlocked.Increment(ref errors);
            LastScanNote = operation + ": " + ex.Message;
            Log.Write("audio " + LastScanNote);
        }

        void Loop()
        {
            try
            {
                while (!stopping)
                {
                    try
                    {
                        if (enumerator == null) Initialize();
                        DrainNotices();
                        long now = AudioClock.Now;
                        if (devicesDirty || now >= nextReconcile)
                        {
                            devicesDirty = false;
                            Reconcile();
                            nextReconcile = AudioClock.Now + RescanMs;
                        }
                        now = AudioClock.Now;
                        // No registry snapshots or window queries per meter reading.
                        Sample(now);
                        ExpireCurrent(now);
                        Action command;
                        while (commands.TryDequeue(out command)) command();
                        ready.Set();
                    }
                    catch (Exception ex)
                    {
                        Failure("watcher", ex);
                        ready.Set();
                        // Bound retries after a service failure; callbacks can wake sooner.
                        wake.WaitOne(1000);
                    }
                    if (!stopping)
                    {
                        wake.WaitOne(NextWait());
                        Interlocked.Increment(ref wakes);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref accepting, 0);
                while (Volatile.Read(ref callbacks) != 0) Thread.Yield();
                foreach (Session session in new List<Session>(sessions.Values)) RemoveSession(session);
                foreach (Endpoint endpoint in new List<Endpoint>(endpoints.Values)) RemoveEndpoint(endpoint);
                if (deviceSinkPointer != IntPtr.Zero)
                {
                    try { enumerator.UnregisterEndpointNotificationCallback(deviceSinkPointer); } catch { }
                    Marshal.Release(deviceSinkPointer);
                    deviceSinkPointer = IntPtr.Zero;
                }
                Release(enumerator); enumerator = null;
                Notice pending;
                while (notices.TryDequeue(out pending)) if (pending.Pointer != IntPtr.Zero) Marshal.Release(pending.Pointer);
                ready.Set();
            }
        }

        void Initialize()
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            deviceSink = new DeviceSink(this);
            deviceSinkPointer = Marshal.GetComInterfaceForObject(deviceSink, typeof(IAudioDeviceNotification));
            int hr = enumerator.RegisterEndpointNotificationCallback(deviceSinkPointer);
            if (hr < 0) Failure("device subscription (reconciliation fallback)", Marshal.GetExceptionForHR(hr));
            devicesDirty = true;
        }

        void Reconcile()
        {
            Interlocked.Increment(ref deviceScans);
            IMMDeviceCollection devices = null;
            HashSet<string> found = new HashSet<string>();
            try
            {
                Check(enumerator.EnumAudioEndpoints(0, 1, out devices));
                int count; Check(devices.GetCount(out count));
                ActiveDevices = count;
                for (int i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    Endpoint endpoint = null;
                    try
                    {
                        Check(devices.Item(i, out device));
                        string id; Check(device.GetId(out id)); found.Add(id);
                        if (!endpoints.TryGetValue(id, out endpoint))
                        {
                            object manager; Guid iid = typeof(IAudioSessionManager2).GUID;
                            Check(device.Activate(ref iid, 23, IntPtr.Zero, out manager));
                            endpoint = new Endpoint { Id = id, Manager = (IAudioSessionManager2)manager };
                            endpoints.Add(id, endpoint);
                        }
                        if (endpoint.SinkPointer == IntPtr.Zero)
                        {
                            endpoint.Sink = new CreatedSink(this, endpoint);
                            IntPtr pointer = Marshal.GetComInterfaceForObject(endpoint.Sink, typeof(IAudioSessionNotification));
                            int hr = endpoint.Manager.RegisterSessionNotification(pointer);
                            if (hr >= 0) endpoint.SinkPointer = pointer;
                            else { Marshal.Release(pointer); Failure("session subscription (reconciliation fallback)", Marshal.GetExceptionForHR(hr)); }
                        }
                        Enumerate(endpoint);
                    }
                    catch (Exception ex)
                    {
                        Failure("endpoint", ex);
                        // A dead manager cannot repair itself during reconciliation.
                        // Retire it so the next pass obtains a fresh service connection.
                        if (endpoint != null) RemoveEndpoint(endpoint);
                    }
                    finally { Release(device); }
                }
                foreach (Endpoint endpoint in new List<Endpoint>(endpoints.Values))
                    if (!found.Contains(endpoint.Id)) RemoveEndpoint(endpoint);
                LastScanNote = "active render devices: " + count;
            }
            finally { Release(devices); }
        }

        void Enumerate(Endpoint endpoint)
        {
            IAudioSessionEnumerator list = null;
            try
            {
                Check(endpoint.Manager.GetSessionEnumerator(out list));
                int count; Check(list.GetCount(out count)); // Arms OnSessionCreated.
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl control;
                    Check(list.GetSession(i, out control));
                    AddSession(endpoint, control);
                }
                // Reconcile states too, recovering a failed/missed state subscription.
                foreach (Session session in new List<Session>(sessions.Values))
                    if (session.Endpoint == endpoint)
                    {
                        int state;
                        if (session.Control.GetState(out state) >= 0) SetState(session, state, AudioClock.Now);
                    }
            }
            finally { Release(list); }
        }

        void AddSession(Endpoint endpoint, IAudioSessionControl control)
        {
            Session session = null;
            bool retained = false;
            try
            {
                IAudioSessionControl2 details = control as IAudioSessionControl2;
                if (details == null) return;
                string id; Check(details.GetSessionInstanceIdentifier(out id));
                string key = endpoint.Id + "\n" + id;
                if (sessions.ContainsKey(key)) return;
                int state; Check(control.GetState(out state));
                if (state == 2) return;
                bool system = details.IsSystemSoundsSession() == 0;
                int pid; int hr = details.GetProcessId(out pid);
                Check(hr); // AUDCLNT_S_NO_SINGLE_PROCESS is success with a creator PID.
                if (!system && pid <= 0) return;
                session = new Session { Key = key, Endpoint = endpoint, Control = control,
                    Meter = control as IAudioMeterInformation, Pid = system ? 0 : pid, System = system,
                    Shared = hr != 0, State = state, BurstUntil = AudioClock.Now + BurstMs };
                session.Sink = new StateSink(this, session);
                IntPtr pointer = Marshal.GetComInterfaceForObject(session.Sink, typeof(IAudioSessionEvents));
                hr = control.RegisterAudioSessionNotification(pointer);
                if (hr >= 0) session.SinkPointer = pointer;
                else { Marshal.Release(pointer); Failure("state subscription (polling fallback)", Marshal.GetExceptionForHR(hr)); }
                // Subscribe before identity capture. Targeted native process queries avoid
                // a whole-machine snapshot and System.Diagnostics.Process enumeration.
                session.Chain = system ? new List<ProcessIdentity>() : ProcessIdentity.Capture(pid);
                sessions.Add(key, session); retained = true;
                Interlocked.Increment(ref discoveries);
                if (Log.Enabled) Log.Write("audio session pid=" + session.Pid + " state=" + state + " chain=" + session.Chain.Count + " at=" + AudioClock.Now);
                // Subscribe before rereading state: streams can start during setup.
                Check(control.GetState(out state));
                SetState(session, state, AudioClock.Now);
            }
            catch (Exception ex) { Failure("new session", ex); if (retained) RemoveSession(session); }
            finally { if (!retained) Release(control); }
        }

        void DrainNotices()
        {
            Notice notice;
            while (notices.TryDequeue(out notice))
            {
                try
                {
                    if (notice.Kind == 1)
                    {
                        if (Log.Enabled) Log.Write("audio created callback lag=" + (AudioClock.Now - notice.At));
                        Endpoint current;
                        if (endpoints.TryGetValue(notice.Endpoint.Id, out current) && current == notice.Endpoint)
                            AddSession(current, (IAudioSessionControl)Marshal.GetObjectForIUnknown(notice.Pointer));
                    }
                    else if (notice.Kind == 2 && !notice.Session.Removed) { if (Log.Enabled) Log.Write("audio state pid=" + notice.Session.Pid + " state=" + notice.State + " lag=" + (AudioClock.Now - notice.At)); SetState(notice.Session, notice.State, notice.At); }
                    else if (notice.Kind == 3 && !notice.Session.Removed) { RemoveSession(notice.Session); devicesDirty = true; }
                    else if (notice.Kind == 4) devicesDirty = true;
                }
                catch (Exception ex) { Failure("callback processing", ex); }
                finally { if (notice.Pointer != IntPtr.Zero) Marshal.Release(notice.Pointer); }
            }
        }

        void SetState(Session session, int state, long at)
        {
            if (state == 2) { RemoveSession(session); return; } // Normal stream teardown, not a device change.
            if (session.State != state)
            {
                // A short burst may stop before the next timer tick. Read the last
                // device period promptly on stop too; state alone never proves sound.
                if (session.State == 1 && state == 0 && session.Meter != null)
                {
                    try { ReadPeak(session, AudioClock.Now); }
                    catch (Exception ex) { Failure("final peak", ex); }
                }
                session.State = state;
                session.NextSample = 0;
                if (state == 1) session.BurstUntil = at + BurstMs;
            }
        }

        void RemoveSession(Session session)
        {
            if (session.Removed) return;
            session.Removed = true;
            sessions.Remove(session.Key);
            if (session.SinkPointer != IntPtr.Zero)
            {
                try { session.Control.UnregisterAudioSessionNotification(session.SinkPointer); } catch { }
                Marshal.Release(session.SinkPointer); session.SinkPointer = IntPtr.Zero;
            }
            Release(session.Control); session.Control = null; session.Meter = null;
            Interlocked.Increment(ref retired);
        }

        void RemoveEndpoint(Endpoint endpoint)
        {
            foreach (Session session in new List<Session>(sessions.Values))
                if (session.Endpoint == endpoint) RemoveSession(session);
            endpoints.Remove(endpoint.Id);
            if (endpoint.SinkPointer != IntPtr.Zero)
            {
                try { endpoint.Manager.UnregisterSessionNotification(endpoint.SinkPointer); } catch { }
                Marshal.Release(endpoint.SinkPointer); endpoint.SinkPointer = IntPtr.Zero;
            }
            Release(endpoint.Manager);
        }

        void Sample(long now)
        {
            // Removal is deferred until after enumeration. This list is allocated only
            // when an actual failure occurs, not on the 25/50/150ms fast path.
            List<Session> failed = null;
            foreach (Session session in sessions.Values)
            {
                if (now < session.NextSample) continue;
                try
                {
                    if (session.SinkPointer == IntPtr.Zero)
                    {
                        int state; Check(session.Control.GetState(out state));
                        if (state == 2) { if (failed == null) failed = new List<Session>(); failed.Add(session); continue; }
                        session.State = state;
                    }
                    session.NextSample = now + QuietPollMs;
                    if (session.State != 1 || session.Meter == null) continue;
                    ReadPeak(session, now);
                    session.NextSample = now + (now < session.BurstUntil ? BurstPollMs :
                        session.LastHeard != 0 && now - session.LastHeard < 1000 ? PollMs : QuietPollMs);
                }
                catch (Exception ex)
                {
                    Failure("meter", ex);
                    if (failed == null) failed = new List<Session>(); failed.Add(session);
                    devicesDirty = true;
                }
            }
            if (failed != null) foreach (Session session in failed) RemoveSession(session);
        }

        void ReadPeak(Session session, long now)
        {
            float peak; Check(session.Meter.GetPeakValue(out peak));
            if (Log.Enabled && session.TraceSamples++ < 8) Log.Write("audio peak pid=" + session.Pid + " peak=" + peak);
            Interlocked.Increment(ref peakReads);
            if (peak < Threshold) return;
            lock (gate)
            {
                session.LastHeard = now;
                session.ObservedTicks = DateTime.Now.Ticks;
                if (session.Owner != null) Remember(session.Owner, now, new DateTime(session.ObservedTicks));
                else if (!session.MetadataQueued)
                {
                    session.MetadataQueued = true;
                    metadata.Enqueue(session); metadataWake.Set();
                }
            }
        }

        int NextWait()
        {
            long now = AudioClock.Now, deadline = nextReconcile;
            foreach (Session session in sessions.Values)
                if (session.State == 1 || session.SinkPointer == IntPtr.Zero) deadline = Math.Min(deadline, session.NextSample);
            lock (gate)
                foreach (Observation item in observations)
                    if (item.InCurrent) deadline = Math.Min(deadline, item.LastAudible + MemoryMs + 1);
            if (devicesDirty || !notices.IsEmpty || !commands.IsEmpty) return 0;
            return (int)Math.Max(1, Math.Min(30000, deadline - now));
        }

        void MetadataLoop()
        {
            while (!stopping || !metadata.IsEmpty)
            {
                Session session;
                while (metadata.TryDequeue(out session))
                {
                    try
                    {
                        ProcessIdentity identity = null;
                        string title = "";
                        foreach (ProcessIdentity candidate in session.Chain)
                        {
                            if (!candidate.IsRunning()) continue;
                            List<IntPtr> windows = Win.WindowsForPid(candidate.Pid);
                            if (windows.Count == 0) continue;
                            identity = candidate; title = Win.TitleOf(windows[0]); break;
                        }
                        if (identity == null && session.Chain.Count > 0) identity = session.Chain[0];
                        int pid = session.System ? 0 : identity == null ? session.Pid : identity.Pid;
                        DateTime started = identity == null ? DateTime.MinValue : identity.Started;
                        lock (gate)
                        {
                            Observation owner = observations.Find(delegate(Observation item) { return item.Pid == pid && item.Started == started; });
                            if (owner == null)
                            {
                                owner = new Observation { Pid = pid, Started = started, System = session.System,
                                    Name = session.System ? "System sounds" : identity == null ? "pid " + pid : identity.Name,
                                    Title = title };
                                observations.Add(owner);
                            }
                            session.Owner = owner;
                            Remember(owner, session.LastHeard, new DateTime(session.ObservedTicks));
                        }
                        Log.Write("audio observed source=" + session.Pid + " owner=" + pid + " shared=" + session.Shared + " system=" + session.System);
                    }
                    catch (Exception ex)
                    {
                        Failure("metadata", ex);
                        lock (gate) session.MetadataQueued = false;
                    }
                }
                if (!stopping) metadataWake.WaitOne();
            }
        }

        // gate held. Mutable private records; allocations occur only for UI snapshots.
        void Remember(Observation owner, long now, DateTime observed)
        {
            if (now < owner.LastAudible) return;
            bool changed = !owner.InCurrent || now - owner.LastAudible > MemoryMs;
            if (changed) owner.FirstSeen = now;
            owner.InCurrent = true; owner.LastAudible = now; owner.Observed = observed;
            history.Remove(owner);
            int index = -1;
            for (int i = 0; i < history.Count; i++) if (history[i].LastAudible < now) { index = i; break; }
            history.Insert(index < 0 ? history.Count : index, owner);
            if (history.Count > 5) history.RemoveAt(5);
            if (changed)
            {
                SignalChanged();
                // Metadata may publish after the source has already stopped and the
                // audio worker went to sleep. Arm its five-second expiry deadline.
                wake.Set();
            }
        }

        void SignalChanged()
        {
            Action changed = Changed;
            if (changed != null) changed(); // Subscribers only signal an event.
        }

        void ExpireCurrent(long now)
        {
            lock (gate)
            {
                bool changed = false;
                foreach (Observation item in observations)
                    if (item.InCurrent && now - item.LastAudible > MemoryMs) { item.InCurrent = false; changed = true; }
                // Keep owners referenced by living sessions: otherwise a long silent
                // stream could resume into an observation absent from the current list.
                for (int i = observations.Count - 1; i >= 0; i--)
                {
                    Observation item = observations[i];
                    if (item.InCurrent || history.Contains(item)) continue;
                    bool retained = false;
                    foreach (Session session in sessions.Values) if (session.Owner == item) { retained = true; break; }
                    if (!retained) observations.RemoveAt(i);
                }
                if (changed) SignalChanged();
            }
        }

        public List<Noisy> Current()
        {
            List<Noisy> result = new List<Noisy>(); long now = AudioClock.Now;
            lock (gate)
                foreach (Observation item in observations)
                    if (!item.System && now - item.LastAudible <= MemoryMs)
                        result.Add(new Noisy { Pid = item.Pid, Name = item.Name, Started = item.Started,
                            FirstSeen = item.FirstSeen, LastAudible = item.LastAudible });
            result.Sort(delegate(Noisy a, Noisy b) { return a.FirstSeen.CompareTo(b.FirstSeen); });
            return result;
        }

        public List<SoundHistoryEntry> History()
        {
            List<SoundHistoryEntry> result = new List<SoundHistoryEntry>();
            lock (gate)
                foreach (Observation item in history)
                    result.Add(new SoundHistoryEntry { Pid = item.Pid, Name = item.Name, Started = item.Started,
                        Observed = item.Observed, Title = item.Title, TabTitle = item.TabTitle });
            return result;
        }

        public void RememberTab(int pid, string title)
        {
            if (string.IsNullOrEmpty(title)) return;
            lock (gate)
            {
                Observation item = observations.FindLast(delegate(Observation o) { return o.Pid == pid && o.InCurrent; });
                if (item != null) item.TabTitle = title;
            }
        }

        // Called by the slower UI metadata worker; no window calls on the audio worker.
        public void RefreshTitles()
        {
            foreach (SoundHistoryEntry entry in History())
            {
                if (!entry.IsRunning()) continue;
                List<IntPtr> windows = Win.WindowsForPid(entry.Pid);
                if (windows.Count == 0) continue;
                string title = Win.TitleOf(windows[0]);
                lock (gate)
                {
                    Observation item = observations.Find(delegate(Observation o) { return o.Pid == entry.Pid && o.Started == entry.Started; });
                    if (item != null && item.InCurrent) item.Title = title;
                }
            }
        }

        public string Dump()
        {
            if (worker == null) Start();
            var completion = new System.Threading.Tasks.TaskCompletionSource<string>();
            commands.Enqueue(delegate
            {
                StringBuilder text = new StringBuilder();
                text.AppendLine(LastScanNote + ", sessions: " + sessions.Count);
                text.AppendLine("peakReads=" + peakReads + " wakes=" + wakes + " created=" + discoveries +
                    " retired=" + retired + " deviceScans=" + deviceScans + " errors=" + errors);
                foreach (Session session in sessions.Values)
                    text.AppendLine("  pid=" + session.Pid + " state=" + session.State + " system=" + session.System +
                        " shared=" + session.Shared + " key=" + session.Key.Replace('\n', ' '));
                completion.TrySetResult(text.ToString());
            });
            wake.Set();
            return completion.Task.Wait(3000) ? completion.Task.Result : "audio diagnostics timed out";
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping = true; wake.Set(); metadataWake.Set();
            if (worker != null) worker.Join();
            if (metadataWorker != null) metadataWorker.Join();
            wake.Dispose(); metadataWake.Dispose(); ready.Dispose();
        }
    }

    sealed class ProcessIdentity
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicInformation
        {
            public IntPtr Reserved1, Peb, Reserved2, Reserved3, UniquePid, ParentPid;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int kind, out BasicInformation info, int size, out int returned);
        public int Pid;
        public DateTime Started;
        public string Name;
        public bool IsRunning()
        {
            return IsRunning(Pid, Started);
        }

        public static bool IsRunning(int pid, DateTime started)
        {
            if (pid <= 0 || started == DateTime.MinValue) return false;
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return false;
            try
            {
                long created, exited, kernel, user; uint code;
                return GetExitCodeProcess(process, out code) && code == 259 &&
                    GetProcessTimes(process, out created, out exited, out kernel, out user) &&
                    DateTime.FromFileTime(created) == started;
            }
            finally { CloseHandle(process); }
        }

        public static List<ProcessIdentity> Capture(int pid)
        {
            // Query only the ancestry we need. A Toolhelp snapshot + Process.StartTime
            // for each ancestor was measured to delay new sessions past a 100ms sound.
            // NT parent lookup is best effort; failure retains the known source identity.
            List<ProcessIdentity> chain = new List<ProcessIdentity>();
            HashSet<int> visited = new HashSet<int>();
            DateTime childStart = DateTime.MaxValue;
            for (int depth = 0; depth < 8 && pid > 0 && visited.Add(pid); depth++)
            {
                IntPtr process = OpenProcess(0x1000, false, pid);
                if (process == IntPtr.Zero) break;
                try
                {
                    long created, exited, kernel, user;
                    if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) break;
                    DateTime started = DateTime.FromFileTime(created);
                    if (started > childStart) break;
                    StringBuilder path = new StringBuilder(1024); int length = path.Capacity;
                    string name = QueryFullProcessImageName(process, 0, path, ref length)
                        ? System.IO.Path.GetFileName(path.ToString()) : "pid " + pid;
                    chain.Add(new ProcessIdentity { Pid = pid, Started = started, Name = name });
                    childStart = started;
                    BasicInformation basic; int returned;
                    if (NtQueryInformationProcess(process, 0, out basic, Marshal.SizeOf(typeof(BasicInformation)), out returned) < 0) break;
                    pid = unchecked((int)basic.ParentPid.ToInt64());
                }
                catch { break; }
                finally { CloseHandle(process); }
            }
            return chain;
        }
    }
}

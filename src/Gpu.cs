using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DlssgManager
{
    public sealed class Candidate
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public double Load { get; set; }
        public int Samples { get; set; }
    }
    public sealed class ProcessProbe : IProcessProbe
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder text, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        public static string ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    int size = 32768;
                    var text = new StringBuilder(size);
                    if (QueryFullProcessImageName(handle, 0, text, ref size)) return Data.Full(text.ToString());
                }
                finally { CloseHandle(handle); }
            }
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE ProcessId=" + pid))
                using (var result = search.Get())
                    foreach (ManagementObject item in result)
                        if (item["ExecutablePath"] != null) return Data.Full(Convert.ToString(item["ExecutablePath"]));
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
        public bool IsRunning(string exe)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(exe);
            foreach (Process p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.HasExited) continue;
                    string path = ImagePath(p.Id);
                    // Same-name process with inaccessible path: fail closed.
                    if (path == null || path.Equals(Data.Full(exe), StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }
        public static string Graphics()
        {
            var names = new List<string>();
            using (var search = new ManagementObjectSearcher("SELECT Name,DriverVersion FROM Win32_VideoController"))
            using (var result = search.Get())
                foreach (ManagementObject item in result)
                    names.Add(Convert.ToString(item["Name"]) + "  ·  驱动 " + Convert.ToString(item["DriverVersion"]));
            return names.Count == 0 ? "未读取到显卡信息" : String.Join("\r\n", names);
        }
    }
    public static class GpuDetector
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhOpenQuery(string source, UIntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        struct FormattedValue
        {
            [FieldOffset(0)] public uint Status;
            [FieldOffset(8)] public double Value;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct CounterItem { public IntPtr Name; public FormattedValue Data; }
        static readonly Regex PidPattern = new Regex(@"pid_(\d+)_", RegexOptions.Compiled);
        static void Require(uint status, string action)
        {
            if (status != 0) throw new IOException(action + "失败（PDH 0x" + status.ToString("X8") + "）。请检查 GPU 计数器/驱动，或使用手动选择 EXE。");
        }
        static Dictionary<int, double> ReadLoads(IntPtr counter)
        {
            uint size = 0, count;
            uint status = PdhGetFormattedCounterArray(counter, 0x200, ref size, out count, IntPtr.Zero);
            if (status == 0x800007D5 || status == 0x800007D1) return new Dictionary<int, double>();
            if (status != 0x800007D2) Require(status, "读取 GPU 数组大小");
            if (size == 0 || size > 64 * 1024 * 1024) throw new IOException("GPU 计数器数组大小异常。");
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Require(PdhGetFormattedCounterArray(counter, 0x200, ref size, out count, buffer), "读取 GPU 数组");
                int itemSize = Marshal.SizeOf(typeof(CounterItem));
                if (count > size / itemSize) throw new IOException("GPU 计数器数组越界。");
                var loads = new Dictionary<int, double>();
                for (int i = 0; i < count; i++)
                {
                    var item = (CounterItem)Marshal.PtrToStructure(IntPtr.Add(buffer, i * itemSize), typeof(CounterItem));
                    double value = item.Data.Value;
                    if (item.Data.Status > 1 || Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 || value > 100) continue;
                    Match match = PidPattern.Match(Marshal.PtrToStringUni(item.Name) ?? ""); int pid;
                    if (!match.Success || !Int32.TryParse(match.Groups[1].Value, out pid)) continue;
                    if (!loads.ContainsKey(pid) || value > loads[pid]) loads[pid] = value;
                }
                return loads;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        static readonly HashSet<string> Excluded = new HashSet<string>(new[] {
            "LockApp", "LogonUI", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "dwm", "csrss", "explorer", "System", "Idle", "chrome", "msedge", "firefox", "Codex",
            "Taskmgr", "ApplicationFrameHost", "TextInputHost", "Widgets", "WidgetService", "RuntimeBroker",
            "DLSSG-Install-Manager", "steamwebhelper", "NVIDIA Overlay", "NVIDIA Share", "obs64"
        }, StringComparer.OrdinalIgnoreCase);
        public static bool Eligible(string name, string path, double value)
        {
            return !Excluded.Contains(name) && !String.IsNullOrWhiteSpace(path) &&
                !Double.IsNaN(value) && !Double.IsInfinity(value) && value > 0 && value <= 100 &&
                !Data.Under(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        }
        public static List<Candidate> Sample(Action<int, double> progress, CancellationToken token)
        {
            var totals = new Dictionary<int, Candidate>();
            var identities = new Dictionary<int, long>();
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try { identities[process.Id] = process.StartTime.ToUniversalTime().Ticks; }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            IntPtr query, counter;
            Require(PdhOpenQuery(null, UIntPtr.Zero, out query), "打开 GPU 查询");
            try
            {
                Require(PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", UIntPtr.Zero, out counter), "添加 GPU 计数器");
                Require(PdhCollectQueryData(query), "初始化 GPU 采样");
                var clock = Stopwatch.StartNew();
                for (int sample = 1; sample <= 10; sample++)
                {
                    // Absolute monotonic deadlines; never pretend loop iterations are seconds.
                    double remaining = sample * 1000.0 - clock.Elapsed.TotalMilliseconds;
                    if (remaining > 0 && token.WaitHandle.WaitOne((int)Math.Ceiling(remaining))) token.ThrowIfCancellationRequested();
                    token.ThrowIfCancellationRequested();
                    Require(PdhCollectQueryData(query), "采集 GPU 数据");
                    foreach (var load in ReadLoads(counter))
                    {
                        if (load.Value <= 0 || !identities.ContainsKey(load.Key)) continue;
                        Candidate c;
                        if (!totals.TryGetValue(load.Key, out c)) { c = new Candidate { Pid = load.Key }; totals[load.Key] = c; }
                        c.Load += load.Value; c.Samples++;
                    }
                    progress(sample, clock.Elapsed.TotalSeconds);
                }
                // Resolve process metadata only after the timing-critical window.
                foreach (Candidate c in totals.Values)
                {
                    c.Load /= 10.0;
                    if (c.Samples < 3 || c.Load < .5) continue;
                    try
                    {
                        using (Process p = Process.GetProcessById(c.Pid))
                        {
                            if (p.HasExited || p.StartTime.ToUniversalTime().Ticks != identities[c.Pid] || Excluded.Contains(p.ProcessName)) continue;
                            string path = ProcessProbe.ImagePath(p.Id);
                            if (!Eligible(p.ProcessName, path, c.Load)) continue;
                            c.Name = p.ProcessName + ".exe"; c.Path = path;
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                var result = totals.Values.Where(c => c.Samples >= 3 && c.Load >= 0.5 && File.Exists(c.Path))
                    .OrderByDescending(c => c.Load).ToList();
                if (result.Count == 0) throw new IOException("检测到的 GPU 数据不足以确认游戏路径或有效负载。请进入实际游戏画面再试，或使用“手动选择 EXE”。任务管理器能显示负载，并不代表当前权限可以读取进程路径。");
                return result;
            }
            finally { PdhCloseQuery(query); }
        }
    }
}

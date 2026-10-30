using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace musicPlayer.Services
{
    /// <summary>
    /// 进程统计小工具（诊断用）：按**父子进程链**找出属于本程序的后代进程。
    ///
    /// 为什么需要它：任务管理器里一堆 msedgewebview2.exe，其中大部分是 Windows Widgets 等
    /// 系统组件自己的内核。只有能沿父进程链追溯到本进程的那些，才是我们隐藏内核的进程。
    /// 用 ToolHelp32 快照读父子关系，不依赖 WMI / System.Management。
    /// </summary>
    public static class ProcessStats
    {
        /// <summary>当前进程工作集（MB）。</summary>
        public static long CurrentWorkingSetMb
        {
            get
            {
                try
                {
                    using var p = Process.GetCurrentProcess();
                    return p.WorkingSet64 / (1024 * 1024);
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// 归还工作集给系统（把暂时不用的物理页换出，任务管理器里的内存数立刻下降）。
        ///
        /// 注意：这不是"释放内存"，只是把工作集缩小、页换到页面文件；下次访问会重新调入。
        /// 只在**空闲回收内核之后**调用一次（间隔 ≥60 秒），不要频繁调——频繁 trim 会让界面变卡。
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                // (SIZE_T)-1 表示"让系统决定"目标大小（即尽可能回收）
                SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TrimWorkingSet 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 统计名字匹配 processName 且父进程链能追溯回本进程的进程数与总内存（MB）。
        /// processName 不带 .exe，例如 "msedgewebview2"。
        /// </summary>
        public static (int Count, long TotalMb) CountDescendants(string processName)
        {
            int selfPid = Environment.ProcessId;
            var snapshot = Snapshot();
            if (snapshot.Count == 0) return (0, 0);

            int count = 0;
            long bytes = 0;

            foreach (var kv in snapshot)
            {
                int pid = kv.Key;
                var (name, parentPid) = kv.Value;

                if (!string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsDescendantOf(pid, selfPid, snapshot)) continue;

                count++;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    bytes += p.WorkingSet64;
                }
                catch { /* 进程可能刚好退出 */ }
            }
            return (count, bytes / (1024 * 1024));
        }

        /// <summary>沿父进程链向上找，判断 pid 是否是 ancestorPid 的后代。</summary>
        private static bool IsDescendantOf(int pid, int ancestorPid,
            Dictionary<int, (string Name, int ParentPid)> snapshot)
        {
            int cur = pid;
            // 上限 32 层，防止异常数据造成死循环
            for (int guard = 0; guard < 32; guard++)
            {
                if (!snapshot.TryGetValue(cur, out var info)) return false;
                cur = info.ParentPid;
                if (cur == ancestorPid) return true;
                if (cur <= 4) return false; // 到 System Idle / System 了
            }
            return false;
        }

        /// <summary>取一份 (pid → (进程名, 父 pid)) 快照。</summary>
        private static Dictionary<int, (string Name, int ParentPid)> Snapshot()
        {
            var result = new Dictionary<int, (string, int)>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;

            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (!Process32FirstW(snap, ref entry)) return result;

                do
                {
                    string name = (entry.szExeFile ?? "").TrimEnd('\0');
                    int dot = name.LastIndexOf('.');
                    if (dot > 0) name = name[..dot];
                    result[(int)entry.th32ProcessID] = (name, (int)entry.th32ParentProcessID);
                }
                while (Process32NextW(snap, ref entry));
            }
            finally
            {
                CloseHandle(snap);
            }
            return result;
        }

        #region P/Invoke

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess,
            IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

        #endregion
    }
}

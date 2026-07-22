using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

// Minimal external-process memory reader + IL2CPP class/object locator for TaskBarHero.
// Read-only: opens the game process with PROCESS_VM_READ only, never writes.
namespace Tbh
{
    internal static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll")]
        public static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, int len);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }
    }

    public class Mem : IDisposable
    {
        const uint PROCESS_VM_READ = 0x0010;
        const uint PROCESS_QUERY_INFORMATION = 0x0400;
        const uint MEM_COMMIT = 0x1000;
        const uint PAGE_NOACCESS = 0x01;
        const uint PAGE_GUARD = 0x100;

        IntPtr _h;

        public Mem(int pid)
        {
            _h = Native.OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (_h == IntPtr.Zero)
                throw new Exception("OpenProcess failed (err " + Marshal.GetLastWin32Error() + "). Run as admin if needed.");
        }

        public void Dispose() { if (_h != IntPtr.Zero) { Native.CloseHandle(_h); _h = IntPtr.Zero; } }

        public byte[] Read(long addr, int size)
        {
            byte[] buf = new byte[size];
            IntPtr read;
            if (!Native.ReadProcessMemory(_h, (IntPtr)addr, buf, (IntPtr)size, out read) || (long)read != size)
                return null;
            return buf;
        }

        public long ReadPtr(long addr) { var b = Read(addr, 8); return b == null ? 0 : BitConverter.ToInt64(b, 0); }
        public int  ReadInt(long addr) { var b = Read(addr, 4); return b == null ? int.MinValue : BitConverter.ToInt32(b, 0); }
        public float ReadFloat(long addr){ var b = Read(addr, 4); return b == null ? float.NaN : BitConverter.ToSingle(b, 0); }

        public string ReadCString(long addr, int max)
        {
            var b = Read(addr, max);
            if (b == null) return null;
            int n = Array.IndexOf(b, (byte)0);
            if (n < 0) n = max;
            return Encoding.ASCII.GetString(b, 0, n);
        }

        // Read an IL2CPP System.String (length @ +0x10, UTF-16 chars @ +0x14).
        public string ReadIl2CppString(long strObj, int maxChars)
        {
            if (strObj == 0) return null;
            int len = ReadInt(strObj + 0x10);
            if (len <= 0 || len > maxChars) return null;
            var b = Read(strObj + 0x14, len * 2);
            return b == null ? null : Encoding.Unicode.GetString(b);
        }

        List<long[]> Regions()
        {
            var list = new List<long[]>();
            long addr = 0x10000;
            Native.MEMORY_BASIC_INFORMATION mbi;
            int sz = Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
            while (addr < 0x7FFFFFFF0000 && Native.VirtualQueryEx(_h, (IntPtr)addr, out mbi, sz) != 0)
            {
                long size = (long)mbi.RegionSize;
                if (size <= 0) break;
                if (mbi.State == MEM_COMMIT && (mbi.Protect & PAGE_NOACCESS) == 0 && (mbi.Protect & PAGE_GUARD) == 0)
                    list.Add(new long[] { (long)mbi.BaseAddress, size });
                addr = (long)mbi.BaseAddress + size;
            }
            return list;
        }

        public List<long> FindBytes(byte[] pattern, int maxHits)
        {
            var hits = new List<long>();
            foreach (var r in Regions())
            {
                long baseAddr = r[0], size = r[1];
                for (long off = 0; off < size; off += 8 * 1024 * 1024)
                {
                    int chunk = (int)Math.Min(8L * 1024 * 1024 + pattern.Length, size - off);
                    var buf = Read(baseAddr + off, chunk);
                    if (buf == null) continue;
                    for (int i = 0; i <= buf.Length - pattern.Length; i++)
                    {
                        if (buf[i] != pattern[0]) continue;
                        bool ok = true;
                        for (int j = 1; j < pattern.Length; j++) if (buf[i + j] != pattern[j]) { ok = false; break; }
                        if (ok) { hits.Add(baseAddr + off + i); if (hits.Count >= maxHits) return hits; }
                    }
                }
            }
            return hits;
        }

        public List<long> FindQwordRefs(HashSet<long> targets, int maxHits)
        {
            var hits = new List<long>();
            foreach (var r in Regions())
            {
                long baseAddr = r[0], size = r[1];
                for (long off = 0; off < size; off += 8 * 1024 * 1024)
                {
                    int chunk = (int)Math.Min(8L * 1024 * 1024, size - off);
                    var buf = Read(baseAddr + off, chunk);
                    if (buf == null) continue;
                    int limit = buf.Length - 7;
                    for (int i = 0; i < limit; i += 8)
                    {
                        long v = BitConverter.ToInt64(buf, i);
                        if (targets.Contains(v)) { hits.Add(baseAddr + off + i); if (hits.Count >= maxHits) return hits; }
                    }
                }
            }
            return hits;
        }

        // Locate an IL2CppClass* by class name + namespace.
        // Il2CppClass (metadata v31): +0x10 = name char*, +0x18 = namespace char*.
        // Il2CppClass (metadata v31): +0x10 name char*, +0x18 namespace char*.
        // Pass ns=null to accept any namespace (used for obfuscated holders like StageCache).
        public long FindClass(string name, string ns)
        {
            var strAddrs = FindBytes(Encoding.ASCII.GetBytes(name + "\0"), 128);
            if (strAddrs.Count == 0) return 0;
            var refs = FindQwordRefs(new HashSet<long>(strAddrs), 512);
            foreach (long r in refs)
            {
                long klass = r - 0x10;
                long nsPtr = ReadPtr(klass + 0x18);
                if (nsPtr == 0) continue;
                if (ns == null || ReadCString(nsPtr, 64) == ns) return klass;
            }
            return 0;
        }

        // Find heap object instances whose klass pointer (obj+0x0) == klass.
        public List<long> FindInstances(long klass, int maxHits)
        {
            return FindQwordRefs(new HashSet<long> { klass }, maxHits);
        }
    }
}

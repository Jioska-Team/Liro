param([Parameter(Mandatory = $true)][int]$ProcessId)
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 cannot inspect every process through Process.MainModule.
# Query only the image name, without requesting module or memory access.
if (-not ('Liro.Setup.ProcessImage' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Liro.Setup
{
    public static class ProcessImage
    {
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, int inherit, uint processId);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder buffer, ref uint size);

        public static string Read(int processId)
        {
            using (var process = OpenProcess(0x1000, 0, checked((uint)processId)))
            {
                if (process.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                var buffer = new StringBuilder(32768);
                uint size = (uint)buffer.Capacity;
                if (QueryFullProcessImageNameW(process, 0, buffer, ref size) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                return buffer.ToString();
            }
        }
    }
}
'@
}
[Liro.Setup.ProcessImage]::Read($ProcessId)

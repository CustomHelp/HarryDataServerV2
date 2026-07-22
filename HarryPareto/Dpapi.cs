using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HarryPareto;

/// <summary>
/// Minimal Windows DPAPI wrapper (CurrentUser scope) via a direct crypt32.dll P/Invoke — so the
/// stored settings password can be encrypted at rest WITHOUT pulling in the
/// System.Security.Cryptography.ProtectedData NuGet package (the machine is offline; no new
/// packages allowed, task E4). Protected blobs are base64-encoded for JSON storage.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved,
        IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain ?? string.Empty);
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = pinned.AddrOfPinnedObject();
            if (!CryptProtectData(ref inBlob, "HarryPareto", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Convert.ToBase64String(outBytes);
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    public static string Unprotect(string protectedBase64)
    {
        var data = Convert.FromBase64String(protectedBase64);
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            inBlob.cbData = data.Length;
            inBlob.pbData = pinned.AddrOfPinnedObject();
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var outBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, outBytes, 0, outBlob.cbData);
            return Encoding.UTF8.GetString(outBytes);
        }
        finally
        {
            if (pinned.IsAllocated) pinned.Free();
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}

using System.Runtime.InteropServices;

namespace RobloxMultiManager.Core;

internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool IsTrusted(string filePath)
    {
        var fileInfo = new WinTrustFileInfo(filePath);
        var fileInfoPointer = IntPtr.Zero;
        var dataPointer = IntPtr.Zero;
        try
        {
            fileInfoPointer = Marshal.AllocHGlobal(
                Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var data = new WinTrustData(fileInfoPointer);
            dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPointer, false);

            return WinVerifyTrust(
                IntPtr.Zero,
                GenericVerifyV2,
                dataPointer) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (dataPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustData>(dataPointer);
                Marshal.FreeHGlobal(dataPointer);
            }

            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
        IntPtr trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public WinTrustFileInfo(string filePath)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
        }

        public uint StructSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public IntPtr FileHandle = IntPtr.Zero;
        public IntPtr KnownSubject = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData
    {
        public WinTrustData(IntPtr fileInfo)
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>();
            UnionChoice = 1;
            FileInfo = fileInfo;
            UiChoice = 2;
            RevocationChecks = 0;
            StateAction = 0;
            ProviderFlags = 0x00001000;
            UiContext = 0;
        }

        public uint StructSize;
        public IntPtr PolicyCallbackData = IntPtr.Zero;
        public IntPtr SipClientData = IntPtr.Zero;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData = IntPtr.Zero;
        public IntPtr UrlReference = IntPtr.Zero;
        public uint ProviderFlags;
        public uint UiContext;
    }
}

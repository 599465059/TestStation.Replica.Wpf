using System.Runtime.InteropServices;
using System.Text;

namespace TestStation.Infrastructure.Transports.Can.Pcan;

// Minimal declarations for the public PCAN-Basic C ABI; library deployment is managed by the CAN manifest.
internal static class PcanNative
{
    static PcanNative() => CanNativeLibraryLoader.InstallPcanResolver();
    private const string Library = "PCANBasic.dll";
    // PCAN-Basic GetValue/SetValue parameter identifiers. Keep these named;
    // CHANNEL_CONDITION is 0x0D; HARDWARE_NAME is 0x0E.
    internal const byte ListenOnly = 8;
    internal const byte ChannelCondition = 0x0D;
    internal const byte AttachedChannelsCount = 42;
    internal const byte AttachedChannels = 43;
    internal const uint QueueEmpty = 0x20;
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public uint Id;
        public byte Type;
        public byte Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MessageFd
    {
        public uint Id;
        public byte Type;
        public byte Dlc;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Data;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Timestamp { public uint Millis; public ushort Overflow; public ushort Micros; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct ChannelInfo
    {
        public ushort Handle;
        public byte DeviceType;
        public byte Controller;
        public uint Features;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Name;
        public uint DeviceId;
        public uint Condition;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_Initialize", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint Initialize(ushort channel, ushort bitrate, byte hwType, uint ioPort, ushort interrupt);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_InitializeFD", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    internal static extern uint InitializeFd(ushort channel, string bitrate);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_Uninitialize", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint Uninitialize(ushort channel);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_Read", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint Read(ushort channel, out Message message, out Timestamp timestamp);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_ReadFD", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint ReadFd(ushort channel, out MessageFd message, out ulong timestamp);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_Write", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint Write(ushort channel, ref Message message);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_WriteFD", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint WriteFd(ushort channel, ref MessageFd message);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_GetValue", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint GetUInt(ushort channel, byte parameter, out uint value, uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_GetValue", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint GetChannels(ushort channel, byte parameter,
        [In, Out] ChannelInfo[] channels, uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_SetValue", CallingConvention = CallingConvention.Winapi)]
    internal static extern uint SetUInt(ushort channel, byte parameter, ref uint value, uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory)]
    [DllImport(Library, EntryPoint = "CAN_GetErrorText", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    internal static extern uint GetErrorText(uint error, ushort language, StringBuilder buffer);

    internal static void Check(uint status, string operation)
    {
        if (status == 0) return;
        var text = new StringBuilder(256);
        var description = GetErrorText(status, 0, text) == 0 ? text.ToString() : "未知 PCAN 错误";
        throw new IOException($"PCAN {operation}失败：{description} (0x{status:X8})");
    }
}

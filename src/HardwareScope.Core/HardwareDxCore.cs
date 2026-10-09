using System.Runtime.InteropServices;

namespace HardwareScope.Core;

internal sealed record HardwareNpuAdapter(string Id, string Name, string Luid);

internal static class HardwareDxCore
{
    public static List<HardwareNpuAdapter> EnumerateNpus()
    {
        var result = new List<HardwareNpuAdapter>();
        var factoryGuid = new Guid("78ee5945-c36e-4b13-a669-005dd11c0f06");
        var listGuid = new Guid("526c7776-40e9-459b-b711-f32ad76dfc28");
        var adapterGuid = new Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
        var mlAttribute = new Guid("b71b0d41-1088-422f-a27c-0250b7d3a988");
        var npuAttribute = new Guid("d46140c4-add7-451b-9e56-06fe8c3b58ed");
        var gpuAttribute = new Guid("b69eb219-3ded-4464-979f-a00bd4687006");
        Factory? factory = null;
        AdapterList? list = null;
        try
        {
            if (DXCoreCreateAdapterFactory(ref factoryGuid, out factory) < 0) return result;
            if (factory.CreateAdapterList(1, [mlAttribute], ref listGuid, out list) < 0) return result;
            for (uint i = 0; i < list.GetAdapterCount(); i++)
            {
                if (list.GetAdapter(i, ref adapterGuid, out var adapter) < 0) continue;
                try
                {
                    // An ML-capable graphics GPU is not an NPU. Older MCDM drivers identify only their non-GPU ML attribute.
                    if (!adapter.IsAttributeSupported(ref npuAttribute) && adapter.IsAttributeSupported(ref gpuAttribute)) continue;
                    if (adapter.GetPropertySize(2, out var bytes) < 0 || bytes == 0) continue;
                    var nameBuffer = Marshal.AllocHGlobal(checked((int)bytes));
                    var luidBuffer = Marshal.AllocHGlobal(8);
                    try
                    {
                        if (adapter.GetProperty(2, bytes, nameBuffer) < 0 || adapter.GetProperty(0, 8, luidBuffer) < 0) continue;
                        var low = unchecked((uint)Marshal.ReadInt32(luidBuffer));
                        var high = unchecked((uint)Marshal.ReadInt32(luidBuffer, 4));
                        var luid = $"0x{high:x8}_0x{low:x8}";
                        result.Add(new("dxcore/" + luid, Marshal.PtrToStringAnsi(nameBuffer) ?? "NPU", luid));
                    }
                    finally { Marshal.FreeHGlobal(nameBuffer); Marshal.FreeHGlobal(luidBuffer); }
                }
                finally { Marshal.FinalReleaseComObject(adapter); }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            // DXCore is an optional OS/driver boundary. PnP inventory remains available to the caller.
        }
        finally
        {
            if (list is not null) Marshal.FinalReleaseComObject(list);
            if (factory is not null) Marshal.FinalReleaseComObject(factory);
        }
        return result;
    }

    [DllImport("dxcore.dll")]
    private static extern int DXCoreCreateAdapterFactory(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out Factory factory);
    [ComImport, Guid("78ee5945-c36e-4b13-a669-005dd11c0f06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Factory
    {
        [PreserveSig] int CreateAdapterList(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Guid[] attributes,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out AdapterList list);
    }
    [ComImport, Guid("526c7776-40e9-459b-b711-f32ad76dfc28"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AdapterList
    {
        [PreserveSig] int GetAdapter(uint index, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out Adapter adapter);
        [PreserveSig] uint GetAdapterCount();
    }
    [ComImport, Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Adapter
    {
        [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsValid();
        [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsAttributeSupported(ref Guid attribute);
        [PreserveSig] [return: MarshalAs(UnmanagedType.U1)] bool IsPropertySupported(uint property);
        [PreserveSig] int GetProperty(uint property, nuint size, IntPtr data);
        [PreserveSig] int GetPropertySize(uint property, out nuint size);
    }
}

using System.Reflection;
using System.Runtime.InteropServices;

using MelonLoader.Utils;

namespace Streety.Gltf;

/// <summary>
/// p/invoke into draco's unity plugin (native/draco/dracodec_unity.dll, deployed to UserLibs).
/// all calls are thread-safe per mesh, so decoding runs on worker threads.
/// </summary>
internal static unsafe class DracoNative
{
    private const string Library = "dracodec_unity";

    public enum DataType
    {
        Invalid = 0,
        Int8,
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Float32,
        Float64,
        Bool,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DracoMesh
    {
        public int NumFaces;
        public int NumVertices;
        public int NumAttributes;
        public IntPtr PrivateMesh;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DracoAttribute
    {
        public int AttributeType;
        public DataType DataType;
        public int NumComponents;
        public int UniqueId;
        public IntPtr PrivateAttribute;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DracoData
    {
        public DataType DataType;
        public IntPtr Data;
    }

    private static bool _resolverInstalled;

    /// <summary>
    /// points the runtime at UserLibs for our native dll; MelonLoader only resolves managed assemblies there.
    /// </summary>
    public static void Initialize()
    {
        if (_resolverInstalled)
            return;

        _resolverInstalled = true;

        NativeLibrary.SetDllImportResolver(typeof(DracoNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
            return IntPtr.Zero;

        var path = Path.Combine(MelonEnvironment.UserLibsDirectory, Library + ".dll");

        if (NativeLibrary.TryLoad(path, out var handle))
            return handle;

        return NativeLibrary.TryLoad(name, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int DecodeDracoMesh(byte* data, uint length, DracoMesh** mesh);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ReleaseDracoMesh(DracoMesh** mesh);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool GetAttributeByUniqueId(DracoMesh* mesh, int uniqueId, DracoAttribute** attribute);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ReleaseDracoAttribute(DracoAttribute** attribute);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool GetAttributeData(DracoMesh* mesh, DracoAttribute* attribute, DracoData** data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool GetMeshIndices(DracoMesh* mesh, DracoData** indices);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ReleaseDracoData(DracoData** data);
}

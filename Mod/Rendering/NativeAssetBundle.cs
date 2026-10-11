using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Bindings;

namespace VS3D.Rendering;

// Unity 6 marshals string/array arguments through Span<T>, which Il2CppInterop cannot
// unstrip, so the managed AssetBundle wrappers throw. These helpers pin .NET memory
// themselves and call the generated *_Injected icalls directly.
public static unsafe class NativeAssetBundle
{
    public static AssetBundle LoadFromFile(string path)
    {
        fixed (char* p = path)
        {
            var span = new ManagedSpanWrapper(p, path.Length);
            var handle = AssetBundle.LoadFromFile_Internal_Injected(ref span, 0u, 0ul);
            return handle == System.IntPtr.Zero ? null : Unmarshal.UnmarshalUnityObject<AssetBundle>(handle);
        }
    }

    public static T LoadAsset<T>(AssetBundle bundle, string name) where T : Object
    {
        fixed (char* p = name)
        {
            var span = new ManagedSpanWrapper(p, name.Length);
            var handle = AssetBundle.LoadAsset_Internal_Injected(bundle.m_CachedPtr, ref span, Il2CppType.Of<T>());
            if (handle == System.IntPtr.Zero)
                return null;
            return Unmarshal.UnmarshalUnityObject<Object>(handle)?.TryCast<T>();
        }
    }
}

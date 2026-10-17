using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CustomHeroes
{
    /// <summary>Raw IL2CPP runtime access, for struct fields (BalancingEntryId) and dictionaries with struct keys
    /// that Il2CppInterop can't marshal.</summary>
    internal static unsafe class Native
    {
        private const string Lib = "GameAssembly";
        public const int ArrayDataOffset = 0x20; // Il2CppArray: object header (16) + bounds (8) + length (8)
        public const int ObjectHeader = 0x10;

        [DllImport(Lib)] public static extern IntPtr il2cpp_object_get_class(IntPtr obj);
        [DllImport(Lib)] public static extern IntPtr il2cpp_class_get_name(IntPtr klass);
        [DllImport(Lib)] public static extern IntPtr il2cpp_class_get_parent(IntPtr klass);
        [DllImport(Lib)] public static extern IntPtr il2cpp_class_get_field_from_name(IntPtr klass, [MarshalAs(UnmanagedType.LPStr)] string name);
        [DllImport(Lib)] public static extern int il2cpp_field_get_offset(IntPtr field);
        [DllImport(Lib)] public static extern ulong il2cpp_array_length(IntPtr array);
        [DllImport(Lib)] public static extern void il2cpp_gc_wbarrier_set_field(IntPtr obj, IntPtr targetAddress, IntPtr value);
        [DllImport(Lib)] public static extern IntPtr il2cpp_class_get_element_class(IntPtr klass);
        [DllImport(Lib)] public static extern int il2cpp_array_element_size(IntPtr arrayClass);
        [DllImport(Lib)] public static extern IntPtr il2cpp_field_get_type(IntPtr field);
        [DllImport(Lib)] public static extern IntPtr il2cpp_class_from_type(IntPtr type);

        /// <summary>Class of a field's type (searches base classes), or zero.</summary>
        public static IntPtr FieldClass(IntPtr klass, string name)
        {
            for (var k = klass; k != IntPtr.Zero; k = il2cpp_class_get_parent(k))
            {
                var f = il2cpp_class_get_field_from_name(k, name);
                if (f != IntPtr.Zero) return il2cpp_class_from_type(il2cpp_field_get_type(f));
            }
            return IntPtr.Zero;
        }

        public static int ElementSize(IntPtr array) => il2cpp_array_element_size(il2cpp_object_get_class(array));

        /// <summary>Feeds every value of an Il2CppSystem Dictionary to <paramref name="sink"/> (raw read,
        /// so it works for struct keys like BalancingEntryId).</summary>
        public static void ScanDictionaryValues(IntPtr dict, Action<IntPtr> sink)
        {
            if (dict == IntPtr.Zero) return;
            int entriesOff = ObjFieldOffset(dict, "_entries");
            if (entriesOff < 0) entriesOff = ObjFieldOffset(dict, "entries");
            int countOff = ObjFieldOffset(dict, "_count");
            if (countOff < 0) countOff = ObjFieldOffset(dict, "count");
            if (entriesOff < 0 || countOff < 0) throw new Exception("Dictionary layout not recognized");
            var entries = ReadPtr(dict + entriesOff);
            if (entries == IntPtr.Zero) return;
            int count = Math.Min(ReadInt(dict + countOff), Length(entries));
            var elemClass = il2cpp_class_get_element_class(il2cpp_object_get_class(entries));
            int hashOff = FieldOffset(elemClass, "hashCode") - ObjectHeader;
            int valueOff = FieldOffset(elemClass, "value") - ObjectHeader;
            int size = ElementSize(entries);
            for (int i = 0; i < count; i++)
            {
                var e = entries + ArrayDataOffset + i * size;
                if (ReadInt(e + hashOff) >= 0) sink(ReadPtr(e + valueOff));
            }
        }

        public static string ClassName(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return null;
            return Marshal.PtrToStringAnsi(il2cpp_class_get_name(il2cpp_object_get_class(obj)));
        }

        /// <summary>Class names from the object's class up to System.Object.</summary>
        public static IEnumerable<string> ClassChain(IntPtr obj)
        {
            for (var k = obj == IntPtr.Zero ? IntPtr.Zero : il2cpp_object_get_class(obj); k != IntPtr.Zero; k = il2cpp_class_get_parent(k))
                yield return Marshal.PtrToStringAnsi(il2cpp_class_get_name(k));
        }

        /// <summary>Field offset from the start of an object (searches base classes). -1 if missing.</summary>
        public static int FieldOffset(IntPtr klass, string name)
        {
            for (var k = klass; k != IntPtr.Zero; k = il2cpp_class_get_parent(k))
            {
                var f = il2cpp_class_get_field_from_name(k, name);
                if (f != IntPtr.Zero) return il2cpp_field_get_offset(f);
            }
            return -1;
        }

        public static int ObjFieldOffset(IntPtr obj, string name) => FieldOffset(il2cpp_object_get_class(obj), name);

        public static int Length(IntPtr array) => array == IntPtr.Zero ? 0 : (int)il2cpp_array_length(array);

        public static int ReadInt(IntPtr p) => *(int*)p;
        public static void WriteInt(IntPtr p, int v) => *(int*)p = v;
        public static IntPtr ReadPtr(IntPtr p) => *(IntPtr*)p;

        /// <summary>Stores an object reference into a field with the GC write barrier.</summary>
        public static void SetRef(IntPtr obj, string field, IntPtr value)
        {
            int off = ObjFieldOffset(obj, field);
            if (off < 0) throw new Exception($"field {field} not found on {ClassName(obj)}");
            il2cpp_gc_wbarrier_set_field(obj, obj + off, value);
        }
    }
}

using System.Reflection;

namespace GoblinTweaks.Core;

/// <summary>Estimates the managed memory a tweak keeps alive by walking the objects reachable from it.</summary>
/// <remarks>
/// .NET cannot attribute memory to an object exactly, so this is an approximation. Only objects the
/// plugin owns are followed (its own types, KamiToolKit nodes, strings, arrays and collections);
/// Dalamud, Lumina and native game memory are not counted. Only fields are read, never properties,
/// so measuring has no side effects and is safe to run off the framework thread.
/// </remarks>
internal static class MemoryEstimator
{
    private const int ObjectHeader = 16;
    private const int ArrayHeader = 24;
    private const int ReferenceSize = 8;
    private const int MaxObjects = 2_000_000;

    private static readonly Dictionary<Type, Layout> Layouts = [];
    private static readonly Dictionary<Type, bool> Owned = [];

    private sealed class Layout
    {
        /// <summary>Bytes taken by the instance fields.</summary>
        public int Size;

        /// <summary>Fields that hold an object reference, or a struct containing one.</summary>
        public FieldInfo[] Walkable = [];
    }

    /// <summary>Approximate bytes of managed memory reachable from <paramref name="tweak"/>. Not thread-safe.</summary>
    public static long Estimate(Tweak tweak)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { tweak };
        var pending = new Stack<object>();
        pending.Push(tweak);

        long total = 0;
        while (seen.Count < MaxObjects && pending.TryPop(out var obj))
            total += Measure(obj, seen, pending);

        return total;
    }

    private static long Measure(object obj, HashSet<object> seen, Stack<object> pending)
    {
        if (obj is string text)
            return Align(22 + text.Length * 2L);

        var type = obj.GetType();
        if (obj is Array array)
        {
            var elementType = type.GetElementType()!;
            if (MayHoldReferences(elementType))
            {
                foreach (var element in array)
                {
                    if (element == null)
                        continue;

                    if (elementType.IsValueType)
                        WalkFields(element, seen, pending);
                    else
                        Enqueue(element, seen, pending);
                }
            }

            return Align(ArrayHeader + array.LongLength * FieldSize(elementType));
        }

        WalkFields(obj, seen, pending);
        return Align(ObjectHeader + GetLayout(type).Size);
    }

    private static void WalkFields(object owner, HashSet<object> seen, Stack<object> pending)
    {
        foreach (var field in GetLayout(owner.GetType()).Walkable)
        {
            var value = field.GetValue(owner);
            if (value == null)
                continue;

            if (field.FieldType.IsValueType)
                WalkFields(value, seen, pending);
            else
                Enqueue(value, seen, pending);
        }
    }

    private static void Enqueue(object value, HashSet<object> seen, Stack<object> pending)
    {
        // Other tweaks and the shared plugin state are reachable through Tweak.Manager, but are not this tweak's memory.
        if (value is Tweak or TweakManager or ConfigStore || !IsOwned(value.GetType()))
            return;

        if (seen.Add(value))
            pending.Push(value);
    }

    private static bool IsOwned(Type type)
    {
        if (Owned.TryGetValue(type, out var owned))
            return owned;

        owned = type == typeof(string)
            || type.IsArray
            || type.Assembly == typeof(Tweak).Assembly
            || type.Assembly.GetName().Name == "KamiToolKit"
            || type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true;

        Owned[type] = owned;
        return owned;
    }

    private static Layout GetLayout(Type type)
    {
        if (Layouts.TryGetValue(type, out var layout))
            return layout;

        layout = new Layout();
        var walkable = new List<FieldInfo>();
        for (var current = type; current != null; current = current.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var field in current.GetFields(flags))
            {
                layout.Size += FieldSize(field.FieldType);
                if (MayHoldReferences(field.FieldType))
                    walkable.Add(field);
            }
        }

        layout.Walkable = [.. walkable];
        Layouts[type] = layout;
        return layout;
    }

    private static bool MayHoldReferences(Type type)
    {
        if (type.IsPointer || type.IsFunctionPointer || type.IsByRef || type.IsPrimitive || type.IsEnum)
            return false;

        if (type.IsValueType)
            return GetLayout(type).Walkable.Length > 0;

        return !typeof(Delegate).IsAssignableFrom(type);
    }

    private static int FieldSize(Type type)
    {
        if (!type.IsValueType)
            return ReferenceSize;

        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);

        if (!type.IsPrimitive)
            return GetLayout(type).Size;

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte => 1,
            TypeCode.Char or TypeCode.Int16 or TypeCode.UInt16 => 2,
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
            _ => 8,
        };
    }

    private static long Align(long size) => Math.Max(24, (size + 7) & ~7L);
}

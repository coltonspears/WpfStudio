// Members newer than .NET Framework that the code shared with the .NET Framework preview host uses.
// Compiled only for net48, into each assembly, so the same source reads the same on both runtimes
// and no polyfill package has to run beside the previewed project's own copies of them.
using System.Runtime.CompilerServices;

namespace System
{
    internal static class NetFrameworkCompatibility
    {
        extension(ArgumentNullException)
        {
            public static void ThrowIfNull(object? argument, [CallerArgumentExpression(nameof(argument))] string? parameterName = null)
            {
                if (argument is null) throw new ArgumentNullException(parameterName);
            }
        }

        extension(ObjectDisposedException)
        {
            public static void ThrowIf(bool condition, object instance)
            {
                if (condition) throw new ObjectDisposedException(instance.GetType().FullName);
            }
        }

        extension(double)
        {
            public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        }

        extension(Enum)
        {
            public static bool IsDefined<T>(T value) where T : struct, Enum => Enum.IsDefined(typeof(T), value);
        }

        extension(Convert)
        {
            public static string ToHexString(byte[] bytes)
            {
                var text = new Text.StringBuilder(bytes.Length * 2);
                foreach (byte value in bytes) text.Append(value.ToString("X2", Globalization.CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        extension(IO.Path)
        {
            /// <summary>A drive and separator, or a UNC or device path. A bare <c>C:file</c> stays relative to something.</summary>
            public static bool IsPathFullyQualified(string path) =>
                path.Length >= 3 && path[1] == ':' && path[2] is '\\' or '/' && (path[0] | 0x20) is >= 'a' and <= 'z'
                || path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';
        }

        public static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;
        public static bool StartsWith(this string text, char value) => text.Length > 0 && text[0] == value;
        public static bool EndsWith(this string text, char value) => text.Length > 0 && text[text.Length - 1] == value;

        extension(Math)
        {
            public static int Clamp(int value, int minimum, int maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
            public static double Clamp(double value, double minimum, double maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
        }
    }
}

namespace System.Collections.Generic
{
    internal static class NetFrameworkCollections
    {
        public static bool TryPop<T>(this Stack<T> stack, out T item)
        {
            if (stack.Count == 0) { item = default!; return false; }
            item = stack.Pop();
            return true;
        }

        public static bool TryDequeue<T>(this Queue<T> queue, out T item)
        {
            if (queue.Count == 0) { item = default!; return false; }
            item = queue.Dequeue();
            return true;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key) where TKey : notnull =>
            dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue fallback) where TKey : notnull =>
            dictionary.TryGetValue(key, out var value) ? value : fallback;
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>, IEqualityComparer
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        private ReferenceEqualityComparer() { }
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object? value) => RuntimeHelpers.GetHashCode(value);
    }
}

namespace System.Linq
{
    internal static class NetFrameworkEnumerable
    {
        public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> key, IEqualityComparer<TKey>? comparer = null)
        {
            var seen = new HashSet<TKey>(comparer);
            foreach (var item in source)
                if (seen.Add(key(item))) yield return item;
        }

        public static T MinBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> key) => source.OrderBy(key).First();

        public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(this IEnumerable<TFirst> first, IEnumerable<TSecond> second) =>
            first.Zip(second, (a, b) => (a, b));

        public static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source, IComparer<T>? comparer = null) => source.OrderBy(item => item, comparer);
        public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> source, int count)
        {
            var items = source as IList<T> ?? source.ToList();
            for (int index = Math.Max(0, items.Count - count); index < items.Count; index++) yield return items[index];
        }
    }
}

namespace System.Numerics
{
    internal static class BitOperations
    {
        public static int PopCount(ulong value)
        {
            int count = 0;
            for (; value != 0; value &= value - 1) count++;
            return count;
        }

        public static int PopCount(uint value) => PopCount((ulong)value);
    }
}

namespace Scalpel.Core;

/// <summary>Arrays kept between frames and replaced only when the size they need changes, so a steady frame
/// allocates none.</summary>
internal static class Buffers {
    /// <summary><paramref name="array"/>, replaced by a new one unless it is <paramref name="length"/> long already.
    /// A kept array's contents are left as they were.</summary>
    internal static T[] Sized<T>(ref T[] array, int length) {
        if (array.Length != length) {
            array = new T[length];
        }
        return array;
    }

    /// <summary><see cref="Sized{T}"/>, with a kept array cleared: either way it holds only defaults.</summary>
    internal static T[] Cleared<T>(ref T[] array, int length) {
        if (array.Length != length) {
            array = new T[length];
        } else {
            Array.Clear(array);
        }
        return array;
    }

    /// <summary><paramref name="array"/>, <see cref="Sized{T}"/> to <paramref name="from"/> and holding a copy of
    /// it.</summary>
    internal static T[] Copied<T>(ref T[] array, ReadOnlySpan<T> from) {
        from.CopyTo(Sized(ref array, from.Length));
        return array;
    }
}

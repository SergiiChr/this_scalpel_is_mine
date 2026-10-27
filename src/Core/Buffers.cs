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

    /// <summary><paramref name="array"/>, replaced by a new one with room to spare unless it holds
    /// <paramref name="length"/> already: for counts that change from frame to frame. Only the first
    /// <paramref name="length"/> items are meant to be used.</summary>
    internal static T[] AtLeast<T>(ref T[] array, int length) {
        if (array.Length < length) {
            array = new T[length + (length / 2)];
        }
        return array;
    }

    /// <summary><paramref name="array"/>, <see cref="AtLeast{T}"/> as long as <paramref name="from"/> and holding a
    /// copy of it in front: as many items as <paramref name="from"/> has are meant to be used.</summary>
    internal static T[] CopiedInto<T>(ref T[] array, ReadOnlySpan<T> from) {
        from.CopyTo(AtLeast(ref array, from.Length));
        return array;
    }

    /// <summary><paramref name="array"/>, <see cref="Sized{T}"/> to <paramref name="from"/> and holding a copy of
    /// it.</summary>
    internal static T[] Copied<T>(ref T[] array, ReadOnlySpan<T> from) {
        from.CopyTo(Sized(ref array, from.Length));
        return array;
    }
}

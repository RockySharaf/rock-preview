namespace Anything.Core;

/// <summary>
/// The shell's ID lists (PIDLs) as bytes: a run of items, each a 2-byte size (counting itself) and its data, ended by a
/// 2-byte zero. The empty list is the Desktop, the top of the namespace. Kept apart from the window so it can be tested.
/// </summary>
public static class IdList
{
    /// <summary>The Desktop's list (no items), or one that can't be read: there is nothing above it.</summary>
    public static bool IsEmpty(byte[] idList) => LastItemOffset(idList) < 0;

    /// <summary>The list of the place one level up (Up), or null at the Desktop.</summary>
    public static byte[]? Parent(byte[] idList)
    {
        int last = LastItemOffset(idList);
        if (last < 0) return null;
        var parent = new byte[last + 2]; // the items before the last, and the ending zero
        Array.Copy(idList, parent, last);
        return parent;
    }

    /// <summary>The last item on its own (the place's ID inside the one above it), or null at the Desktop.</summary>
    public static byte[]? LastItem(byte[] idList)
    {
        int last = LastItemOffset(idList);
        if (last < 0) return null;
        int size = idList[last] | idList[last + 1] << 8;
        var item = new byte[size + 2];
        Array.Copy(idList, last, item, 0, size);
        return item;
    }

    /// <summary>Where the last item starts, or -1 when there is none or the list runs past its end.</summary>
    static int LastItemOffset(byte[] idList)
    {
        int offset = 0, last = -1;
        while (offset + 2 <= idList.Length)
        {
            int size = idList[offset] | idList[offset + 1] << 8;
            if (size == 0) return last;
            if (size < 2 || offset + size + 2 > idList.Length) return -1; // no room for the item and the ending zero
            last = offset;
            offset += size;
        }
        return -1;
    }
}

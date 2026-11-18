using System.Buffers.Binary;
using System.Text;

namespace Plugin.NaughtyDog.Content;

/// <summary>
/// A Naughty Dog "DC" data module (Uncharted 2 <c>dc1/*.bin</c>): compiled
/// script data the game links in place and looks symbols up in by 32-bit hash.
/// </summary>
/// <remarks>
/// Layout [C 1.09 0x1563a8 link, 0x465fe8 load]:
/// <list type="bullet">
/// <item>header: u32 <c>'DC00'</c>, u32 1, u32 relocation table offset, u32 0;</item>
/// <item>root at 0x10: u32 pointer to the symbol table, u32 the module's type hash;</item>
/// <item>symbol table: u32 (unknown, 1 in every module seen), u32 count, then count
/// (u32 symbol hash, u32 pointer) pairs;</item>
/// <item>relocation table: u32 count, then the file offsets of every word that holds
/// a pointer. A pointer is a file offset; linking adds the load address to each.</item>
/// </list>
/// Edits never move existing bytes: new data is appended after the old data and
/// the relocation table is rewritten after it, so every old pointer stays valid.
/// </remarks>
public sealed class DcModule
{
    public const uint Magic = 0x44433030; // "DC00"
    private const int HeaderSize = 0x10;

    private readonly List<byte> _data;
    private readonly SortedSet<int> _pointers;

    public uint TypeHash => ReadU32(0x14);

    /// <summary>Symbol hash to the file offset of its value, in table order.</summary>
    public IReadOnlyList<(uint Hash, int Offset)> Symbols
    {
        get
        {
            int table = (int)ReadU32(0x10);
            int count = (int)ReadU32(table + 4);
            List<(uint, int)> symbols = new(count);
            for (int i = 0; i < count; i++)
                symbols.Add((ReadU32(table + 8 + 8 * i), (int)ReadU32(table + 12 + 8 * i)));
            return symbols;
        }
    }

    public int Length => _data.Count;

    public DcModule(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize + 8 || BinaryPrimitives.ReadUInt32BigEndian(file) != Magic)
            throw new InvalidDataException("not a DC00 module");
        int relocOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(file[8..]);
        if (relocOffset < HeaderSize || relocOffset + 4 > file.Length)
            throw new InvalidDataException($"relocation table offset 0x{relocOffset:x} is outside the module");
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(file[relocOffset..]);
        if (relocOffset + 4 + 4L * count > file.Length)
            throw new InvalidDataException("relocation table runs past the end of the module");
        _data = [.. file[..relocOffset]];
        _pointers = [];
        for (int i = 0; i < count; i++)
        {
            int at = (int)BinaryPrimitives.ReadUInt32BigEndian(file[(relocOffset + 4 + 4 * i)..]);
            if (at < 0 || at + 4 > relocOffset)
                throw new InvalidDataException($"relocation {i} points at 0x{at:x}, outside the data");
            _pointers.Add(at);
        }
    }

    public int Find(uint hash)
    {
        foreach ((uint h, int offset) in Symbols)
            if (h == hash)
                return offset;
        throw new KeyNotFoundException($"symbol 0x{hash:x8} is not in the module");
    }

    public bool IsPointer(int offset) => _pointers.Contains(offset);

    public uint ReadU32(int offset) =>
        (uint)(_data[offset] << 24 | _data[offset + 1] << 16 | _data[offset + 2] << 8 | _data[offset + 3]);

    public void WriteU32(int offset, uint value)
    {
        _data[offset] = (byte)(value >> 24);
        _data[offset + 1] = (byte)(value >> 16);
        _data[offset + 2] = (byte)(value >> 8);
        _data[offset + 3] = (byte)value;
    }

    /// <summary>Copies a word, as a pointer when the source is one.</summary>
    public void CopyWord(int from, int to)
    {
        WriteU32(to, ReadU32(from));
        if (IsPointer(from))
            _pointers.Add(to);
        else
            _pointers.Remove(to);
    }

    /// <summary>The string a pointer word points at, or null when the word is not a pointer.</summary>
    public string? ReadStringAt(int pointerWord)
    {
        if (!IsPointer(pointerWord))
            return null;
        int at = (int)ReadU32(pointerWord);
        int end = at;
        while (end < _data.Count && _data[end] != 0)
            end++;
        return Encoding.Latin1.GetString([.. _data.GetRange(at, end - at)]);
    }

    /// <summary>Appends bytes (16-aligned) and returns their offset. Words listed in <paramref name="pointerWords"/> (relative) become pointers.</summary>
    public int Append(ReadOnlySpan<byte> bytes, IEnumerable<int>? pointerWords = null)
    {
        while (_data.Count % 16 != 0)
            _data.Add(0);
        int at = _data.Count;
        _data.AddRange(bytes.ToArray());
        foreach (int word in pointerWords ?? [])
            _pointers.Add(at + word);
        return at;
    }

    /// <summary>Points the word at <paramref name="pointerWord"/> at a new NUL-terminated string.</summary>
    public void SetString(int pointerWord, string value)
    {
        int at = Append([.. Encoding.Latin1.GetBytes(value), 0]);
        WriteU32(pointerWord, (uint)at);
        _pointers.Add(pointerWord);
    }

    /// <summary>
    /// Copies an array of fixed-size records into a new array whose records are
    /// <paramref name="newStride"/> bytes, keeping each record's old bytes at its
    /// start (and its pointers) and zero-filling the rest, then points
    /// <paramref name="arrayPointerWord"/> at the copy. For a build that grew a
    /// record (Uncharted 2 1.09's actor list is 0x24 bytes per record where the
    /// patch 7 data has 0x20).
    /// </summary>
    public void WidenArray(int arrayPointerWord, int count, int oldStride, int newStride)
    {
        if (newStride < oldStride || oldStride % 4 != 0 || newStride % 4 != 0)
            throw new ArgumentException("a record can only grow, by whole words");
        if (!IsPointer(arrayPointerWord))
            throw new InvalidDataException($"word 0x{arrayPointerWord:x} is not a pointer");
        int old = (int)ReadU32(arrayPointerWord);
        byte[] copy = new byte[count * newStride];
        List<int> pointers = [];
        for (int i = 0; i < count; i++)
        {
            _data.CopyTo(old + i * oldStride, copy, i * newStride, oldStride);
            for (int word = 0; word < oldStride; word += 4)
                if (IsPointer(old + i * oldStride + word))
                    pointers.Add(i * newStride + word);
        }
        WriteU32(arrayPointerWord, (uint)Append(copy, pointers));
    }

    /// <summary>
    /// Grows a {count, pointer} list by <paramref name="strings"/>.Count records of <paramref name="stride"/> bytes: the
    /// array is copied (old records and their pointers kept) with the new records after them, each new record zero but
    /// for its string fields (record offset to text), and the count and pointer words are updated.
    /// </summary>
    public void AppendRecords(int countWord, int arrayPointerWord, int stride, IReadOnlyList<IReadOnlyDictionary<int, string>> strings)
    {
        int count = (int)ReadU32(countWord);
        if (count > 0 && !IsPointer(arrayPointerWord))
            throw new InvalidDataException($"word 0x{arrayPointerWord:x} is not a pointer");
        int old = (int)ReadU32(arrayPointerWord);
        byte[] copy = new byte[(count + strings.Count) * stride];
        List<int> pointers = [];
        for (int i = 0; i < count; i++)
        {
            _data.CopyTo(old + i * stride, copy, i * stride, stride);
            for (int word = 0; word < stride; word += 4)
                if (IsPointer(old + i * stride + word))
                    pointers.Add(i * stride + word);
        }
        int at = Append(copy, pointers);
        WriteU32(arrayPointerWord, (uint)at);
        _pointers.Add(arrayPointerWord);
        WriteU32(countWord, (uint)(count + strings.Count));
        for (int i = 0; i < strings.Count; i++)
            foreach ((int field, string text) in strings[i])
                SetString(at + (count + i) * stride + field, text);
    }

    /// <summary>
    /// Adds a symbol whose value is <paramref name="value"/> (appended; words at <paramref name="pointerWords"/> are
    /// pointers). The symbol table is copied with the new entry in hash order (the game's tables are sorted) and the
    /// root pointed at the copy.
    /// </summary>
    public void AddSymbol(uint hash, ReadOnlySpan<byte> value, IEnumerable<int>? pointerWords = null)
    {
        List<(uint Hash, int Offset)> symbols = [.. Symbols];
        if (symbols.Any(s => s.Hash == hash))
            throw new InvalidOperationException($"symbol 0x{hash:x8} is already in the module");
        int table = (int)ReadU32(0x10);
        uint first = ReadU32(table);
        symbols.Add((hash, Append(value, pointerWords)));
        symbols.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        byte[] copy = new byte[8 + 8 * symbols.Count];
        BinaryPrimitives.WriteUInt32BigEndian(copy, first);
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(4), (uint)symbols.Count);
        for (int i = 0; i < symbols.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(8 + 8 * i), symbols[i].Hash);
            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(12 + 8 * i), (uint)symbols[i].Offset);
        }
        WriteU32(0x10, (uint)Append(copy, Enumerable.Range(0, symbols.Count).Select(i => 12 + 8 * i)));
    }

    public byte[] ToArray()
    {
        List<byte> file = [.. _data];
        while (file.Count % 16 != 0)
            file.Add(0);
        int relocOffset = file.Count;
        byte[] table = new byte[4 + 4 * _pointers.Count];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)_pointers.Count);
        int i = 0;
        foreach (int pointer in _pointers)
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4 + 4 * i++), (uint)pointer);
        file.AddRange(table);
        byte[] bytes = [.. file];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), (uint)relocOffset);
        return bytes;
    }
}

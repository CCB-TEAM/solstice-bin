using System.Buffers.Binary;

namespace Solstice.Bin;

/// <summary>
/// 全局 tag store 的读写。
///
/// <para>
/// 关键：<b>FName 是变长的</b>——带数字后缀时占 8 字节（索引 | 0x80000000 再跟 number），
/// 否则 4 字节。export path 是 4 个 FName，所以一条可能是 16 或更多字节。
/// 按固定 16 字节切会在样本上少算 884 字节（221 个带号 FName）。
/// </para>
///
/// <para>
/// 布局：<c>BeginMagic | int32 nums[11] | int32 textBytes | texts | numberlessNames | names
/// | numberlessExportPaths | exportPaths | ansiOffsets | wideOffsets | ansiStrings
/// | wideStrings | numberlessPairs | pairs | EndMagic</c>。
/// </para>
/// </summary>
public sealed class TagStore
{
    private int[] _nums = new int[11];

    private byte[] _texts = [];
    private byte[] _numberlessNames = [];
    private byte[] _names = [];
    private byte[] _numberlessExportPaths = [];
    private byte[] _exportPaths = [];
    private byte[] _wideOffsets = [];
    private byte[] _wideStrings = [];
    private byte[] _numberlessPairs = [];

    private readonly List<uint> _ansiOffsets = new();
    private byte[] _ansiStrings = [];

    /// <summary>待追加的 AnsiString。攒着一起写，避免每次 Add 都整块复制（O(n²)）。</summary>
    private readonly List<byte[]> _pendingAnsi = new();
    private int _ansiTotalLen;
    private readonly List<(uint Key, uint Value)> _pairs = new();

    public static TagStore Parse(byte[] file, int storeStart, int storeEnd)
    {
        var s = storeStart + 4;

        var nums = new int[11];
        for (var i = 0; i < 11; i++)
        {
            nums[i] = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(s + i * 4, 4));
        }

        s += 44;

        var textBytes = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(s, 4));
        s += 4;

        var store = new TagStore { _nums = nums };
        store._texts = file.AsSpan(s, textBytes).ToArray();
        s += textBytes;

        store._numberlessNames = Walk(file, ref s, nums[0], 1);
        store._names = Walk(file, ref s, nums[1], 1);
        store._numberlessExportPaths = Walk(file, ref s, nums[2], 4);
        store._exportPaths = Walk(file, ref s, nums[3], 4);

        for (var i = 0; i < nums[5]; i++)
        {
            store._ansiOffsets.Add(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(s + i * 4, 4)));
        }

        s += nums[5] * 4;

        store._wideOffsets = file.AsSpan(s, nums[6] * 4).ToArray(); s += nums[6] * 4;
        store._ansiStrings = file.AsSpan(s, nums[7]).ToArray(); s += nums[7];
        store._ansiTotalLen = store._ansiStrings.Length;
        store._wideStrings = file.AsSpan(s, nums[8] * 2).ToArray(); s += nums[8] * 2;

        store._numberlessPairs = Walk(file, ref s, nums[9], 1, 4);
        var pairs = Walk(file, ref s, nums[10], 1, 4);

        for (var i = 0; i < nums[10]; i++)
        {
            var key = BinaryPrimitives.ReadUInt32LittleEndian(pairs.AsSpan(i * 8, 4));
            var val = BinaryPrimitives.ReadUInt32LittleEndian(pairs.AsSpan(i * 8 + 4, 4));
            store._pairs.Add((key, val));
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(s, 4)) != BinLayout.StoreEndMagic)
        {
            throw new InvalidOperationException("store EndMagic 不匹配，布局假设有误。");
        }

        return store;
    }

    /// <summary>
    /// 按变长 FName 走 count 条记录，返回原始字节。
    /// <paramref name="fnamesPerRecord"/> 是每条记录的 FName 个数；
    /// <paramref name="tailBytes"/> 是记录尾部定长字节数（pair 的 value 为 4）。
    /// </summary>
    private static byte[] Walk(byte[] file, ref int s, int count, int fnamesPerRecord, int tailBytes = 0)
    {
        var start = s;

        for (var i = 0; i < count; i++)
        {
            for (var f = 0; f < fnamesPerRecord; f++)
            {
                var idx = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(s, 4));
                s += 4;
                if ((idx & 0x80000000u) != 0)
                {
                    s += 4; // number
                }
            }

            s += tailBytes;
        }

        return file.AsSpan(start, s - start).ToArray();
    }

    /// <summary>追加一个 AnsiString，返回它的索引（用于拼 valueId）。</summary>
    public uint AddAnsiString(string value)
    {
        var raw = System.Text.Encoding.ASCII.GetBytes(value);

        _ansiOffsets.Add((uint)_ansiTotalLen);
        _ansiTotalLen += raw.Length + 1;
        _pendingAnsi.Add(raw);

        return (uint)(_ansiOffsets.Count - 1);
    }

    /// <summary>追加 pair，返回起始索引。</summary>
    public uint AddPairs(IReadOnlyList<(uint Key, uint Value)> pairs)
    {
        var start = (uint)_pairs.Count;
        _pairs.AddRange(pairs);
        return start;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream(_ansiStrings.Length + 1024);
        using var w = new BinaryWriter(ms);

        w.Write(BinLayout.StoreBeginMagic);

        var nums = (int[])_nums.Clone();
        nums[5] = _ansiOffsets.Count;
        nums[7] = _ansiTotalLen;
        nums[10] = _pairs.Count;

        foreach (var n in nums)
        {
            w.Write(n);
        }

        w.Write(_texts.Length);
        w.Write(_texts);

        w.Write(_numberlessNames);
        w.Write(_names);
        w.Write(_numberlessExportPaths);
        w.Write(_exportPaths);

        foreach (var off in _ansiOffsets)
        {
            w.Write(off);
        }

        w.Write(_wideOffsets);
        w.Write(_ansiStrings);
        foreach (var raw in _pendingAnsi)
        {
            w.Write(raw);
            w.Write((byte)0);
        }

        w.Write(_wideStrings);
        w.Write(_numberlessPairs);

        foreach (var (key, value) in _pairs)
        {
            w.Write(key);
            w.Write(value);
        }

        w.Write(BinLayout.StoreEndMagic);

        w.Flush();
        return ms.ToArray();
    }
}

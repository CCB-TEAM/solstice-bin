using System.Buffers.Binary;
using System.Text;
using ULocres;

namespace Solstice.Bin;

/// <summary>要追加的一条资产。所有名字必须能由 name batch 表达。</summary>
public sealed record NewAsset(
    string PackagePath,
    string ClassPackage,
    string AssetClass,
    string PackageName,
    string AssetName,
    uint PackageFlags,
    int ChunkId = 0,
    IReadOnlyList<KeyValuePair<string, string>>? Tags = null);

/// <summary>
/// name batch 的读写。
///
/// <para>布局：<c>uint32 count | uint32 numStringBytes | uint64 hashVersion | uint64 hashes[count]
/// | 2 字节头[count] | 字符串区</c>。</para>
///
/// <para>头部 = <c>(isUtf16 &lt;&lt; 7) | (len &gt;&gt; 8)</c> 与 <c>len &amp; 0xFF</c>；
/// ASCII 名的 len 是字节数，UTF-16 名是字符数。</para>
/// </summary>
public sealed class NameBatch
{
    public const ulong HashVersion = 0xC1640000UL;

    private readonly List<byte[]> _strings = new();
    private readonly List<ushort> _headers = new();
    private readonly List<string> _names = new();

    /// <summary>名字 → 索引。避免 FindOrAdd 的线性扫描（30k 名字时是 O(n²)）。</summary>
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Names => _names;
    public int Count => _names.Count;

    public static NameBatch Parse(byte[] file, int offset, out int end)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset, 4));
        var numStringBytes = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset + 4, 4));
        var headersOffset = offset + 16 + count * 8;
        var stringsOffset = headersOffset + count * 2;

        var batch = new NameBatch();
        var cursor = stringsOffset;

        for (var i = 0; i < count; i++)
        {
            var h = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(headersOffset + i * 2, 2));
            // 头部是「大端风格」：高字节在前
            var b0 = (byte)(h & 0xFF);
            var b1 = (byte)(h >> 8);

            var isUtf16 = ((b0 >> 7) & 1) == 1;
            var length = ((b0 & 0x7F) << 8) | b1;
            var byteLen = isUtf16 ? length * 2 : length;

            var raw = file.AsSpan(cursor, byteLen).ToArray();
            cursor += byteLen;

            var name = isUtf16 ? Encoding.Unicode.GetString(raw) : Encoding.ASCII.GetString(raw);

            batch._strings.Add(raw);
            batch._headers.Add(h);
            batch._names.Add(name);
            batch._index[name] = batch._names.Count - 1;
        }

        end = cursor;
        return batch;
    }

    /// <summary>名字已存在则返回索引；否则追加到末尾（原有索引不变）并返回新索引。</summary>
    public int FindOrAdd(string name)
    {
        if (_index.TryGetValue(name, out var existing))
        {
            return existing;
        }

        var isAscii = IsPureAscii(name);
        var raw = isAscii ? Encoding.ASCII.GetBytes(name) : Encoding.Unicode.GetBytes(name);
        var length = isAscii ? raw.Length : name.Length;

        if (length > 0x7FFF)
        {
            throw new InvalidOperationException($"名字过长：{length}");
        }

        var flag = isAscii ? 0 : 1;
        // 文件里的两字节顺序是 [B0][B1]，其中 B0=(flag<<7)|(len>>8)、B1=len&0xFF。
        // BinaryWriter 按小端写 ushort，所以这里要构造成 B0 | (B1<<8)。
        var b0 = (flag << 7) | ((length >> 8) & 0x7F);
        var b1 = length & 0xFF;
        var header = (ushort)(b0 | (b1 << 8));

        _strings.Add(raw);
        _headers.Add(header);
        _names.Add(name);

        var index = _names.Count - 1;
        _index[name] = index;

        return index;
    }

    /// <summary>序列化为 UE 格式。hashes 由 CityHash64(ASCII 小写的名字) 计算。</summary>
    public byte[] Serialize()
    {
        var num = _names.Count;
        var totalStrings = _strings.Sum(s => s.Length);

        using var ms = new MemoryStream(16 + num * 10 + totalStrings);
        using var w = new BinaryWriter(ms);

        w.Write(num);
        w.Write(totalStrings);
        w.Write(HashVersion);

        foreach (var name in _names)
        {
            var isAscii = IsPureAscii(name);
            var lower = ToLowerAsciiOnly(name);
            var bytes = isAscii ? Encoding.ASCII.GetBytes(lower) : Encoding.Unicode.GetBytes(lower);
            w.Write(CityHash.Hash64(bytes));
        }

        foreach (var h in _headers)
        {
            w.Write(h);
        }

        foreach (var s in _strings)
        {
            w.Write(s);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static bool IsPureAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>UE 的 FName 小写化只处理 ASCII A-Z。</summary>
    private static string ToLowerAsciiOnly(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= 'A' && chars[i] <= 'Z')
            {
                chars[i] = (char)(chars[i] + 32);
            }
        }

        return new string(chars);
    }
}

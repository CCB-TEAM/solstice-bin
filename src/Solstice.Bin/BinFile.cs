using System.Buffers.Binary;

namespace Solstice.Bin;

/// <summary>
/// 一份 AssetRegistry.bin。
///
/// <para>
/// 写回策略：**以原始字节为基底做拼接**，不重新序列化整份文件。
/// 未修改时逐字节原样返回；追加资产时，差异只出现在：
/// 资产计数（4 字节）、新增的资产记录、name batch 末尾追加的名字。
/// store 与原有资产记录的字节**完全不动**。
/// </para>
/// </summary>
public sealed class BinFile
{
    private readonly byte[] _original;

    private BinFile(byte[] original, BinLayout layout)
    {
        _original = original;
        Layout = layout;
    }

    public BinLayout Layout { get; }

    public int AssetCount => Layout.AssetCount;

    public static BinFile Load(byte[] data)
    {
        if (!BinLayout.TryParse(data, out var layout, out var error))
        {
            throw new InvalidOperationException($"无法解析 AssetRegistry.bin：{error}");
        }

        return new BinFile(data, layout!);
    }

    /// <summary>未修改写回：原样返回，逐字节一致。</summary>
    public byte[] Save() => (byte[])_original.Clone();

    /// <summary>
    /// 追加资产。新记录插在「资产计数」之后、原有第一条记录之前——
    /// UE 的读取器按顺序读 N 条，不要求排序，因此这是改动最小的插入点。
    ///
    /// <para>
    /// 限制：**不支持新增 tag**（句柄写 0）。需要 tag 的资产请先把键值并入 store。
    /// </para>
    /// </summary>
    public byte[] AppendAssets(IReadOnlyList<NewAsset> assets)
    {
        if (assets.Count == 0)
        {
            return Save();
        }

        var batch = NameBatch.Parse(_original, Layout.HeaderEnd, out _);

        // 只有真的带 tag 时才重建 store；否则原样保留，保证零差异。
        var needsStore = assets.Any(a => a.Tags is { Count: > 0 });
        var store = needsStore ? TagStore.Parse(_original, Layout.NameBatchEnd, Layout.StoreEnd) : null;

        // 先把所有需要的名字登记好（原有的复用，缺的追加到末尾）
        var records = new List<byte[]>(assets.Count);
        foreach (var a in assets)
        {
            var pathIdx = batch.FindOrAdd(a.PackagePath);
            var classPkgIdx = batch.FindOrAdd(a.ClassPackage);
            var classIdx = batch.FindOrAdd(a.AssetClass);
            var pkgIdx = batch.FindOrAdd(a.PackageName);
            var nameIdx = batch.FindOrAdd(a.AssetName);

            // tag：每个值进全局 store 的 AnsiString 表，pair 记 (name 索引, valueId)
            ulong handle = 0;
            if (a.Tags is { Count: > 0 })
            {
                var pairs = new List<(uint Key, uint Value)>(a.Tags.Count);
                foreach (var kv in a.Tags)
                {
                    var keyIdx = (uint)batch.FindOrAdd(kv.Key);
                    var valueIdx = store!.AddAnsiString(kv.Value);
                    pairs.Add((keyIdx, valueIdx << 3)); // 类型 0 = AnsiString
                }

                var start = store!.AddPairs(pairs);
                handle = (1UL << 63) | ((ulong)(uint)pairs.Count << 32) | start;
            }

            using var ms = new MemoryStream(64);
            using var w = new BinaryWriter(ms);

            w.Write(pathIdx);        // PackagePath
            w.Write(classPkgIdx);    // FTopLevelAssetPath.PackageName
            w.Write(classIdx);       // FTopLevelAssetPath.AssetName
            w.Write(pkgIdx);         // PackageName
            w.Write(nameIdx);        // AssetName

            // filterEditorOnly 时不写 OptionalOuterPath
            w.Write(handle);         // tag map handle
            w.Write(0);              // bundles 数量
            w.Write(1);              // ChunkIDs 数量
            w.Write(a.ChunkId);
            w.Write(a.PackageFlags);

            w.Flush();
            records.Add(ms.ToArray());
        }

        var newNameBatch = batch.Serialize();
        var newStore = store is null
            ? _original.AsSpan(Layout.NameBatchEnd, Layout.StoreEnd - Layout.NameBatchEnd).ToArray()
            : store.Serialize();

        // body：count + 新记录 + 原有内容（跳过原来的 4 字节 count）
        var oldBody = _original.AsSpan(Layout.StoreEnd).ToArray();
        var recordsSize = records.Sum(r => r.Length);

        var newBody = new byte[4 + recordsSize + (oldBody.Length - 4)];
        BinaryPrimitives.WriteInt32LittleEndian(newBody.AsSpan(0, 4), Layout.AssetCount + assets.Count);

        var pos = 4;
        foreach (var r in records)
        {
            r.CopyTo(newBody, pos);
            pos += r.Length;
        }

        oldBody.AsSpan(4).CopyTo(newBody.AsSpan(pos));

        // 拼装：header + 新 name batch + 原 store + 新 body
        var header = _original.AsSpan(0, Layout.HeaderEnd);
        var result = new byte[Layout.HeaderEnd + newNameBatch.Length + newStore.Length + newBody.Length];

        header.CopyTo(result);
        newNameBatch.CopyTo(result.AsSpan(Layout.HeaderEnd));
        newStore.CopyTo(result.AsSpan(Layout.HeaderEnd + newNameBatch.Length));
        newBody.CopyTo(result.AsSpan(Layout.HeaderEnd + newNameBatch.Length + newStore.Length));

        return result;
    }
}

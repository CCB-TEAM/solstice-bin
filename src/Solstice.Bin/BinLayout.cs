using System.Buffers.Binary;

namespace Solstice.Bin;

/// <summary>
/// AssetRegistry.bin 的分段布局。
///
/// <para>
/// 依据 UE 源码（<c>AssetRegistryArchive.cpp:220</c>，<c>~FAssetRegistryWriter</c>）：
/// <c>Header → NameBatch → (对齐) → Store → Body</c>。
/// </para>
///
/// <para>
/// 实测 KARDS 样本（version 21）：name batch 结束后**没有对齐填充**，store 的
/// BeginMagic 紧接其后。
/// </para>
/// </summary>
public sealed class BinLayout
{
    public const uint StoreBeginMagic = 0x12345679u;
    public const uint StoreEndMagic = 0x87654321u;
    public const int HeaderSize = 24;

    /// <summary>头部结束（即 name batch 起点）。</summary>
    public int HeaderEnd { get; private init; }

    /// <summary>name batch 结束（store 起点）。</summary>
    public int NameBatchEnd { get; private init; }

    /// <summary>store 结束（Body 起点）。</summary>
    public int StoreEnd { get; private init; }

    public int NameCount { get; private init; }
    public int NumStringBytes { get; private init; }
    public ulong HashVersion { get; private init; }
    public int Version { get; private init; }
    public bool FilterEditorOnly { get; private init; }

    /// <summary>Body 里的资产条数。</summary>
    public int AssetCount { get; private init; }

    public int FileLength { get; private init; }

    public static bool TryParse(byte[] file, out BinLayout? layout, out string? error)
    {
        layout = null;
        error = null;

        if (file.Length < HeaderSize + 4)
        {
            error = "文件太短，读不到头部。";
            return false;
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16, 4));
        var filterEditorOnly = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(20, 4)) != 0;

        var nameCount = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(HeaderSize, 4));
        if (nameCount <= 0)
        {
            error = $"name 条数异常：{nameCount}";
            return false;
        }

        var numStringBytes = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(HeaderSize + 4, 4));
        var hashVersion = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(HeaderSize + 8, 8));

        var nameBatchEnd = HeaderSize + 16 + nameCount * 8 + nameCount * 2 + numStringBytes;
        if (nameBatchEnd <= 0 || nameBatchEnd > file.Length - 8)
        {
            error = $"name batch 越界：{nameBatchEnd} / {file.Length}";
            return false;
        }

        // v21 实测无对齐填充；若首个 uint32 不是 BeginMagic，尝试按 8 字节对齐。
        var storeStart = nameBatchEnd;
        if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(storeStart, 4)) != StoreBeginMagic)
        {
            var aligned = (storeStart + 7) & ~7;
            if (aligned + 4 <= file.Length &&
                BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(aligned, 4)) == StoreBeginMagic)
            {
                storeStart = aligned;
            }
            else
            {
                error = $"在 {storeStart} 处找不到 store BeginMagic。";
                return false;
            }
        }

        if (!TryFindStoreEnd(file, storeStart, out var storeEnd, out var storeError))
        {
            error = storeError;
            return false;
        }

        if (storeEnd + 4 > file.Length)
        {
            error = "store 之后没有 body。";
            return false;
        }

        var assetCount = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(storeEnd, 4));

        layout = new BinLayout
        {
            HeaderEnd = HeaderSize,
            NameBatchEnd = nameBatchEnd,
            StoreEnd = storeEnd,
            NameCount = nameCount,
            NumStringBytes = numStringBytes,
            HashVersion = hashVersion,
            Version = version,
            FilterEditorOnly = filterEditorOnly,
            AssetCount = assetCount,
            FileLength = file.Length
        };

        return true;
    }

    /// <summary>
    /// 定位 store 的结束位置（即 EndMagic 之后、Body 起点）。
    ///
    /// 先按 11 个计数推算长度并校验 EndMagic；不一致时退化为向后扫描 EndMagic。
    /// </summary>
    private static bool TryFindStoreEnd(byte[] file, int storeStart, out int storeEnd, out string? error)
    {
        storeEnd = 0;
        error = null;

        var p = storeStart + 4; // 跳过 BeginMagic
        if (p + 44 + 4 > file.Length)
        {
            error = "store 头不完整。";
            return false;
        }

        Span<int> nums = stackalloc int[11];
        for (var i = 0; i < 11; i++)
        {
            nums[i] = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(p + i * 4, 4));
        }

        p += 44;

        var textBytes = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(p, 4));
        p += 4;

        if (textBytes < 0 || p + textBytes > file.Length)
        {
            error = $"store text 段越界：{textBytes}";
            return false;
        }

        p += textBytes;

        long computed = p;
        computed += (long)nums[0] * 4;      // numberlessNames
        computed += (long)nums[1] * 4;      // names
        computed += (long)nums[2] * 16;     // numberlessExportPaths (4 个 FName)
        computed += (long)nums[3] * 16;     // exportPaths
        computed += (long)nums[5] * 4;      // ansiOffsets
        computed += (long)nums[6] * 4;      // wideOffsets
        computed += nums[7];                // ansiStrings 字节
        computed += (long)nums[8] * 2;      // wideStrings（uint16 个数）
        computed += (long)nums[9] * 8;      // numberlessPairs
        computed += (long)nums[10] * 8;     // pairs

        if (computed + 4 <= file.Length &&
            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan((int)computed, 4)) == StoreEndMagic)
        {
            storeEnd = (int)computed + 4;
            return true;
        }

        // 退化：向后扫描 EndMagic
        for (var i = p; i + 4 <= file.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(i, 4)) == StoreEndMagic)
            {
                storeEnd = i + 4;
                return true;
            }
        }

        error = "找不到 store EndMagic。";
        return false;
    }
}

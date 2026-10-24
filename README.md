# solstice-bin

`AssetRegistry.bin`（UE **version 21**）的**容器层**读写：分段布局、名字表、标签存储、追加拼接。

不认识「资产是什么」——只认字节怎么排。语义层（`FAssetData` / 写入口径）在
[solstice-assetregistry](https://github.com/CCB-TEAM/solstice-assetregistry)。

## 分段布局

```
Header (24B)
NameBatch
Store
Body
```

| 段 | 内容 |
|---|---|
| Header | version / filterEditorOnly 等 |
| NameBatch | `count` `numStringBytes` `hashVersion(0xC1640000)` `hashes[count]` `2 字节头[count]` `字符串池` |
| Store | `BeginMagic(0x12345679)` … `EndMagic(0x87654321)`，标签名/导出路径/字符串/键值对 |
| Body | `AssetCount` + 资产记录 + `int64 depSectionSize` + 依赖段 + 包数据 |

实测：**name batch 结束后没有对齐填充**，Store 的 `BeginMagic` 紧接其后。

## 四个类型

| 类型 | 职责 |
|---|---|
| `BinLayout` | 解析三段边界与头部字段；`TryParse` 会校验 `EndMagic` 是否落在预期位置 |
| `NameBatch` | 名字表的解析与追加（`FindOrAdd`），哈希用 UE 的 CityHash64 |
| `TagStore` | 标签存储的解析与追加（`AddAnsiString` / `AddPairs`） |
| `BinFile` | 门面：`Load` → `Save()` / `AppendAssets()` |

## 两个设计约束

**1. 不改动就逐字节不变**

```csharp
public byte[] Save() => (byte[])_original.Clone();
```

没有待追加内容时直接返回原字节副本，绝不重新序列化。避免「只是读一下再保存」把文件写坏。

**2. 追加是重建三段再拼，不是原地插入**

原地插入要移动几十 MB 数据并重算所有偏移；这里分别重建 name batch / store / body，
再按 `Header + NameBatch + Store + Body` 顺序拼接，头部 24 字节原样复制。

## 用法

```csharp
using Solstice.Bin;

var file = BinFile.Load(File.ReadAllBytes("AssetRegistry.bin"));
Console.WriteLine($"资产 {file.AssetCount} 个");

var appended = file.AppendAssets(new[]
{
    new NewAsset(
        PackagePath: "/Game/Blueprints/Cards/demo",
        ClassPackage: "/Script/Engine",
        AssetClass: "BlueprintGeneratedClass",
        PackageName: "demo",
        AssetName: "Default__demo_C",
        PackageFlags: 0,
        ChunkId: 0,
        Tags: null)
});

File.WriteAllBytes("AssetRegistry.patched.bin", appended);
```

## 构建

```bash
git clone --recursive https://github.com/CCB-TEAM/solstice-bin.git
dotnet build src/Solstice.Bin
```

唯一第三方依赖是 [CCB-TEAM/ULocres](https://github.com/CCB-TEAM/ULocres)（CityHash64）。
name batch 的哈希必须与 UE 逐位一致，否则写出的 bin 游戏读不了——所以复用团队实现，不自己造。

## 限制

- 只验证过 version 21
- 只能往末尾追加新资产；删除或修改既有条目不在支持范围
- 不触碰既有 name batch 内容，只追加

## 许可

MIT

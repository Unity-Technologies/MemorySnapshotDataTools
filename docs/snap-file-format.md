# Unity Memory Snapshot Binary File Format (`.snap`)

This document describes the on-disk binary layout of Unity memory snapshot files
(`.snap`). It is derived from two primary sources:

- **[`Core/Parser/SnapReader.cs`](../Core/Parser/SnapReader.cs)** and
  **[`Core/Parser/SnapDataModel.cs`](../Core/Parser/SnapDataModel.cs)** — the
  parser in this tool.
- **`com.unity.memoryprofiler` package** — the authoritative Unity-side reader
  (`Editor/MemorySnapshot/Reader/QueriedSnapshot/FileReader.cs`,
  `Entry.cs`, `Block.cs`, `EntryType.cs`, `IReader.cs`).

All multi-byte integers are **little-endian**. Offsets are **absolute** byte
positions within the file unless noted otherwise.

> **Scope note:** The entry type catalog in Section 6 covers the complete
> canonical list from the Unity Memory Profiler package. This tool only reads
> a subset of those entries; entries not read by this tool are marked with †.

---

## High-Level Structure

A `.snap` file is composed of four logical regions:

```
┌─────────────────────────────────────────────────────────────┐
│  Header (4 bytes)                                           │
├─────────────────────────────────────────────────────────────┤
│  Data Blocks (variable)                                     │
│    Raw payload bytes, divided into fixed-size chunks.       │
│    Entries reference ranges inside these blocks.            │
├─────────────────────────────────────────────────────────────┤
│  Index Structures (variable)                                │
│    Chapter directory → Entry directory + Block section      │
│    Each structure is at an offset stored in the footer.     │
├─────────────────────────────────────────────────────────────┤
│  Footer (12 bytes)                                          │
│    Chapter directory offset (uint64) + Footer signature     │
└─────────────────────────────────────────────────────────────┘
```

| Region | Position | Size |
|--------|----------|------|
| Header | offset `0` | 4 bytes |
| Data blocks | offset `4` (scattered) | variable |
| Chapter directory | `chapterDirectoryOffset` (from footer) | variable |
| Block section | `blockSectionOffset` (from chapter directory) | variable |
| Footer | `fileLength - 12` | 12 bytes |

The minimum valid file size is **16 bytes**.

---

## Signatures and Version Constants

| Name | Value | Purpose |
|------|-------|---------|
| `HeaderSignature` | `0xAEABCDCD` | First 4 bytes of file |
| `DirectorySignature` | `0xCDCDAEAB` | First 4 bytes of chapter directory |
| `FooterSignature` | `0xABCDCDAE` | Last 4 bytes of file |
| `ChapterSectionVersion` | `0x20170724` | Expected version field in chapter directory |
| `BlockSectionVersion` | `0x20170724` | Expected version field in block section |

---

## 1. Header

Located at file offset `0`.

| Offset | Type | Value |
|--------|------|-------|
| `+0` | `uint32` | `0xAEABCDCD` (header signature) |

---

## 2. Footer

Located at the last 12 bytes of the file (`fileLength - 12`).

| Offset from EOF | Type | Description |
|-----------------|------|-------------|
| `-12` | `uint64` | Absolute file offset of the chapter directory |
| `-4` | `uint32` | `0xABCDCDAE` (footer signature) |

The chapter directory offset must be a positive value strictly less than the
file length.

---

## 3. Chapter Directory

Located at the absolute offset read from the footer.

| Relative offset | Type | Description |
|-----------------|------|-------------|
| `+0` | `uint32` | `0xCDCDAEAB` (directory signature) |
| `+4` | `uint32` | Chapter section version (`0x20170724`) |
| `+8` | `uint64` | Absolute offset of the block section |
| `+16` | — | Entry directory (inline, immediately follows) |

### 3a. Entry Directory (inline in Chapter Directory)

Starts at `chapterDirectoryOffset + 16`.

| Relative offset | Type | Description |
|-----------------|------|-------------|
| `+0` | `int32` | Entry count `N` (capped at `EntryType.Count` when reading) |
| `+4` | `int64 × N` | Absolute file offsets to entry metadata, one per `EntryType` index |

Each element of the offset array corresponds to the `EntryType` whose
integer value equals its zero-based array index. An offset of `0` means that
entry is not present in the snapshot.

---

## 4. Block Section

Located at the absolute offset stored in the chapter directory (`+8`).

| Relative offset | Type | Description |
|-----------------|------|-------------|
| `+0` | `uint32` | Block section version (`0x20170724`) |
| `+4` | `int32` | Block count `B` (must be ≥ 1) |
| `+8` | `int64 × B` | Absolute file offsets to block metadata records |

### 4a. Block Metadata Record

At each block metadata offset:

| Relative offset | Type | Description |
|-----------------|------|-------------|
| `+0` | `uint64` | Chunk size in bytes (must be > 0) |
| `+8` | `uint64` | Total payload bytes for this block |
| `+16` | `int64 × C` | Absolute file offsets for each chunk |

where `C = ceil(totalBytes / chunkSize)`.

Each **chunk** is a contiguous run of up to `chunkSize` bytes in the file
(the last chunk may be shorter). To read a range `[blockOffset, blockOffset + length)`
from a block, the reader computes `chunkIndex = blockOffset / chunkSize`,
seeks to `chunkOffsets[chunkIndex] + (blockOffset % chunkSize)`, and reads
across chunk boundaries if necessary.

---

## 5. Entry Metadata Records

Entries are the logical data sections of the snapshot (e.g. native object names,
heap bytes, type descriptions). Each non-zero entry offset from the entry
directory points to an entry metadata record.

### 5a. Common Header (all entry formats)

The `EntryHeader` struct is **18 bytes** with 2-byte pack alignment:

| Byte offset | Type | Description |
|-------------|------|-------------|
| `+0` | `uint16` | Entry format (`EntryFormat`) |
| `+2` | `uint32` | Block index (index into block array) |
| `+6` | `uint32` | `entriesMeta` — format-dependent (see below) |
| `+10` | `uint64` | `headerMeta` — format-dependent (see below) |

### 5b. Format 1 — `SingleElement`

One blob of bytes stored at a fixed block offset.

| Field | Meaning |
|-------|---------|
| `entriesMeta` | Byte length of the element |
| `headerMeta` | Block-relative byte offset of the data |

Element count is always **1**.

### 5c. Format 2 — `ConstantSizeElementArray`

A packed array of fixed-size elements stored contiguously.

| Field | Meaning |
|-------|---------|
| `entriesMeta` | Size in bytes of a single element |
| `headerMeta` | Number of elements |

Element `i` starts at block offset `i × entriesMeta`.

### 5d. Format 3 — `DynamicSizeElementArray`

A variable-length array where each element may differ in size.

**On-disk layout** (immediately after the 18-byte common header):

| Field | Type | Description |
|-------|------|-------------|
| `entriesMeta` | `uint32` | Element count `N` (in common header) |
| `headerMeta` | `uint64` | Block-relative start offset of element `0` (in common header) |
| offsets | `int64 × N` | Block-relative **end** offset of each element (i.e. start of next) |

The raw offset array encodes element boundaries:
- `offsets[0..N-2]` are the start offsets of elements `1` through `N-1`
  (equivalently, the end offsets of elements `0` through `N-2`).
- `offsets[N-1]` is the total payload size for this entry within the block
  (end offset of the last element).

> **In-memory rewrite:** Both readers (this tool and the Unity package) transform
> this into a start-indexed form after loading: `offsets[0]` is rewritten to
> `headerMeta` (start of element 0), elements `1..N-1` shift to `offsets[1..N-1]`,
> and `headerMeta` is replaced with the original `offsets[N-1]` (total size).
> After this transform, `offsets[i]` = start of element `i`, and `headerMeta` =
> end of the last element.

---

## 6. Entry Type Catalog

The following table lists all canonical `EntryType` values from the Unity Memory
Profiler package (`EntryType.cs`). Index values are sequential (no gaps) and must
match the native C++ capture enum order exactly — the package's `Count` sentinel
(93) is the total of the canonical list. Newer captures may append the optional
swapped-page entries 93–97 (see below); their presence does not change the format
version.

Entries marked **†** are present in the canonical format but not decoded by this
tool. The version column shows the minimum `FormatVersion` at which the entry was
introduced (`—` = present since the earliest supported version, `8`).

### Metadata

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 0 | `Metadata_Version` | `uint32` | SingleElement | — |
| 1 | `Metadata_RecordDate` | `int64` (.NET ticks, UTC) | SingleElement | — |
| 2 | `Metadata_UserMetadata` † | length-prefixed UTF-16 string data | DynamicSizeElementArray | 8 |
| 3 | `Metadata_CaptureFlags` † | `uint32` | SingleElement | — |
| 4 | `Metadata_VirtualMachineInformation` | `uint32[6]` (pointer size, object header size, array header size, array bounds offset, array size offset, allocation granularity) | SingleElement | — |

### Native Types

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 5 | `NativeTypes_Name` | UTF-8 string per native type | DynamicSizeElementArray | — |
| 6 | `NativeTypes_NativeBaseTypeArrayIndex` | `int32` per native type | ConstantSizeElementArray | — |

### Native Objects

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 7 | `NativeObjects_NativeTypeArrayIndex` | `int32` per object | ConstantSizeElementArray | — |
| 8 | `NativeObjects_HideFlags` | `int32` per object | ConstantSizeElementArray | — |
| 9 | `NativeObjects_Flags` | `int32` per object | ConstantSizeElementArray | — |
| 10 | `NativeObjects_InstanceId` | `int32` (v < 18) or `uint64` (v ≥ 18) per object | ConstantSizeElementArray | — |
| 11 | `NativeObjects_Name` | UTF-8 string per object | DynamicSizeElementArray | — |
| 12 | `NativeObjects_NativeObjectAddress` † | `uint64` per object | ConstantSizeElementArray | — |
| 13 | `NativeObjects_Size` | `uint64` per object | ConstantSizeElementArray | — |
| 14 | `NativeObjects_RootReferenceId` † | `int64` per object | ConstantSizeElementArray | — |

### GC Handles

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 15 | `GCHandles_Target` | `uint64` managed heap address per handle | ConstantSizeElementArray | — |

### Connections

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 16 | `Connections_From` | unified `int32` index (v < 10) or instance ID `int32`/`uint64` (v ≥ 10) | ConstantSizeElementArray | — |
| 17 | `Connections_To` | same encoding as `Connections_From` | ConstantSizeElementArray | — |

### Managed Heap Sections

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 18 | `ManagedHeapSections_StartAddress` | `uint64` base address per section (high bit masked off for v ≥ 12) | ConstantSizeElementArray | — |
| 19 | `ManagedHeapSections_Bytes` | raw bytes per section | DynamicSizeElementArray | — |

### Managed Stacks

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 20 | `ManagedStacks_StartAddress` † | `uint64` per stack section | ConstantSizeElementArray | — |
| 21 | `ManagedStacks_Bytes` † | raw bytes per stack section | DynamicSizeElementArray | — |

### Type Descriptions (Managed)

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 22 | `TypeDescriptions_Flags` | `int32` per type | ConstantSizeElementArray | — |
| 23 | `TypeDescriptions_Name` | UTF-8 string per type | DynamicSizeElementArray | — |
| 24 | `TypeDescriptions_Assembly` | UTF-8 string per type | DynamicSizeElementArray | — |
| 25 | `TypeDescriptions_FieldIndices` | `int32[]` per type (jagged) | DynamicSizeElementArray | — |
| 26 | `TypeDescriptions_StaticFieldBytes` † | raw bytes per type (static field data) | DynamicSizeElementArray | — |
| 27 | `TypeDescriptions_BaseOrElementTypeIndex` | `int32` per type | ConstantSizeElementArray | — |
| 28 | `TypeDescriptions_Size` | `int32` per type | ConstantSizeElementArray | — |
| 29 | `TypeDescriptions_TypeInfoAddress` | `uint64` per type | ConstantSizeElementArray | — |
| 30 | `TypeDescriptions_TypeIndex` † | `int32` per type | ConstantSizeElementArray | — |

### Field Descriptions

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 31 | `FieldDescriptions_Offset` | `int32` per field | ConstantSizeElementArray | — |
| 32 | `FieldDescriptions_TypeIndex` | `int32` per field | ConstantSizeElementArray | — |
| 33 | `FieldDescriptions_Name` | UTF-8 string per field | DynamicSizeElementArray | — |
| 34 | `FieldDescriptions_IsStatic` | `byte` per field (non-zero = static) | ConstantSizeElementArray | — |

### Native Root References

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 35 | `NativeRootReferences_Id` | `int64` per root | ConstantSizeElementArray | — |
| 36 | `NativeRootReferences_AreaName` | UTF-8 string per root | DynamicSizeElementArray | — |
| 37 | `NativeRootReferences_ObjectName` | UTF-8 string per root | DynamicSizeElementArray | — |
| 38 | `NativeRootReferences_AccumulatedSize` | `uint64` per root | ConstantSizeElementArray | — |

### Native Allocations

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 39 | `NativeAllocations_MemoryRegionIndex` | `int32` per allocation (−1 = none) | ConstantSizeElementArray | — |
| 40 | `NativeAllocations_RootReferenceId` † | `int64` per allocation | ConstantSizeElementArray | — |
| 41 | `NativeAllocations_AllocationSiteId` † | `int64` per allocation | ConstantSizeElementArray | — |
| 42 | `NativeAllocations_Address` | `uint64` per allocation | ConstantSizeElementArray | — |
| 43 | `NativeAllocations_Size` | `uint64` per allocation | ConstantSizeElementArray | — |
| 44 | `NativeAllocations_OverheadSize` | `uint64` per allocation | ConstantSizeElementArray | — |
| 45 | `NativeAllocations_PaddingSize` | `uint64` per allocation | ConstantSizeElementArray | — |

### Native Memory Regions

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 46 | `NativeMemoryRegions_Name` | UTF-8 string per region | DynamicSizeElementArray | — |
| 47 | `NativeMemoryRegions_ParentIndex` | `int32` per region (−1 = root) | ConstantSizeElementArray | — |
| 48 | `NativeMemoryRegions_AddressBase` | `uint64` per region | ConstantSizeElementArray | — |
| 49 | `NativeMemoryRegions_AddressSize` | `uint64` per region | ConstantSizeElementArray | — |
| 50 | `NativeMemoryRegions_FirstAllocationIndex` | `int32` per region (−1 = none) | ConstantSizeElementArray | — |
| 51 | `NativeMemoryRegions_NumAllocations` | `int32` per region | ConstantSizeElementArray | — |

### Native Memory Labels

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 52 | `NativeMemoryLabels_Name` | UTF-8 string per label | DynamicSizeElementArray | — |

### Native Allocation Sites and Callstacks

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 53 | `NativeAllocationSites_Id` † | `int64` per site | ConstantSizeElementArray | — |
| 54 | `NativeAllocationSites_MemoryLabelIndex` † | `int32` per site | ConstantSizeElementArray | — |
| 55 | `NativeAllocationSites_CallstackSymbols` † | `int64[]` per site (jagged symbol indices) | DynamicSizeElementArray | — |
| 56 | `NativeCallstackSymbol_Symbol` † | `uint64` per symbol | ConstantSizeElementArray | — |
| 57 | `NativeCallstackSymbol_ReadableStackTrace` † | UTF-8 string per symbol | DynamicSizeElementArray | — |

### Native Objects — GC Handle Index

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 58 | `NativeObjects_GCHandleIndex` | `int32` per object (−1 = none) | ConstantSizeElementArray | 10 |

### Profile Target Info and Memory Stats

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 59 | `ProfileTarget_Info` † | 512-byte `ProfileTargetInfo` struct | SingleElement | 11 |
| 60 | `ProfileTarget_MemoryStats` † | 264-byte `ProfileTargetMemoryStats` struct | SingleElement | 11 |
| 61 | `NativeMemoryLabels_Size` † | `uint64` per label | ConstantSizeElementArray | 12 |

### Scene Objects and Asset Bundles

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 62 | `SceneObjects_Name` † | UTF-8 string per scene object | DynamicSizeElementArray | 13 |
| 63 | `SceneObjects_Path` † | UTF-8 string per scene object | DynamicSizeElementArray | 13 |
| 64 | `SceneObjects_AssetPath` † | UTF-8 string per scene object | DynamicSizeElementArray | 13 |
| 65 | `SceneObjects_BuildIndex` † | `int32` per scene object | ConstantSizeElementArray | 13 |
| 66 | `SceneObjects_RootIdCounts` † | `int32` per scene object | ConstantSizeElementArray | 13 |
| 67 | `SceneObjects_RootIdOffsets` † | `int32` per scene object | ConstantSizeElementArray | 13 |
| 68 | `SceneObjects_RootIds` † | `int64` per root reference | ConstantSizeElementArray | 13 |

### Gfx Resources and Allocators

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 69 | `NativeMemoryLabels_AllocatorIdentifier` † | `int32` per label | ConstantSizeElementArray | 14 |
| 70 | `NativeGfxResourceReferences_Id` † | `uint64` per reference | ConstantSizeElementArray | 14 |
| 71 | `NativeGfxResourceReferences_Size` † | `uint64` per reference | ConstantSizeElementArray | 14 |
| 72 | `NativeGfxResourceReferences_RootId` † | `int64` per reference | ConstantSizeElementArray | 14 |
| 73 | `NativeAllocatorInfo_AllocatorName` † | UTF-8 string per allocator | DynamicSizeElementArray | 14 |
| 74 | `NativeAllocatorInfo_Identifier` † | `int32` per allocator | ConstantSizeElementArray | 14 |
| 75 | `NativeAllocatorInfo_UsedSize` † | `uint64` per allocator | ConstantSizeElementArray | 14 |
| 76 | `NativeAllocatorInfo_ReservedSize` † | `uint64` per allocator | ConstantSizeElementArray | 14 |
| 77 | `NativeAllocatorInfo_OverheadSize` † | `uint64` per allocator | ConstantSizeElementArray | 14 |
| 78 | `NativeAllocatorInfo_PeakUsedSize` † | `uint64` per allocator | ConstantSizeElementArray | 14 |
| 79 | `NativeAllocatorInfo_AllocationCount` † | `int32` per allocator | ConstantSizeElementArray | 14 |
| 80 | `NativeAllocatorInfo_Flags` † | `int32` per allocator | ConstantSizeElementArray | 14 |

### Object Metadata

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 81 | `ObjectMetaData_MetaDataBufferIndicies` † | `int32[]` per object (jagged) | DynamicSizeElementArray | 15 |
| 82 | `ObjectMetaData_MetaDataBuffer` † | raw bytes per metadata entry | DynamicSizeElementArray | 15 |

### System Memory Regions

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 83 | `SystemMemoryRegions_Address` † | `uint64` per region | ConstantSizeElementArray | 16 |
| 84 | `SystemMemoryRegions_Size` † | `uint64` per region | ConstantSizeElementArray | 16 |
| 85 | `SystemMemoryRegions_Resident` † | `uint64` per region | ConstantSizeElementArray | 16 |
| 86 | `SystemMemoryRegions_Type` † | `int32` per region | ConstantSizeElementArray | 16 |
| 87 | `SystemMemoryRegions_Name` † | UTF-8 string per region | DynamicSizeElementArray | 16 |

### System Memory Resident Pages

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 88 | `SystemMemoryResidentPages_Address` † | `uint64` per range | ConstantSizeElementArray | 17 |
| 89 | `SystemMemoryResidentPages_FirstPageIndex` † | `int32` per range | ConstantSizeElementArray | 17 |
| 90 | `SystemMemoryResidentPages_LastPageIndex` † | `int32` per range | ConstantSizeElementArray | 17 |
| 91 | `SystemMemoryResidentPages_PagesState` † | `byte` per range | ConstantSizeElementArray | 17 |
| 92 | `SystemMemoryResidentPages_PageSize` † | `uint64` per range | ConstantSizeElementArray | 17 |

### System Memory Swapped Pages (optional, appended at v17)

Five **optional** entries appended after the resident-page entries. The format
version stays **17** — readers must gate on **entry presence**, never on version
(v18 is `EntityIDAs8ByteStructs`). The encoding mirrors entries 88–92: the same
range geometry as the resident pages (one range per system region), a single
global bitset blob where bit *i* (LSB-first) means page *i* is **swapped**
(written to zRAM / swap, pagemap bit 62), and a page size stored as a raw byte
count (not an exponent). A page is either resident (pagemap bit 63) or swapped
(bit 62), never both; swapped bytes are a subset of (committed − resident).

| Index | Name | Element Type | Format | Version |
|-------|------|--------------|--------|---------|
| 93 | `SystemMemorySwappedPages_Address` | `uint64` per range | ConstantSizeElementArray | 17 (optional) |
| 94 | `SystemMemorySwappedPages_FirstPageIndex` | `int32` per range | ConstantSizeElementArray | 17 (optional) |
| 95 | `SystemMemorySwappedPages_LastPageIndex` | `int32` per range | ConstantSizeElementArray | 17 (optional) |
| 96 | `SystemMemorySwappedPages_PagesState` | single global bitset blob (bit i = page i swapped, LSB-first) | DynamicSizeElementArray | 17 (optional) |
| 97 | `SystemMemorySwappedPages_PageSize` | `uint32`/`uint64` byte count (element [0]) | ConstantSizeElementArray | 17 (optional) |

---

## 7. Format Version Gates

The format version is stored in `Metadata_Version` (entry index 0) and governs
how several entries are interpreted during decoding. Version numbers come from
`FormatVersion` in `IReader.cs`.

| Version | Constant name | Unity version introduced | Effect |
|---------|---------------|--------------------------|--------|
| 8 | `SnapshotMinSupportedFormatVersion` | — | Earliest supported version; metadata entries present. |
| 9 | `StreamingManagedMemoryCaptureFormatVersion` | — | Screenshot is independent from the snapshot; managed memory is streamed rather than fully embedded. |
| 10 | `NativeConnectionsAsInstanceIdsVersion` | 2019.3+ | `Connections_From/To` change from unified integer indices to native object instance IDs. `NativeObjects_GCHandleIndex` (58) is present. |
| 11 | `ProfileTargetInfoAndMemStatsVersion` | ~2021.2.0a12+ | `ProfileTarget_Info` (59) and `ProfileTarget_MemoryStats` (60) entries added. |
| 12 | `MemLabelSizeAndHeapIdVersion` | 2021.2.0a12, 2021.1.9, 2020.3.12f1, 2019.4.29f1+ | High bit (bit 63) of each `ManagedHeapSections_StartAddress` value encodes a heap type flag and must be masked off before use. `NativeMemoryLabels_Size` (61) added. |
| 13 | `SceneRootsAndAssetBundlesVersion` | 2022.2+ | `SceneObjects_*` entries (62–68) added for scene roots and asset bundle relations. |
| 14 | `GfxResourceReferencesAndAllocatorsVersion` | 2022.2+ | Gfx resource references, allocator info, and additional memory label data (entries 69–80) added. |
| 15 | `NativeObjectMetaDataVersion` | 2022.2+ | `ObjectMetaData_*` entries (81–82) added to allow per-native-object metadata buffers. |
| 16 | `SystemMemoryRegionsVersion` | 2022.2+ | `SystemMemoryRegions_*` entries (83–87) added. |
| 17 | `SystemMemoryResidentPagesVersion` | 2023.1+ | `SystemMemoryResidentPages_*` entries (88–92) added. `SystemMemorySwappedPages_*` entries (93–97) may additionally be present in newer captures **without a version bump** — gate on entry presence, not on version. |
| 18 | `EntityIDAs8ByteStructs` | 2023.1+ | `NativeObjects_InstanceId` and connection instance IDs widen from `int32` to `uint64`. |

---

## 8. Known Discrepancy — `NativeObjects_GCHandleIndex_Legacy`

This tool's `SnapDataModel.cs` defines `NativeObjects_GCHandleIndex_Legacy = 62`
as a fallback for reading GC handle indices on pre-v10 snapshots where index 58
may be absent. However, in the canonical Unity Memory Profiler package
(`EntryType.cs`), index 62 is `SceneObjects_Name` (introduced in v13).

In practice this is harmless for files this tool targets:

- On **v10–v12** files (where the legacy path is taken), index 62 is simply
  absent, so the read returns an empty array and the fallback silently
  succeeds.
- On **v13+** files, index 58 (`NativeObjects_GCHandleIndex`) is expected to be
  present, so the legacy path is not reached.

If support for v13+ files where index 58 is missing is ever needed, this
constant should be treated as unreliable.

---

## 9. Reading Algorithm (Pseudocode)

```
open file
assert file[0..4]   == HeaderSignature
assert file[-4..]   == FooterSignature
chapterDir = uint64 at file[-12..-4]     // read as unsigned, must be > 0 and < fileLength

seek chapterDir
assert read uint32 == DirectorySignature
assert read uint32 == ChapterSectionVersion
blockSectionOffset = read uint64

// Entry directory (immediately follows chapter directory header)
// starts at chapterDir + 4 + 4 + 8 = chapterDir + 16
entryCount = read int32                   // at chapterDir + 16
entryOffsets[0..entryCount] = read int64 × entryCount

// Block section (at blockSectionOffset)
assert read uint32 == BlockSectionVersion
blockCount = read int32                   // must be >= 1
blockMetaOffsets[0..blockCount] = read int64 × blockCount

for each blockMetaOffsets[i]:
    seek blockMetaOffsets[i]
    chunkSize  = read uint64
    totalBytes = read uint64
    numChunks  = ceil(totalBytes / chunkSize)
    chunkFileOffsets[i][0..numChunks] = read int64 × numChunks

for each entryOffsets[j] where entryOffsets[j] != 0:
    seek entryOffsets[j]
    // 18-byte EntryHeader (Size=18, Pack=2)
    format      = read uint16   // EntryFormat
    blockIndex  = read uint32
    entriesMeta = read uint32
    headerMeta  = read uint64
    if format == DynamicSizeElementArray:
        rawOffsets[0..entriesMeta] = read int64 × entriesMeta
        // rawOffsets stores end-offsets; rewrite to start-offsets in memory:
        totalSize    = rawOffsets[N-1]
        startOffsets[0]   = headerMeta          // start of element 0
        startOffsets[1..N-1] = rawOffsets[0..N-2]  // start of elements 1..N-1
        headerMeta   = totalSize

// Read element bytes:
function readElementBytes(entry, elementIndex):
    block = blocks[entry.blockIndex]
    if entry.format == SingleElement:
        readBlockRange(block, offset=entry.headerMeta, length=entry.entriesMeta)
    elif entry.format == ConstantSizeElementArray:
        readBlockRange(block, offset=elementIndex * entry.entriesMeta, length=entry.entriesMeta)
    elif entry.format == DynamicSizeElementArray:
        start = entry.startOffsets[elementIndex]
        end   = entry.startOffsets[elementIndex + 1]  // or headerMeta (totalSize) if last
        readBlockRange(block, offset=start, length=end - start)
```

---

## See Also

- [`Core/Parser/SnapReader.cs`](../Core/Parser/SnapReader.cs) — low-level binary reader implementation
- [`Core/Parser/SnapDataModel.cs`](../Core/Parser/SnapDataModel.cs) — entry type enum (tool subset) and decoded model types
- [`Core/Parser/SnapSectionDecoders.cs`](../Core/Parser/SnapSectionDecoders.cs) — maps entries to `DecodedSnapshot`, including version-specific decoding logic
- `com.unity.memoryprofiler@1.12.0` package `Editor/MemorySnapshot/Reader/QueriedSnapshot/FileReader.cs` — authoritative Unity-side reader with embedded format diagram
- `com.unity.memoryprofiler@1.12.0` package `Editor/MemorySnapshot/Reader/QueriedSnapshot/EntryType.cs` — canonical entry type enum (all 93 entries)
- `com.unity.memoryprofiler@1.12.0` package `Editor/MemorySnapshot/Reader/IReader.cs` — `FormatVersion` enum with all 11 version constants

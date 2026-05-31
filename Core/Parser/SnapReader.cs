using System.Buffers.Binary;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Low-level reader for Unity memory snapshot (.snap) files. Parses the file header, chapter directory,
/// block and entry metadata, and provides typed access to snapshot entries (primitive arrays, strings, dynamic arrays).
/// Call <see cref="Open"/> to create an instance; use <see cref="HasEntry"/> and <see cref="GetEntryCount"/> before reading.
/// </summary>
internal sealed class SnapReader : IDisposable
{
    private const uint HeaderSignature = 0xAEABCDCD;
    private const uint DirectorySignature = 0xCDCDAEAB;
    private const uint FooterSignature = 0xABCDCDAE;
    private const uint ChapterSectionVersion = 0x20170724;
    private const uint BlockSectionVersion = 0x20170724;

    private readonly FileStream _stream;
    private readonly BinaryReader _reader;
    private readonly EntryData[] _entries;
    private readonly BlockData[] _blocks;

    private SnapReader(FileStream stream, BinaryReader reader, EntryData[] entries, BlockData[] blocks)
    {
        _stream = stream;
        _reader = reader;
        _entries = entries;
        _blocks = blocks;
    }

    /// <summary>
    /// Opens a snapshot file and initializes the reader. Validates header/footer signatures and chapter directory, then loads block and entry metadata.
    /// </summary>
    /// <param name="snapshotPath">Path to the .snap file.</param>
    /// <returns>A configured <see cref="SnapReader"/> ready for entry reads.</returns>
    /// <exception cref="InvalidOperationException">If file format is invalid or unsupported.</exception>
    public static SnapReader Open(string snapshotPath)
    {
        var stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            ValidateSignatures(reader, stream.Length, out var chapterDirectoryOffset);

            stream.Position = chapterDirectoryOffset;
            var directorySig = reader.ReadUInt32();
            var chapterVersion = reader.ReadUInt32();
            if (directorySig != DirectorySignature)
                throw new InvalidOperationException($"Invalid snapshot chapter directory signature: 0x{directorySig:X8}");
            if (chapterVersion != ChapterSectionVersion)
                throw new InvalidOperationException($"Unsupported chapter section version: 0x{chapterVersion:X8}");

            var blockSectionOffset = reader.ReadInt64();
            var entryDirectoryOffset = chapterDirectoryOffset + sizeof(uint) + sizeof(uint) + sizeof(long);
            var entryOffsets = ReadEntryOffsets(reader, entryDirectoryOffset);
            var blockOffsets = ReadBlockOffsets(reader, blockSectionOffset);
            var blocks = ReadBlocks(reader, blockOffsets);
            var entries = ReadEntries(reader, entryOffsets);

            return new SnapReader(stream, reader, entries, blocks);
        }
        catch
        {
            reader.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Returns whether the snapshot contains data for the given entry type.</summary>
    /// <param name="entryType">The snapshot entry type to check.</param>
    /// <returns>True if the entry is present and defined.</returns>
    public bool HasEntry(SnapEntryType entryType) => (int)entryType < _entries.Length && _entries[(int)entryType].IsDefined;

    /// <summary>Returns the number of elements in the given entry (1 for single-element, array length otherwise).</summary>
    /// <param name="entryType">The snapshot entry type.</param>
    /// <returns>Element count for the entry.</returns>
    /// <exception cref="InvalidOperationException">If the entry is missing or index out of range.</exception>
    public uint GetEntryCount(SnapEntryType entryType)
    {
        EnsureDefined(entryType);
        return _entries[(int)entryType].Count;
    }

    /// <summary>Reads the snapshot format version number from metadata.</summary>
    /// <returns>Format version (e.g. 10, 18).</returns>
    public uint ReadMetadataVersion() => ReadSingle<uint>(SnapEntryType.Metadata_Version);

    /// <summary>Reads the snapshot record date as .NET ticks (UTC), or 0 if the entry is missing.</summary>
    /// <returns>Ticks value or 0.</returns>
    public long ReadMetadataRecordDateTicks()
    {
        if (!HasEntry(SnapEntryType.Metadata_RecordDate))
            return 0;
        return ReadSingle<long>(SnapEntryType.Metadata_RecordDate);
    }

    /// <summary>
    /// Reads an entry as an array of unmanaged primitives. Supports single-element, constant-size, and dynamic-size entry formats.
    /// </summary>
    /// <typeparam name="T">Unmanaged type (e.g. int, long, ulong).</typeparam>
    /// <param name="entryType">The entry to read.</param>
    /// <returns>Array of values; may be empty if the entry has no data.</returns>
    /// <exception cref="InvalidOperationException">If entry is missing, format is unsupported, or size mismatch.</exception>
    public T[] ReadPrimitiveArray<T>(SnapEntryType entryType) where T : unmanaged
    {
        EnsureDefined(entryType);
        var entry = _entries[(int)entryType];
        var elementSize = Marshal.SizeOf<T>();
        if (entry.Format == SnapEntryFormat.SingleElement)
        {
            var bytes = ReadConstEntryBytes(entry, 0, 1);
            if (bytes.Length == 0)
                return [];
            if (bytes.Length % elementSize != 0)
            {
                throw new InvalidOperationException(
                    $"Entry '{entryType}' byte-size {bytes.Length} is not divisible by element size {elementSize}.");
            }

            var singleCount = bytes.Length / elementSize;
            var output = new T[singleCount];
            bytes.AsSpan().CopyTo(MemoryMarshal.AsBytes<T>(output.AsSpan()));
            return output;
        }

        var count = checked((int)entry.Count);
        if (count == 0)
            return [];

        if (entry.Format == SnapEntryFormat.ConstantSizeElementArray)
        {
            var expectedBytes = checked(count * elementSize);
            var bytes = ReadConstEntryBytes(entry, 0, count);
            if (bytes.Length != expectedBytes)
                throw new InvalidOperationException($"Entry '{entryType}' byte-size mismatch. expected={expectedBytes}, actual={bytes.Length}");

            var output = new T[count];
            var source = bytes.AsSpan();
            var destination = MemoryMarshal.AsBytes<T>(output.AsSpan());
            source.CopyTo(destination);
            return output;
        }

        if (entry.Format == SnapEntryFormat.DynamicSizeElementArray)
        {
            var output = new T[count];
            Span<byte> smallBuffer = stackalloc byte[256];
            for (var i = 0; i < count; i++)
            {
                GetDynamicElementBounds(entry, i, out var start, out var length);
                if (length != elementSize)
                    throw new InvalidOperationException(
                        $"Dynamic entry '{entryType}' element {i} has unexpected size {length}, expected {elementSize}.");

                Span<byte> bytes = elementSize <= 256 ? smallBuffer[..elementSize] : new byte[elementSize];
                ReadBlockRange(_blocks[checked((int)entry.BlockIndex)], start, bytes[..elementSize]);
                output[i] = MemoryMarshal.Read<T>(bytes);
            }
            return output;
        }

        throw new InvalidOperationException($"Entry '{entryType}' has unsupported format '{entry.Format}'.");
    }

    /// <summary>
    /// Reads an entry as an array of UTF-8 strings. The entry must be in dynamic-size element array format.
    /// </summary>
    /// <param name="entryType">The entry to read.</param>
    /// <returns>Array of decoded strings.</returns>
    /// <exception cref="InvalidOperationException">If entry is missing or not a dynamic string array.</exception>
    public string[] ReadUtf8StringArray(SnapEntryType entryType)
    {
        EnsureDefined(entryType);
        var entry = _entries[(int)entryType];
        if (entry.Format != SnapEntryFormat.DynamicSizeElementArray)
            throw new InvalidOperationException($"Entry '{entryType}' is not a dynamic string array.");

        var count = checked((int)entry.Count);
        var output = new string[count];
        for (var i = 0; i < count; i++)
        {
            GetDynamicElementBounds(entry, i, out var start, out var length);
            if (length == 0)
            {
                output[i] = string.Empty;
                continue;
            }

            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                ReadBlockRange(_blocks[checked((int)entry.BlockIndex)], start, rented.AsSpan(0, length));
                output[i] = Encoding.UTF8.GetString(rented, 0, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        return output;
    }

    /// <summary>
    /// Reads an entry as an array of variable-length byte arrays (dynamic-size element array format).
    /// </summary>
    /// <param name="entryType">The entry to read.</param>
    /// <returns>Array of byte arrays, one per element.</returns>
    /// <exception cref="InvalidOperationException">If entry is missing or not dynamic.</exception>
    public byte[][] ReadDynamicByteArrays(SnapEntryType entryType)
    {
        EnsureDefined(entryType);
        var entry = _entries[(int)entryType];
        if (entry.Format != SnapEntryFormat.DynamicSizeElementArray)
            throw new InvalidOperationException($"Entry '{entryType}' is not a dynamic array.");

        var count = checked((int)entry.Count);
        var output = new byte[count][];
        for (var i = 0; i < count; i++)
            output[i] = ReadDynamicElementBytes(entry, i);
        return output;
    }

    /// <summary>
    /// Reads an entry as an array of variable-length primitive arrays. Each element is a byte array decoded into T[].
    /// </summary>
    /// <typeparam name="T">Unmanaged element type.</typeparam>
    /// <param name="entryType">The entry to read.</param>
    /// <returns>Jagged array of primitive arrays.</returns>
    /// <exception cref="InvalidOperationException">If entry is missing or element length is not divisible by sizeof(T).</exception>
    public T[][] ReadDynamicPrimitiveArrays<T>(SnapEntryType entryType) where T : unmanaged
    {
        var bytes = ReadDynamicByteArrays(entryType);
        var output = new T[bytes.Length][];
        var elementSize = Marshal.SizeOf<T>();
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i].Length % elementSize != 0)
            {
                throw new InvalidOperationException(
                    $"Dynamic entry '{entryType}' element {i} length {bytes[i].Length} is not divisible by element size {elementSize}.");
            }

            var elementCount = bytes[i].Length / elementSize;
            var row = new T[elementCount];
            bytes[i].AsSpan().CopyTo(MemoryMarshal.AsBytes<T>(row.AsSpan()));
            output[i] = row;
        }

        return output;
    }

    /// <summary>Releases the file stream and binary reader.</summary>
    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    private T ReadSingle<T>(SnapEntryType entryType) where T : unmanaged
    {
        var arr = ReadPrimitiveArray<T>(entryType);
        if (arr.Length == 0)
            throw new InvalidOperationException($"Entry '{entryType}' has no elements.");
        return arr[0];
    }

    /// <summary>
    /// Reads the raw byte blob for a single-element entry, or the first element of a constant-size array.
    /// </summary>
    internal byte[] ReadSingleElementBytes(SnapEntryType entryType)
    {
        EnsureDefined(entryType);
        var entry = _entries[(int)entryType];
        if (entry.Format == SnapEntryFormat.SingleElement)
            return ReadConstEntryBytes(entry, 0, 1);

        if (entry.Format == SnapEntryFormat.ConstantSizeElementArray && entry.Count > 0)
            return ReadConstEntryBytes(entry, 0, 1);

        if (entry.Format == SnapEntryFormat.DynamicSizeElementArray && entry.Count > 0)
        {
            GetDynamicElementBounds(entry, 0, out var start, out var length);
            return ReadBlockRange(_blocks[checked((int)entry.BlockIndex)], start, checked((int)length));
        }

        throw new InvalidOperationException($"Entry '{entryType}' cannot be read as a single blob.");
    }

    /// <summary>
    /// Reads raw bytes for a contiguous range of constant-size elements (or the single-element blob).
    /// Used for resident page bitmaps stored as one constant-size blob per entry.
    /// </summary>
    internal byte[] ReadConstantRangeBytes(SnapEntryType entryType, int startIndex, int count)
    {
        EnsureDefined(entryType);
        var entry = _entries[(int)entryType];
        return ReadConstEntryBytes(entry, startIndex, count);
    }

    /// <summary>
    /// Reads up to <paramref name="byteCount"/> leading bytes of an entry's data block, starting at the
    /// first element. Used for fixed-layout struct blobs (e.g. ProfileTarget_MemoryStats) whose stored
    /// element size/count does not describe the full struct, mirroring Unity's <c>ReadUnsafe(..., sizeof(T), 0, 1)</c>.
    /// </summary>
    internal byte[] ReadEntryLeadingBytes(SnapEntryType entryType, int byteCount)
    {
        EnsureDefined(entryType);
        var entry = _entries[(int)entryType];
        if (entry.Format == SnapEntryFormat.DynamicSizeElementArray)
            throw new InvalidOperationException($"Entry '{entryType}' is dynamic; use a dynamic read.");

        var start = entry.Format == SnapEntryFormat.SingleElement ? checked((long)entry.HeaderMeta) : 0L;
        var block = _blocks[checked((int)entry.BlockIndex)];
        var available = checked((long)block.TotalBytes) - start;
        var length = (int)Math.Max(0, Math.Min(byteCount, available));
        return ReadBlockRange(block, start, length);
    }

    private byte[] ReadConstEntryBytes(EntryData entry, int startIndex, int count)
    {
        if (entry.Format == SnapEntryFormat.SingleElement && startIndex == 0 && count == 1)
            return ReadBlockRange(_blocks[checked((int)entry.BlockIndex)], checked((long)entry.HeaderMeta), checked((int)entry.EntriesMeta));

        if (entry.Format != SnapEntryFormat.ConstantSizeElementArray)
            throw new InvalidOperationException($"Entry '{entry.EntryType}' is not a constant-size array.");

        var byteOffset = checked((long)entry.EntriesMeta * startIndex);
        var byteLength = checked((int)(entry.EntriesMeta * (uint)count));
        return ReadBlockRange(_blocks[checked((int)entry.BlockIndex)], byteOffset, byteLength);
    }

    private byte[] ReadDynamicElementBytes(EntryData entry, int elementIndex)
    {
        GetDynamicElementBounds(entry, elementIndex, out var start, out var length);
        return ReadBlockRange(_blocks[checked((int)entry.BlockIndex)], start, length);
    }

    private static void GetDynamicElementBounds(EntryData entry, int elementIndex, out long start, out int length)
    {
        if (entry.DynamicOffsets == null)
            throw new InvalidOperationException($"Entry '{entry.EntryType}' has no dynamic offsets.");
        if (elementIndex < 0 || elementIndex >= entry.DynamicOffsets.Length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));

        start = entry.DynamicOffsets[elementIndex];
        var end = elementIndex == entry.DynamicOffsets.Length - 1
            ? checked((long)entry.HeaderMeta)
            : entry.DynamicOffsets[elementIndex + 1];
        length = checked((int)(end - start));
        if (length < 0)
            throw new InvalidOperationException($"Entry '{entry.EntryType}' has invalid dynamic offsets.");
    }

    private byte[] ReadBlockRange(BlockData block, long blockRelativeOffset, int byteLength)
    {
        var output = new byte[byteLength];
        if (byteLength == 0)
            return output;

        ReadBlockRange(block, blockRelativeOffset, output);
        return output;
    }

    private void ReadBlockRange(BlockData block, long blockRelativeOffset, Span<byte> destination)
    {
        if (destination.Length == 0)
            return;

        var chunkSize = checked((long)block.ChunkSize);
        var readCursor = 0;
        var offset = blockRelativeOffset;
        while (readCursor < destination.Length)
        {
            var chunkIndex = checked((int)(offset / chunkSize));
            if (chunkIndex < 0 || chunkIndex >= block.ChunkOffsets.Length)
                throw new InvalidOperationException("Chunk index out of range while reading snapshot block.");

            var offsetInChunk = offset % chunkSize;
            var availableInChunk = chunkSize - offsetInChunk;
            var toRead = (int)Math.Min(availableInChunk, destination.Length - readCursor);
            var absoluteFileOffset = checked(block.ChunkOffsets[chunkIndex] + offsetInChunk);

            _stream.Position = absoluteFileOffset;
            var read = _stream.Read(destination.Slice(readCursor, toRead));
            if (read != toRead)
                throw new InvalidOperationException("Unexpected EOF while reading snapshot block.");

            readCursor += toRead;
            offset += toRead;
        }
    }

    private void EnsureDefined(SnapEntryType entryType)
    {
        var idx = (int)entryType;
        if (idx < 0 || idx >= _entries.Length)
            throw new InvalidOperationException($"Entry type index out of range: {entryType}");
        if (!_entries[idx].IsDefined)
            throw new InvalidOperationException($"Entry '{entryType}' is missing in this snapshot.");
    }

    private static void ValidateSignatures(BinaryReader reader, long fileLength, out long chapterDirectoryOffset)
    {
        if (fileLength < 16)
            throw new InvalidOperationException("Snapshot file is too small.");

        reader.BaseStream.Position = 0;
        var headerSig = reader.ReadUInt32();
        if (headerSig != HeaderSignature)
            throw new InvalidOperationException($"Invalid snapshot header signature: 0x{headerSig:X8}");

        reader.BaseStream.Position = fileLength - sizeof(uint);
        var footerSig = reader.ReadUInt32();
        if (footerSig != FooterSignature)
            throw new InvalidOperationException($"Invalid snapshot footer signature: 0x{footerSig:X8}");

        reader.BaseStream.Position = fileLength - sizeof(uint) - sizeof(long);
        chapterDirectoryOffset = reader.ReadInt64();
        if (chapterDirectoryOffset <= 0 || chapterDirectoryOffset >= fileLength)
            throw new InvalidOperationException("Snapshot chapter directory offset is invalid.");
    }

    private static long[] ReadEntryOffsets(BinaryReader reader, long entryDirectoryOffset)
    {
        reader.BaseStream.Position = entryDirectoryOffset;
        var entryCount = reader.ReadInt32();
        if (entryCount <= 0)
            return [];

        var offsets = new long[entryCount];
        for (var i = 0; i < entryCount; i++)
            offsets[i] = reader.ReadInt64();
        return offsets;
    }

    private static long[] ReadBlockOffsets(BinaryReader reader, long blockSectionOffset)
    {
        reader.BaseStream.Position = blockSectionOffset;
        var blockVersion = reader.ReadUInt32();
        if (blockVersion != BlockSectionVersion)
            throw new InvalidOperationException($"Unsupported block section version: 0x{blockVersion:X8}");

        var blockCount = reader.ReadInt32();
        if (blockCount <= 0)
            throw new InvalidOperationException("Snapshot block section has no blocks.");

        var offsets = new long[blockCount];
        for (var i = 0; i < blockCount; i++)
            offsets[i] = reader.ReadInt64();
        return offsets;
    }

    private static BlockData[] ReadBlocks(BinaryReader reader, long[] blockOffsets)
    {
        var blocks = new BlockData[blockOffsets.Length];
        for (var i = 0; i < blockOffsets.Length; i++)
        {
            reader.BaseStream.Position = blockOffsets[i];
            var chunkSize = reader.ReadUInt64();
            var totalBytes = reader.ReadUInt64();
            if (chunkSize == 0)
                throw new InvalidOperationException($"Block {i} has zero chunk size.");

            var offsetCount = (int)(totalBytes / chunkSize + (totalBytes % chunkSize == 0 ? 0UL : 1UL));
            var chunkOffsets = new long[offsetCount];
            for (var c = 0; c < offsetCount; c++)
                chunkOffsets[c] = reader.ReadInt64();

            blocks[i] = new BlockData(chunkSize, totalBytes, chunkOffsets);
        }

        return blocks;
    }

    private static EntryData[] ReadEntries(BinaryReader reader, long[] entryOffsets)
    {
        var entries = new EntryData[entryOffsets.Length];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = EntryData.Undefined((SnapEntryType)i);

        for (var i = 0; i < entryOffsets.Length; i++)
        {
            var offset = entryOffsets[i];
            if (offset == 0)
                continue;

            reader.BaseStream.Position = offset;
            var format = (SnapEntryFormat)reader.ReadUInt16();
            var blockIndex = reader.ReadUInt32();
            var entriesMeta = reader.ReadUInt32();
            var headerMeta = reader.ReadUInt64();
            long[]? dynamicOffsets = null;

            if (format == SnapEntryFormat.DynamicSizeElementArray)
            {
                var count = checked((int)entriesMeta);
                dynamicOffsets = new long[count];
                for (var d = 0; d < count; d++)
                    dynamicOffsets[d] = reader.ReadInt64();

                if (count > 0)
                {
                    var totalSize = dynamicOffsets[count - 1];
                    for (var d = count - 1; d >= 1; d--)
                        dynamicOffsets[d] = dynamicOffsets[d - 1];
                    dynamicOffsets[0] = checked((long)headerMeta);
                    headerMeta = checked((ulong)totalSize);
                }
            }

            entries[i] = new EntryData(
                (SnapEntryType)i,
                true,
                format,
                blockIndex,
                entriesMeta,
                headerMeta,
                dynamicOffsets);
        }

        return entries;
    }

    private sealed class BlockData
    {
        public BlockData(ulong chunkSize, ulong totalBytes, long[] chunkOffsets)
        {
            ChunkSize = chunkSize;
            TotalBytes = totalBytes;
            ChunkOffsets = chunkOffsets;
        }

        public ulong ChunkSize { get; }
        public ulong TotalBytes { get; }
        public long[] ChunkOffsets { get; }
    }

    private sealed class EntryData
    {
        public EntryData(
            SnapEntryType entryType,
            bool isDefined,
            SnapEntryFormat format,
            uint blockIndex,
            uint entriesMeta,
            ulong headerMeta,
            long[]? dynamicOffsets)
        {
            EntryType = entryType;
            IsDefined = isDefined;
            Format = format;
            BlockIndex = blockIndex;
            EntriesMeta = entriesMeta;
            HeaderMeta = headerMeta;
            DynamicOffsets = dynamicOffsets;
        }

        public static EntryData Undefined(SnapEntryType type) => new(type, false, SnapEntryFormat.Undefined, 0, 0, 0, null);

        public SnapEntryType EntryType { get; }
        public bool IsDefined { get; }
        public SnapEntryFormat Format { get; }
        public uint BlockIndex { get; }
        public uint EntriesMeta { get; }
        public ulong HeaderMeta { get; }
        public long[]? DynamicOffsets { get; }
        public uint Count => Format switch
        {
            SnapEntryFormat.SingleElement => 1,
            SnapEntryFormat.ConstantSizeElementArray => (uint)HeaderMeta,
            SnapEntryFormat.DynamicSizeElementArray => EntriesMeta,
            _ => 0
        };
    }
}


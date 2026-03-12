using System.Buffers.Binary;
using MemorySnapshotDataTools;

namespace MemorySnapshotDataTools.Parser;

/// <summary>
/// Result of crawling the managed heap: discovered managed objects, connections between them (and to native objects), and address-to-index map.
/// </summary>
internal sealed class ManagedCrawlResult
{
    /// <summary>Discovered managed heap objects (index, address, size, type, native link).</summary>
    public List<ManagedObjectRow> ManagedObjects { get; } = [];

    /// <summary>Edges from the crawl: managed-to-managed, managed-to-native, native-to-managed.</summary>
    public List<ConnectionRow> ManagedConnections { get; } = [];

    /// <summary>Map from managed heap address to <see cref="ManagedObjectRow.ManagedObjectIndex"/>.</summary>
    public Dictionary<ulong, int> ManagedIndexByAddress { get; } = [];
}

/// <summary>
/// Crawls the managed heap from a <see cref="DecodedSnapshot"/>: starts from GC handle roots, follows references,
/// parses object headers and fields/arrays, and produces <see cref="ManagedCrawlResult"/> with objects and connections.
/// </summary>
internal sealed class ManagedSnapshotCrawler
{
    private const int TypeFlagValueType = 1 << 0;
    private const int TypeFlagArray = 1 << 1;
    private const int TypeFlagArrayRankMask = unchecked((int)0xFFFF0000);

    private readonly DecodedSnapshot _snapshot;
    private readonly DecodedVirtualMachineInfo _vm;
    private readonly List<ManagedHeapSection> _sections;
    private readonly Dictionary<ulong, int> _typeInfoToIndex;
    private readonly Dictionary<ulong, long> _managedAddressToNativeObjectIndex;
    private readonly Dictionary<int, int[]> _instanceFieldIndexCache = [];
    private readonly Queue<ulong> _crawlQueue = new();
    private readonly ManagedCrawlResult _result = new();
    private readonly HashSet<EdgeKey> _edgeDedup = [];

    /// <summary>Builds the crawler for the given decoded snapshot. Validates VM pointer size and builds heap sections and type/native maps.</summary>
    /// <param name="snapshot">Decoded snapshot (must include heap sections and type descriptions).</param>
    /// <exception cref="InvalidOperationException">If pointer size is not 4 or 8.</exception>
    public ManagedSnapshotCrawler(DecodedSnapshot snapshot)
    {
        _snapshot = snapshot;
        _vm = snapshot.VirtualMachineInformation;
        if (_vm.PointerSize is not 4 and not 8)
            throw new InvalidOperationException($"Unsupported VM pointer size: {_vm.PointerSize}");

        _sections = BuildManagedHeapSections(snapshot);
        _typeInfoToIndex = BuildTypeInfoIndex(snapshot);
        _managedAddressToNativeObjectIndex = BuildManagedAddressToNativeMap(snapshot);
    }

    /// <summary>
    /// Crawls the managed heap starting from GC handle roots, discovers all reachable managed objects and their references, and returns the result.
    /// </summary>
    /// <param name="snapshot">Decoded snapshot with heap sections and type metadata.</param>
    /// <returns>Managed objects, connections, and address-to-index map.</returns>
    public static ManagedCrawlResult Crawl(DecodedSnapshot snapshot)
    {
        var crawler = new ManagedSnapshotCrawler(snapshot);
        return crawler.CrawlInternal();
    }

    private ManagedCrawlResult CrawlInternal()
    {
        for (var gcHandleIndex = 0; gcHandleIndex < _snapshot.GcHandleTargets.Length; gcHandleIndex++)
        {
            var address = _snapshot.GcHandleTargets[gcHandleIndex];
            if (address == 0)
                continue;
            TryEnsureManagedObject(address, $"gc-handle[{gcHandleIndex}]");
        }

        while (_crawlQueue.Count > 0)
        {
            var address = _crawlQueue.Dequeue();
            var sourceManagedIndex = _result.ManagedIndexByAddress[address];
            var source = _result.ManagedObjects[sourceManagedIndex];
            var sourceTypeIndex = source.TypeIndex;

            foreach (var targetAddress in EnumerateOutgoingManagedReferences(address, sourceTypeIndex))
            {
                if (targetAddress == 0)
                    continue;
                if (TryEnsureManagedObject(targetAddress, $"reference from managed index {sourceManagedIndex}") is { } targetManagedIndex)
                    AddManagedEdge(sourceManagedIndex, targetManagedIndex, "managed_reference");
            }

            if (source.NativeObjectIndex >= 0)
            {
                AddManagedToNativeEdge(sourceManagedIndex, source.NativeObjectIndex, "managed_native_bridge");
                AddNativeToManagedEdge(source.NativeObjectIndex, sourceManagedIndex, "native_gc_handle_bridge");
            }
        }

        return _result;
    }

    /// <summary>Returns managed object index if the object was added or already present; null if type could not be resolved (object skipped).</summary>
    private int? TryEnsureManagedObject(ulong address, string reason)
    {
        if (_result.ManagedIndexByAddress.TryGetValue(address, out var existing))
            return existing;

        var parsed = ParseManagedObjectHeader(address, reason);
        if (parsed is null)
            return null;

        var managedIndex = _result.ManagedObjects.Count;
        _result.ManagedIndexByAddress[address] = managedIndex;
        _result.ManagedObjects.Add(new ManagedObjectRow
        {
            ManagedObjectIndex = managedIndex,
            Address = address,
            SizeBytes = parsed.Value.SizeBytes,
            TypeIndex = parsed.Value.TypeIndex,
            ManagedTypeName = _snapshot.ManagedTypeNames[parsed.Value.TypeIndex] ?? string.Empty,
            NativeObjectIndex = _managedAddressToNativeObjectIndex.TryGetValue(address, out var nativeObjectIndex) ? nativeObjectIndex : -1,
        });
        _crawlQueue.Enqueue(address);
        return managedIndex;
    }

    private ParsedManagedObject? ParseManagedObjectHeader(ulong address, string reason)
    {
        if (!TryReadPointer(address, out var ptrIdentity))
            return null;
        if (!TryResolveTypeIndex(ptrIdentity, reason, out var typeIndex))
            return null;
        var sizeBytes = ComputeObjectSizeBytes(address, typeIndex, reason);
        if (sizeBytes <= 0)
            return null;
        if (!TryGetReadableWindow(address, checked((ulong)sizeBytes), out _, out _))
            return null;
        return new ParsedManagedObject(typeIndex, sizeBytes);
    }

    private bool TryResolveTypeIndex(ulong ptrIdentity, string reason, out int typeIndex)
    {
        typeIndex = 0;
        if (_typeInfoToIndex.TryGetValue(ptrIdentity, out var direct))
        {
            typeIndex = direct;
            return true;
        }

        if (!TryReadPointer(ptrIdentity, out var typeInfoPtr))
            return false;

        if (_typeInfoToIndex.TryGetValue(typeInfoPtr, out var indirect))
        {
            typeIndex = indirect;
            return true;
        }
        return false;
    }

    private long ComputeObjectSizeBytes(ulong address, int typeIndex, string reason)
    {
        EnsureValidTypeIndex(typeIndex, reason);
        if (IsArrayType(typeIndex))
        {
            var length = ReadArrayLength(address, typeIndex, reason);
            var elementTypeIndex = _snapshot.ManagedTypeBaseOrElementTypeIndices[typeIndex];
            if (elementTypeIndex < 0)
                elementTypeIndex = typeIndex;
            EnsureValidTypeIndex(elementTypeIndex, reason);

            var elementSize = IsValueType(elementTypeIndex)
                ? _snapshot.ManagedTypeSizes[elementTypeIndex]
                : checked((int)_vm.PointerSize);
            if (elementSize < 0)
                throw new InvalidOperationException($"Negative array element size for type '{GetTypeName(elementTypeIndex)}'. reason={reason}");

            return checked((long)_vm.ArrayHeaderSize + checked((long)elementSize * length));
        }

        if (IsStringType(typeIndex))
        {
            var length = ReadInt32Strict(address + _vm.ObjectHeaderSize, $"string length for {reason}");
            if (length < 0)
                throw new InvalidOperationException($"Negative string length {length} at 0x{address:X16}. reason={reason}");
            return checked((long)_vm.ObjectHeaderSize + 4L + checked((long)length * 2L) + 2L);
        }

        var typeSize = _snapshot.ManagedTypeSizes[typeIndex];
        if (typeSize < 0)
            throw new InvalidOperationException($"Negative type size {typeSize} for '{GetTypeName(typeIndex)}'. reason={reason}");
        return IsValueType(typeIndex)
            ? checked(typeSize + (long)_vm.ObjectHeaderSize)
            : typeSize;
    }

    private long ReadArrayLength(ulong address, int arrayTypeIndex, string reason)
    {
        var bounds = ReadPointerStrict(address + _vm.ArrayBoundsOffsetInHeader, $"array bounds for {reason}");
        if (bounds == 0)
            return ReadInt32Strict(address + _vm.ArraySizeOffsetInHeader, $"array size for {reason}");

        var rank = (_snapshot.ManagedTypeFlags[arrayTypeIndex] & TypeFlagArrayRankMask) >> 16;
        if (rank <= 0)
            throw new InvalidOperationException($"Invalid array rank {rank} for '{GetTypeName(arrayTypeIndex)}'. reason={reason}");

        long length = 1;
        for (var i = 0; i < rank; i++)
        {
            var dimensionLength = ReadInt32Strict(bounds + (ulong)(i * 8), $"array rank[{i}] length for {reason}");
            if (dimensionLength < 0)
                throw new InvalidOperationException($"Negative array dimension length {dimensionLength} for '{GetTypeName(arrayTypeIndex)}'. reason={reason}");
            length = checked(length * dimensionLength);
        }

        return length;
    }

    private IEnumerable<ulong> EnumerateOutgoingManagedReferences(ulong objectAddress, int objectTypeIndex)
    {
        if (IsStringType(objectTypeIndex))
            yield break;

        if (IsArrayType(objectTypeIndex))
        {
            foreach (var reference in EnumerateArrayReferences(objectAddress, objectTypeIndex))
                yield return reference;
            yield break;
        }

        foreach (var reference in EnumerateReferenceTypeFieldReferences(objectAddress, objectTypeIndex))
            yield return reference;
    }

    private IEnumerable<ulong> EnumerateArrayReferences(ulong arrayAddress, int arrayTypeIndex)
    {
        var length = ReadArrayLength(arrayAddress, arrayTypeIndex, $"array refs for '{GetTypeName(arrayTypeIndex)}'");
        if (length == 0)
            yield break;

        var elementTypeIndex = _snapshot.ManagedTypeBaseOrElementTypeIndices[arrayTypeIndex];
        if (elementTypeIndex < 0)
            elementTypeIndex = arrayTypeIndex;
        EnsureValidTypeIndex(elementTypeIndex, $"array element of {GetTypeName(arrayTypeIndex)}");

        var arrayDataAddress = checked(arrayAddress + _vm.ArrayHeaderSize);
        if (IsValueType(elementTypeIndex))
        {
            var elementSize = _snapshot.ManagedTypeSizes[elementTypeIndex];
            if (elementSize < 0)
                throw new InvalidOperationException($"Negative value-type array element size for '{GetTypeName(elementTypeIndex)}'.");

            for (long i = 0; i < length; i++)
            {
                var elementAddress = checked(arrayDataAddress + checked((ulong)(i * elementSize)));
                foreach (var reference in EnumerateValueTypeReferences(elementAddress, elementTypeIndex, recursionDepth: 0))
                    yield return reference;
            }
        }
        else
        {
            for (long i = 0; i < length; i++)
            {
                var ptrAddress = checked(arrayDataAddress + checked((ulong)(i * (long)_vm.PointerSize)));
                var targetAddress = ReadPointerStrict(ptrAddress, $"array element pointer for '{GetTypeName(arrayTypeIndex)}'");
                if (targetAddress != 0)
                    yield return targetAddress;
            }
        }
    }

    private IEnumerable<ulong> EnumerateReferenceTypeFieldReferences(ulong objectAddress, int typeIndex)
    {
        var instanceFields = GetInstanceFieldIndices(typeIndex);
        for (var instanceFieldIdx = 0; instanceFieldIdx < instanceFields.Length; instanceFieldIdx++)
        {
            var fieldIndex = instanceFields[instanceFieldIdx];
            if ((uint)fieldIndex >= (uint)_snapshot.FieldTypeIndices.Length)
                throw new InvalidOperationException($"Field index out of range: {fieldIndex} for type '{GetTypeName(typeIndex)}'.");
            if (_snapshot.FieldIsStatic[fieldIndex] != 0)
                continue;

            var fieldOffset = _snapshot.FieldOffsets[fieldIndex];
            if (fieldOffset < 0)
                continue;

            var fieldTypeIndex = _snapshot.FieldTypeIndices[fieldIndex];
            EnsureValidTypeIndex(fieldTypeIndex, $"field '{_snapshot.FieldNames[fieldIndex]}' on '{GetTypeName(typeIndex)}'");

            var fieldAddress = checked(objectAddress + (ulong)fieldOffset);
            if (IsValueType(fieldTypeIndex))
            {
                foreach (var reference in EnumerateValueTypeReferences(fieldAddress, fieldTypeIndex, recursionDepth: 0))
                    yield return reference;
            }
            else
            {
                var targetAddress = ReadPointerStrict(fieldAddress, $"field '{_snapshot.FieldNames[fieldIndex]}' on '{GetTypeName(typeIndex)}'");
                if (targetAddress != 0)
                    yield return targetAddress;
            }
        }
    }

    private IEnumerable<ulong> EnumerateValueTypeReferences(ulong valueBaseAddress, int valueTypeIndex, int recursionDepth)
    {
        if (recursionDepth > 24)
            throw new InvalidOperationException($"Value-type recursion depth exceeded for '{GetTypeName(valueTypeIndex)}'.");

        var instanceFields = GetInstanceFieldIndices(valueTypeIndex);
        for (var instanceFieldIdx = 0; instanceFieldIdx < instanceFields.Length; instanceFieldIdx++)
        {
            var fieldIndex = instanceFields[instanceFieldIdx];
            if ((uint)fieldIndex >= (uint)_snapshot.FieldTypeIndices.Length)
                throw new InvalidOperationException($"Value-type field index out of range: {fieldIndex} for '{GetTypeName(valueTypeIndex)}'.");
            if (_snapshot.FieldIsStatic[fieldIndex] != 0)
                continue;

            var adjustedOffset = _snapshot.FieldOffsets[fieldIndex] - (int)_vm.ObjectHeaderSize;
            if (adjustedOffset < 0)
                continue;

            var fieldTypeIndex = _snapshot.FieldTypeIndices[fieldIndex];
            EnsureValidTypeIndex(fieldTypeIndex, $"value-type field '{_snapshot.FieldNames[fieldIndex]}' on '{GetTypeName(valueTypeIndex)}'");

            var fieldAddress = checked(valueBaseAddress + (ulong)adjustedOffset);
            if (IsValueType(fieldTypeIndex))
            {
                if (fieldTypeIndex == valueTypeIndex)
                    continue;
                foreach (var nested in EnumerateValueTypeReferences(fieldAddress, fieldTypeIndex, recursionDepth + 1))
                    yield return nested;
            }
            else
            {
                var targetAddress = ReadPointerStrict(fieldAddress, $"value-type field '{_snapshot.FieldNames[fieldIndex]}'");
                if (targetAddress != 0)
                    yield return targetAddress;
            }
        }
    }

    private int[] GetInstanceFieldIndices(int typeIndex)
    {
        EnsureValidTypeIndex(typeIndex, "enumerate fields");
        if (_instanceFieldIndexCache.TryGetValue(typeIndex, out var cached))
            return cached;

        var chain = new List<int>(8);
        var visited = new HashSet<int>();
        var current = typeIndex;
        while (current >= 0)
        {
            if (!visited.Add(current))
                throw new InvalidOperationException($"Cyclic managed base-type chain detected at type index {current}.");
            chain.Add(current);
            current = _snapshot.ManagedTypeBaseOrElementTypeIndices[current];
        }

        var fields = new List<int>(16);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var chainTypeIndex = chain[i];
            var fieldIndices = _snapshot.ManagedTypeFieldIndices[chainTypeIndex];
            for (var fieldIndex = 0; fieldIndex < fieldIndices.Length; fieldIndex++)
                fields.Add(fieldIndices[fieldIndex]);
        }

        cached = fields.ToArray();
        _instanceFieldIndexCache[typeIndex] = cached;
        return cached;
    }

    private void AddManagedEdge(long fromManagedIndex, long toManagedIndex, string type)
    {
        if (_edgeDedup.Add(new EdgeKey(fromManagedIndex, toManagedIndex, EdgeType.ManagedToManaged)))
        {
            _result.ManagedConnections.Add(new ConnectionRow
            {
                FromKind = "managed_object",
                FromIndex = fromManagedIndex,
                ToKind = "managed_object",
                ToIndex = toManagedIndex,
                ConnectionType = type,
            });
        }
    }

    private void AddManagedToNativeEdge(long fromManagedIndex, long toNativeIndex, string type)
    {
        if (_edgeDedup.Add(new EdgeKey(fromManagedIndex, toNativeIndex, EdgeType.ManagedToNative)))
        {
            _result.ManagedConnections.Add(new ConnectionRow
            {
                FromKind = "managed_object",
                FromIndex = fromManagedIndex,
                ToKind = "native_object",
                ToIndex = toNativeIndex,
                ConnectionType = type,
            });
        }
    }

    private void AddNativeToManagedEdge(long fromNativeIndex, long toManagedIndex, string type)
    {
        if (_edgeDedup.Add(new EdgeKey(fromNativeIndex, toManagedIndex, EdgeType.NativeToManaged)))
        {
            _result.ManagedConnections.Add(new ConnectionRow
            {
                FromKind = "native_object",
                FromIndex = fromNativeIndex,
                ToKind = "managed_object",
                ToIndex = toManagedIndex,
                ConnectionType = type,
            });
        }
    }

    private bool IsArrayType(int typeIndex) => (_snapshot.ManagedTypeFlags[typeIndex] & TypeFlagArray) != 0;

    private bool IsValueType(int typeIndex) => (_snapshot.ManagedTypeFlags[typeIndex] & TypeFlagValueType) != 0;

    private bool IsStringType(int typeIndex)
        => string.Equals(_snapshot.ManagedTypeNames[typeIndex], "System.String", StringComparison.Ordinal);

    private string GetTypeName(int typeIndex)
        => typeIndex >= 0 && typeIndex < _snapshot.ManagedTypeNames.Length
            ? _snapshot.ManagedTypeNames[typeIndex] ?? string.Empty
            : $"type#{typeIndex}";

    private void EnsureValidTypeIndex(int typeIndex, string reason)
    {
        if (typeIndex < 0 || typeIndex >= _snapshot.ManagedTypeNames.Length)
            throw new InvalidOperationException($"Invalid managed type index {typeIndex}. reason={reason}");
    }

    private ulong ReadPointerStrict(ulong address, string reason)
    {
        if (!TryReadPointer(address, out var value))
            throw new InvalidOperationException($"Unable to read pointer at 0x{address:X16}. reason={reason}");
        return value;
    }

    private int ReadInt32Strict(ulong address, string reason)
    {
        if (!TryReadInt32(address, out var value))
            throw new InvalidOperationException($"Unable to read int32 at 0x{address:X16}. reason={reason}");
        return value;
    }

    private void EnsureReadable(ulong address, long byteCount, string reason)
    {
        if (byteCount < 0)
            throw new InvalidOperationException($"Negative readability check size {byteCount}. reason={reason}");
        if (!TryGetReadableWindow(address, checked((ulong)byteCount), out _, out _))
            throw new InvalidOperationException($"Managed heap read out of range at 0x{address:X16} len={byteCount}. reason={reason}");
    }

    private bool TryReadPointer(ulong address, out ulong value)
    {
        value = 0;
        if (!TryGetReadableWindow(address, _vm.PointerSize, out var section, out var offset))
            return false;

        if (_vm.PointerSize == 8)
        {
            value = BinaryPrimitives.ReadUInt64LittleEndian(section.Bytes.AsSpan(offset, 8));
            return true;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(section.Bytes.AsSpan(offset, 4));
        return true;
    }

    private bool TryReadInt32(ulong address, out int value)
    {
        value = 0;
        if (!TryGetReadableWindow(address, 4, out var section, out var offset))
            return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(section.Bytes.AsSpan(offset, 4));
        return true;
    }

    private bool TryGetReadableWindow(ulong address, ulong byteCount, out ManagedHeapSection section, out int offsetInSection)
    {
        section = default;
        offsetInSection = 0;
        if (_sections.Count == 0)
            return false;

        var sectionIndex = FindSectionIndex(address);
        if (sectionIndex < 0)
            return false;

        var candidate = _sections[sectionIndex];
        var localOffset = checked((long)(address - candidate.StartAddress));
        if (localOffset < 0 || localOffset > candidate.Bytes.Length)
            return false;

        if (byteCount > 0 && checked((ulong)localOffset + byteCount) > (ulong)candidate.Bytes.Length)
            return false;

        section = candidate;
        offsetInSection = (int)localOffset;
        return true;
    }

    private int FindSectionIndex(ulong address)
    {
        var lo = 0;
        var hi = _sections.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var start = _sections[mid].StartAddress;
            if (start <= address)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0)
            return -1;

        var section = _sections[found];
        return address < section.EndAddressExclusive ? found : -1;
    }

    private static Dictionary<ulong, int> BuildTypeInfoIndex(DecodedSnapshot snapshot)
    {
        var map = new Dictionary<ulong, int>(snapshot.ManagedTypeInfoAddresses.Length);
        for (var i = 0; i < snapshot.ManagedTypeInfoAddresses.Length; i++)
        {
            var typeInfoAddress = snapshot.ManagedTypeInfoAddresses[i];
            if (typeInfoAddress == 0)
                continue;
            map.TryAdd(typeInfoAddress, i);
        }
        return map;
    }

    private static Dictionary<ulong, long> BuildManagedAddressToNativeMap(DecodedSnapshot snapshot)
    {
        var gcHandleToNativeObject = new Dictionary<int, int>(snapshot.NativeObjectGcHandleIndices.Length);
        for (var nativeIndex = 0; nativeIndex < snapshot.NativeObjectGcHandleIndices.Length; nativeIndex++)
        {
            var gcHandleIndex = snapshot.NativeObjectGcHandleIndices[nativeIndex];
            if (gcHandleIndex >= 0)
                gcHandleToNativeObject.TryAdd(gcHandleIndex, nativeIndex);
        }

        var map = new Dictionary<ulong, long>(gcHandleToNativeObject.Count);
        foreach (var (gcHandleIndex, nativeObjectIndex) in gcHandleToNativeObject)
        {
            if (gcHandleIndex < 0 || gcHandleIndex >= snapshot.GcHandleTargets.Length)
                continue;
            var address = snapshot.GcHandleTargets[gcHandleIndex];
            if (address != 0)
                map[address] = nativeObjectIndex;
        }

        return map;
    }

    private static List<ManagedHeapSection> BuildManagedHeapSections(DecodedSnapshot snapshot)
    {
        var sections = new List<ManagedHeapSection>(snapshot.ManagedHeapSectionStartAddresses.Length);
        for (var i = 0; i < snapshot.ManagedHeapSectionStartAddresses.Length; i++)
        {
            sections.Add(new ManagedHeapSection(snapshot.ManagedHeapSectionStartAddresses[i], snapshot.ManagedHeapSectionBytes[i]));
        }

        sections.Sort((a, b) => a.StartAddress.CompareTo(b.StartAddress));
        return sections;
    }

    private readonly record struct ParsedManagedObject(int TypeIndex, long SizeBytes);

    private readonly record struct ManagedHeapSection(ulong StartAddress, byte[] Bytes)
    {
        public ulong EndAddressExclusive => StartAddress + (ulong)Bytes.Length;
    }

    private enum EdgeType : byte
    {
        ManagedToManaged = 0,
        ManagedToNative = 1,
        NativeToManaged = 2,
    }

    private readonly record struct EdgeKey(long FromIndex, long ToIndex, EdgeType Type);
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.BcacheFs;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public class BcacheFsBtreeReaderTests {
  [Test, Category("Btree")]
  public void CoreReader_ReadsWriterTreesThroughNativeRootPointers() {
    var writer = new BcacheFsWriter();
    writer.AddFile("alpha.bin", [1, 2, 3, 4, 5]);
    writer.AddFile("beta.bin", [6, 7, 8]);

    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var inodes = core.ReadTree(BcacheFsBtreeId.Inodes);
    var dirents = core.ReadTree(BcacheFsBtreeId.Dirents);
    var extents = core.ReadTree(BcacheFsBtreeId.Extents);

    Assert.Multiple(() => {
      Assert.That(core.Recoverable, Is.True, string.Join(Environment.NewLine, core.Diagnostics));
      Assert.That(inodes.Complete, Is.True, string.Join(Environment.NewLine, inodes.Diagnostics));
      Assert.That(dirents.Complete, Is.True, string.Join(Environment.NewLine, dirents.Diagnostics));
      Assert.That(extents.Complete, Is.True, string.Join(Environment.NewLine, extents.Diagnostics));
      Assert.That(inodes.MaterializedLeafSlots, Has.Count.EqualTo(3)); // root + two files
      Assert.That(dirents.MaterializedLeafSlots, Has.Count.EqualTo(2));
      Assert.That(extents.MaterializedLeafSlots, Has.Count.EqualTo(2));
      Assert.That(inodes.Nodes.All(n => n.BtreeId == (byte)BcacheFsBtreeId.Inodes), Is.True);
      Assert.That(inodes.Nodes.All(n => n.Sets.All(s => s.Keys.Count > 0)), Is.True);
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_RecursesThroughMultiNodeTrees() {
    var writer = new BcacheFsWriter();
    const int files = 1200;
    for (var i = 0; i < files; ++i)
      writer.AddFile($"f{i:D4}", []);

    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var inodes = core.ReadTree(BcacheFsBtreeId.Inodes);

    Assert.Multiple(() => {
      Assert.That(inodes.Complete, Is.True, string.Join(Environment.NewLine, inodes.Diagnostics));
      Assert.That(inodes.Nodes.Count, Is.GreaterThan(1));
      Assert.That(inodes.Nodes.Max(n => n.Level), Is.EqualTo(1));
      Assert.That(inodes.Nodes.Count(n => n.Level == 0), Is.GreaterThan(1));
      Assert.That(inodes.MaterializedLeafSlots, Has.Count.EqualTo(files + 1));
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_TraversesMaximumFourLevelTree() {
    var writer = new BcacheFsWriter();
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var originalRoot = core.Root(BcacheFsBtreeId.Inodes)!;
    var firstSector = image.Length / SectorSize / 2;
    firstSector -= firstSector % BucketSectors;

    Key? childPointer = null;
    for (var level = 0; level < BcacheFsOnDiskCatalog.MaxBtreeDepth; ++level) {
      var node = new BcacheFsNodeBuilder {
        BtreeId = (int)BcacheFsBtreeId.Inodes,
        Seq = checked((ulong)(0x5000 + level)),
        SuperblockMagic = core.Superblock.FilesystemMagic,
        Level = level,
        MinKey = Bpos.Min,
        MaxKey = Bpos.Max,
      };
      if (childPointer is { } child)
        node.Add(child);

      var sector = firstSector + level * BucketSectors;
      childPointer = WriteSyntheticNode(image, node, sector);
    }

    var rootKey = DecodeCurrent(childPointer!.Value);
    SetRoot(core, BcacheFsBtreeId.Inodes, originalRoot with {
      Level = BcacheFsOnDiskCatalog.MaxBtreeDepth - 1,
      Key = rootKey,
    });

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    var lookup = core.Lookup(BcacheFsBtreeId.Inodes, new Bpos(123, 456, 789));
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
      Assert.That(tree.Nodes, Has.Count.EqualTo(BcacheFsOnDiskCatalog.MaxBtreeDepth));
      Assert.That(tree.Nodes.Select(n => (int)n.Level), Is.EqualTo(new[] { 3, 2, 1, 0 }));
      Assert.That(tree.MaterializedLeafSlots, Is.Empty);
      Assert.That(lookup.Complete, Is.True, string.Join(Environment.NewLine, lookup.Diagnostics));
      Assert.That(lookup.Key, Is.Null);
    });
  }

  [Test, Category("Btree")]
  public void CoreEngine_LookupAndRangeIterationUseRecoveredTreeView() {
    var writer = new BcacheFsWriter();
    const int files = 1200;
    for (var i = 0; i < files; ++i)
      writer.AddFile($"q{i:D4}", []);

    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
    Assert.That(tree.Nodes.Count(n => n.Level == 0), Is.GreaterThan(1));

    var expected = tree.MaterializedLeafSlots;
    var target = expected[expected.Count / 2];
    var lookup = core.Lookup(BcacheFsBtreeId.Inodes, target.Position);

    var startIndex = expected.Count / 3;
    var endIndex = startIndex + expected.Count / 4;
    var range = core.ReadRange(
      BcacheFsBtreeId.Inodes,
      expected[startIndex].Position,
      expected[endIndex].Position);

    Assert.Multiple(() => {
      Assert.That(lookup.Complete, Is.True, string.Join(Environment.NewLine, lookup.Diagnostics));
      Assert.That(lookup.Key, Is.Not.Null);
      Assert.That(lookup.Key!.Position, Is.EqualTo(target.Position));
      Assert.That(lookup.Key.EncodedBytes, Is.EqualTo(target.EncodedBytes));
      Assert.That(range.Complete, Is.True, string.Join(Environment.NewLine, range.Diagnostics));
      Assert.That(range.Keys.Select(k => k.Position),
        Is.EqualTo(expected.Skip(startIndex).Take(endIndex - startIndex).Select(k => k.Position)));
      Assert.That(range.Keys.Zip(range.Keys.Skip(1), (a, b) => Compare(a.Position, b.Position) < 0).All(x => x), Is.True);
    });
  }

  [Test, Category("Btree")]
  public void CoreEngine_AppliesRecoveryJournalKeyOverlay() {
    var writer = new BcacheFsWriter();
    writer.AddFile("journal-shadow.bin", []);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var baseline = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.That(baseline.Complete, Is.True, string.Join(Environment.NewLine, baseline.Diagnostics));
    var target = baseline.MaterializedLeafSlots[^1];
    var deleted = Reencode(target with {
      RawType = (byte)BcacheFsKeyType.Deleted,
      Size = 0,
      Value = [],
    });

    var updates = core.Overlay.KeyUpdates as List<BcacheFsJournalKeyUpdate>
      ?? throw new InvalidOperationException("Journal overlay key list is not mutable in this test fixture.");
    updates.Add(new BcacheFsJournalKeyUpdate(
      Sequence: (core.Checkpoint?.JournalSequence ?? 0) + 1,
      JournalOrder: 0,
      BtreeId: (byte)BcacheFsBtreeId.Inodes,
      Level: 0,
      Key: deleted));

    var lookup = core.Lookup(BcacheFsBtreeId.Inodes, target.Position);
    var range = core.ReadRange(BcacheFsBtreeId.Inodes, target.Position, Successor(target.Position));
    Assert.Multiple(() => {
      Assert.That(lookup.Complete, Is.True, string.Join(Environment.NewLine, lookup.Diagnostics));
      Assert.That(lookup.Key, Is.Null);
      Assert.That(range.Complete, Is.True, string.Join(Environment.NewLine, range.Diagnostics));
      Assert.That(range.Keys, Is.Empty);
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_FallsBackToValidReplicatedMetadataPointer() {
    var writer = new BcacheFsWriter();
    writer.AddFile("replica.bin", [1, 2, 3]);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    using var mirror = new MemoryStream(image.ToArray(), writable: true);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var root = core.Root(BcacheFsBtreeId.Inodes)!;
    Assert.That(BcacheFsExtentCodec.TryReadBtreePointer(
      root.Key, core.Superblock, out var pointer, out var pointerError), Is.True, pointerError);
    var firstReplica = pointer!.Replicas.Single();

    var value = new byte[root.Key.Value.Length + sizeof(ulong)];
    root.Key.Value.CopyTo(value, 0);
    var firstWord = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(40));
    var mirrorWord = (firstWord & ~(0xFFUL << 48)) | (1UL << 48);
    BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(root.Key.Value.Length), mirrorWord);
    SetRoot(core, BcacheFsBtreeId.Inodes, root with { Key = Reencode(root.Key with { Value = value }) });

    var devices = core.Devices as Dictionary<byte, Stream>
      ?? throw new InvalidOperationException("Core device map is not mutable in this test fixture.");
    devices[1] = mirror;

    var corruptAt = firstReplica.Sector * SectorSize + 200;
    image.Position = corruptAt;
    var original = image.ReadByte();
    Assert.That(original, Is.GreaterThanOrEqualTo(0));
    image.Position = corruptAt;
    image.WriteByte((byte)(original ^ 0x5A));

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
      Assert.That(tree.MaterializedLeafSlots, Has.Count.EqualTo(2));
      Assert.That(tree.Nodes, Has.Count.EqualTo(1));
      Assert.That(tree.Nodes[0].PhysicalPointer.Device, Is.EqualTo(1));
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_ZeroSectorsWrittenScansNodeByBsetSequence() {
    var writer = new BcacheFsWriter();
    writer.AddFile("zero-written.bin", []);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var root = core.Root(BcacheFsBtreeId.Inodes)!;
    var value = root.Key.Value.ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(16), 0);
    var modified = Reencode(root.Key with { Value = value });
    SetRoot(core, BcacheFsBtreeId.Inodes, root with { Key = modified });

    Assert.That(BcacheFsExtentCodec.TryReadBtreePointer(
      modified, core.Superblock, out var pointer, out var pointerError), Is.True, pointerError);
    Assert.That(pointer!.SectorsWritten, Is.Zero);

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
      Assert.That(tree.MaterializedLeafSlots, Has.Count.EqualTo(2));
      Assert.That(tree.Nodes.Single().RawBytes.Length,
        Is.EqualTo(core.Superblock.BtreeNodeSectors * SectorSize));
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_RangeUpdatedUsesPointerBoundsAndDropsOldOutsideKeys() {
    var writer = new BcacheFsWriter();
    writer.AddFile("range-a.bin", []);
    writer.AddFile("range-b.bin", []);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var baseline = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.That(baseline.Complete, Is.True, string.Join(Environment.NewLine, baseline.Diagnostics));
    Assert.That(baseline.Nodes, Has.Count.EqualTo(1));
    var target = baseline.MaterializedLeafSlots[1];
    var root = core.Root(BcacheFsBtreeId.Inodes)!;

    var value = root.Key.Value.ToArray();
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(18));
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(18), (ushort)(flags | 1));
    WriteBpos(value.AsSpan(20), target.Position);
    var modified = Reencode(root.Key with { Position = target.Position, Value = value });
    SetRoot(core, BcacheFsBtreeId.Inodes, root with { Key = modified });

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    var lookup = core.Lookup(BcacheFsBtreeId.Inodes, target.Position);
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
      Assert.That(tree.Nodes.Single().MinKey, Is.EqualTo(target.Position));
      Assert.That(tree.Nodes.Single().MaxKey, Is.EqualTo(target.Position));
      Assert.That(tree.MaterializedLeafSlots.Select(k => k.Position), Is.EqualTo(new[] { target.Position }));
      Assert.That(lookup.Complete, Is.True, string.Join(Environment.NewLine, lookup.Diagnostics));
      Assert.That(lookup.Key?.Position, Is.EqualTo(target.Position));
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_LegacyPointerStillValidatesNodeMaximum() {
    var writer = new BcacheFsWriter();
    writer.AddFile("legacy.bin", []);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var baseline = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.That(baseline.Complete, Is.True, string.Join(Environment.NewLine, baseline.Diagnostics));
    var root = core.Root(BcacheFsBtreeId.Inodes)!;
    var wrongMaximum = baseline.MaterializedLeafSlots[^1].Position;
    Assert.That(wrongMaximum, Is.Not.EqualTo(baseline.Nodes.Single().MaxKey));

    var legacy = Reencode(root.Key with {
      RawType = (byte)BcacheFsKeyType.BtreePtr,
      Position = wrongMaximum,
      Value = root.Key.Value.AsSpan(40).ToArray(),
    });
    SetRoot(core, BcacheFsBtreeId.Inodes, root with { Key = legacy });

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.False);
      Assert.That(tree.MaterializedLeafSlots, Is.Empty);
      Assert.That(tree.Diagnostics.Any(d => d.Contains("pointer max", StringComparison.OrdinalIgnoreCase)), Is.True,
        string.Join(Environment.NewLine, tree.Diagnostics));
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_AppendedBsetParticipatesInLastWriterWinsView() {
    var writer = new BcacheFsWriter();
    writer.AddFile("append-delete.bin", []);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var baseline = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.That(baseline.Complete, Is.True, string.Join(Environment.NewLine, baseline.Diagnostics));
    var target = baseline.MaterializedLeafSlots[^1];
    var deleted = Reencode(target with {
      RawType = (byte)BcacheFsKeyType.Deleted,
      Size = 0,
      Value = [],
    });

    AppendBset(image, core, BcacheFsBtreeId.Inodes, deleted, journalSequence: 0, zeroSectorsWritten: false);

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
      Assert.That(tree.RawLeafRuns, Has.Count.EqualTo(2));
      Assert.That(tree.RawLeafRuns.All(s => s.Visible), Is.True);
      Assert.That(tree.MaterializedLeafSlots.Any(k => k.Position == target.Position), Is.False);
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_BlacklistedAppendedBsetIsInvisibleWhenScanningZeroWrittenNode() {
    var writer = new BcacheFsWriter();
    writer.AddFile("append-blacklist.bin", []);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var baseline = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.That(baseline.Complete, Is.True, string.Join(Environment.NewLine, baseline.Diagnostics));
    var target = baseline.MaterializedLeafSlots[^1];
    var deleted = Reencode(target with {
      RawType = (byte)BcacheFsKeyType.Deleted,
      Size = 0,
      Value = [],
    });

    var blacklists = core.Overlay.BlacklistedSequences as List<BcacheFsJournalSequenceRange>
      ?? throw new InvalidOperationException("Journal blacklist list is not mutable in this test fixture.");
    blacklists.Add(new BcacheFsJournalSequenceRange(123, 124));
    AppendBset(image, core, BcacheFsBtreeId.Inodes, deleted, journalSequence: 123, zeroSectorsWritten: true);

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
      Assert.That(tree.RawLeafRuns, Has.Count.EqualTo(2));
      Assert.That(tree.RawLeafRuns[^1].Visible, Is.False);
      Assert.That(tree.MaterializedLeafSlots.Any(k => k.Position == target.Position), Is.True);
    });
  }

  [Test, Category("Btree")]
  public void CoreReader_RejectsCorruptNodeChecksumInsteadOfReturningKeys() {
    var writer = new BcacheFsWriter();
    writer.AddFile("payload.bin", Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());

    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var root = core.Root(BcacheFsBtreeId.Inodes)!;
    Assert.That(BcacheFsExtentCodec.TryReadBtreePointer(
      root.Key, core.Superblock, out var pointer, out var pointerError), Is.True, pointerError);
    var replica = pointer!.Replicas.Single();

    var corruptAt = replica.Sector * SectorSize + 200;
    image.Position = corruptAt;
    var original = image.ReadByte();
    Assert.That(original, Is.GreaterThanOrEqualTo(0));
    image.Position = corruptAt;
    image.WriteByte((byte)(original ^ 0x5A));

    var tree = core.ReadTree(BcacheFsBtreeId.Inodes);
    Assert.Multiple(() => {
      Assert.That(tree.Complete, Is.False);
      Assert.That(tree.MaterializedLeafSlots, Is.Empty);
      Assert.That(tree.Diagnostics.Any(d => d.Contains("checksum", StringComparison.OrdinalIgnoreCase)), Is.True,
        string.Join(Environment.NewLine, tree.Diagnostics));
    });
  }

  [Test, Category("Extent")]
  public void ExtentCodec_DecodesCrcAndPhysicalPointerChain() {
    var writer = new BcacheFsWriter();
    writer.AddFile("one.bin", [10, 20, 30]);

    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;

    var core = BcacheFsCoreVolume.Open(image);
    var tree = core.ReadTree(BcacheFsBtreeId.Extents);
    Assert.That(tree.Complete, Is.True, string.Join(Environment.NewLine, tree.Diagnostics));
    var extent = tree.MaterializedLeafSlots.Single();

    Assert.That(BcacheFsExtentCodec.TryParseEntries(
      extent.Value, core.Superblock, out var entries, out var error), Is.True, error);
    Assert.That(entries.Select(e => e.KnownType), Is.EqualTo(new[] {
      BcacheFsExtentEntryType.Crc32,
      BcacheFsExtentEntryType.Pointer,
    }));

    Assert.That(BcacheFsExtentCodec.TryReadExtentCrc(entries[0], out var crc, out error), Is.True, error);
    var pointerWord = BinaryPrimitives.ReadUInt64LittleEndian(entries[1].RawBytes);
    Assert.Multiple(() => {
      Assert.That(crc!.CompressedSize, Is.EqualTo(1));
      Assert.That(crc.UncompressedSize, Is.EqualTo(1));
      Assert.That(crc.Offset, Is.Zero);
      Assert.That(crc.ChecksumType, Is.EqualTo(BcacheFsChecksumType.Crc32C));
      Assert.That(crc.CompressionType, Is.EqualTo(BcacheFsCompressionType.None));
      Assert.That(IsPointer(pointerWord), Is.True);
      Assert.That(PointerSector(pointerWord), Is.GreaterThan(0));
    });
  }

  [Test, Category("Extent")]
  public void ExtentTypeTable_PreservesFutureEntryLengthsFromSuperblockField() {
    var writer = new BcacheFsWriter();
    using var image = new MemoryStream();
    writer.WriteTo(image);
    image.Position = 0;
    var core = BcacheFsCoreVolume.Open(image);

    var sizes = BcacheFsExtentCodec.EntryU64s(core.Superblock);
    Assert.That(sizes.Take(9), Is.EqualTo(new byte[] { 1, 1, 2, 3, 1, 1, 1, 1, 1 }));
  }

  private static BcacheFsRawKey Reencode(BcacheFsRawKey key) {
    var current = key with { Format = KeyFormatCurrent };
    return current with { EncodedBytes = current.EncodeCurrent() };
  }

  private static BcacheFsRawKey DecodeCurrent(Key key) {
    var bytes = new byte[key.Bytes];
    WriteKey(bytes, key);
    if (!BcacheFsRawKeyCodec.TryDecode(bytes, null, out var raw, out var error))
      throw new InvalidDataException(error);
    return raw!;
  }

  private static void SetRoot(
      BcacheFsCoreVolume core,
      BcacheFsBtreeId id,
      BcacheFsTreeRoot root) {
    var roots = core.EffectiveRoots as Dictionary<byte, BcacheFsTreeRoot>
      ?? throw new InvalidOperationException("Effective root map is not mutable in this test fixture.");
    roots[(byte)id] = root;
  }

  private static Key WriteSyntheticNode(
      MemoryStream image,
      BcacheFsNodeBuilder node,
      long sector) {
    var buffer = new byte[BucketBytes];
    var sectors = node.Write(buffer);
    image.Position = checked(sector * SectorSize);
    image.Write(buffer);
    return node.Pointer(sector, sectors);
  }

  private static void AppendBset(
      MemoryStream image,
      BcacheFsCoreVolume core,
      BcacheFsBtreeId id,
      BcacheFsRawKey update,
      ulong journalSequence,
      bool zeroSectorsWritten) {
    var root = core.Root(id) ?? throw new InvalidDataException($"btree {id} has no root.");
    if (!BcacheFsExtentCodec.TryReadBtreePointer(
        root.Key, core.Superblock, out var pointer, out var pointerError))
      throw new InvalidDataException(pointerError);
    var replica = pointer!.Replicas.Single();

    var baseline = core.ReadTree(id);
    if (!baseline.Complete)
      throw new InvalidDataException(string.Join(Environment.NewLine, baseline.Diagnostics));
    var node = baseline.Nodes.Single();
    var blockBytes = checked(core.Superblock.BlockSizeSectors * SectorSize);
    var entryOffset = RoundUp(node.Sets[^1].EndByte, blockBytes);
    var keyBytes = update.EncodeCurrent();
    var entry = new byte[16 + 24 + keyBytes.Length];

    BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(16), node.FirstSetSequence);
    BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(24), journalSequence);
    var flags = (uint)BcacheFsChecksumType.Crc32CNonzero
      | checked((uint)(entryOffset / SectorSize) << 16);
    BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(32), flags);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(36), BcacheFsFormat.Version);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(38), checked((ushort)(keyBytes.Length / sizeof(ulong))));
    keyBytes.CopyTo(entry.AsSpan(40));
    BinaryPrimitives.WriteUInt64LittleEndian(entry, MetadataChecksum(entry.AsSpan(16)));

    var newEnd = RoundUp(entryOffset + entry.Length, blockBytes);
    var block = new byte[newEnd - entryOffset];
    entry.CopyTo(block, 0);
    image.Position = checked(replica.Sector * SectorSize + entryOffset);
    image.Write(block);

    var value = root.Key.Value.ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(
      value.AsSpan(16),
      zeroSectorsWritten ? (ushort)0 : checked((ushort)(newEnd / SectorSize)));
    SetRoot(core, id, root with { Key = Reencode(root.Key with { Value = value }) });
  }

  private static int RoundUp(int value, int alignment)
    => checked((value + alignment - 1) / alignment * alignment);
}
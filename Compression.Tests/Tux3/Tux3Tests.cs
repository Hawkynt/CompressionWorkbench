using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

[TestFixture]
public class Tux3Tests {
  private const int NativeBlockSize = 4096;

  private static byte[] BuildNativeImage(
    bool legacy2012 = false,
    ulong volBlocks = 0x1234,
    ushort blockBits = 12,
    int imageLength = 16 * 1024) {
    var image = new byte[imageLength];
    var super = image.AsSpan(Tux3Reader.SuperblockOffset, Tux3Reader.DiskSuperSize);
    (legacy2012 ? Tux3Reader.Legacy2012Magic : Tux3Reader.Magic).CopyTo(super);

    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x08, 8), 0x0123_4567_89AB_CDEFUL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x10, 8), 0x1020_3040_5060_7080UL);
    BinaryPrimitives.WriteUInt16BigEndian(super.Slice(0x18, 2), blockBits);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x20, 8), volBlocks);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x28, 8), 0x0001_0000_0000_0042UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x30, 8), 0x8002_0000_0000_0043UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x38, 8), 0x0000_0000_0000_0040UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x40, 8), 0x0000_0000_0000_0080UL);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x48, 8), 0x0000_0000_0000_0100UL);
    BinaryPrimitives.WriteUInt32BigEndian(super.Slice(0x50, 4), 0x1020_3040U);
    BinaryPrimitives.WriteUInt32BigEndian(super.Slice(0x54, 4), 0x5060_7080U);
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x58, 8), 0x0000_0000_0000_2222UL);
    BinaryPrimitives.WriteUInt32BigEndian(super.Slice(0x60, 4), 0x0000_0003U);
    return image;
  }

  /// <summary>
  /// Builds a small but structurally native metadata graph:
  /// superblock -> two-level inode btree -> bitmap inode -> one-level data dleaf -> bitmap data.
  /// Optional log payloads are written oldest-to-newest to consecutive physical blocks starting at 6.
  /// </summary>
  private static byte[] BuildAllocationMappedImage(
    IReadOnlyList<byte[]>? logPayloads = null,
    params int[] additionallyAllocated) {
    const int volumeBlocks = 16;
    logPayloads ??= [];
    var image = BuildNativeImage(volBlocks: volumeBlocks, imageLength: volumeBlocks * NativeBlockSize);
    var super = image.AsSpan(Tux3Reader.SuperblockOffset, Tux3Reader.DiskSuperSize);

    // inode-tree root: depth 2, root bnode at physical block 2.
    BinaryPrimitives.WriteUInt64BigEndian(super.Slice(0x28, 8), PackRoot(direct: false, countOrDepth: 2, block: 2));

    // Root bnode with one leftmost entry leading to the inode leaf.
    var bnode = GetBlock(image, 2);
    BinaryPrimitives.WriteUInt16BigEndian(bnode.Slice(0, 2), 0xB4DE);
    BinaryPrimitives.WriteUInt32BigEndian(bnode.Slice(4, 4), 1);
    BinaryPrimitives.WriteUInt64BigEndian(bnode.Slice(8, 8), 0);
    BinaryPrimitives.WriteUInt64BigEndian(bnode.Slice(16, 8), 3);

    // ileaf covers inode 0 and bitmap inode 1. Inode 0 is empty; inode 1 has only DATA_BTREE.
    var ileaf = GetBlock(image, 3);
    BinaryPrimitives.WriteUInt16BigEndian(ileaf.Slice(0, 2), 0x90DE);
    BinaryPrimitives.WriteUInt16BigEndian(ileaf.Slice(2, 2), 2);
    BinaryPrimitives.WriteUInt64BigEndian(ileaf.Slice(8, 8), 0);
    BinaryPrimitives.WriteUInt16BigEndian(ileaf.Slice(16, 2), 0x2000); // DATA_BTREE, version 0
    BinaryPrimitives.WriteUInt64BigEndian(ileaf.Slice(18, 8), PackRoot(direct: false, countOrDepth: 1, block: 4));
    BinaryPrimitives.WriteUInt16BigEndian(ileaf.Slice(NativeBlockSize - 2, 2), 0);
    BinaryPrimitives.WriteUInt16BigEndian(ileaf.Slice(NativeBlockSize - 4, 2), 10);

    // Bitmap data tree leaf: logical bitmap block 0 is physical block 5, followed by a hole sentinel.
    var dleaf = GetBlock(image, 4);
    BinaryPrimitives.WriteUInt16BigEndian(dleaf.Slice(0, 2), 0xBEAF);
    BinaryPrimitives.WriteUInt16BigEndian(dleaf.Slice(2, 2), 2);
    BinaryPrimitives.WriteUInt64BigEndian(dleaf.Slice(8, 8), 0);
    BinaryPrimitives.WriteUInt64BigEndian(dleaf.Slice(16, 8), 5);
    BinaryPrimitives.WriteUInt64BigEndian(dleaf.Slice(24, 8), 1);
    BinaryPrimitives.WriteUInt64BigEndian(dleaf.Slice(32, 8), 0);

    var allocated = new HashSet<int> { 0, 1, 2, 3, 4, 5 };
    foreach (var block in additionallyAllocated)
      allocated.Add(block);

    for (var i = 0; i < logPayloads.Count; ++i) {
      var physical = 6 + i;
      allocated.Add(physical);
      var previous = i == 0 ? 0UL : (ulong)(physical - 1);
      WriteLogBlock(GetBlock(image, physical), previous, logPayloads[i]);
    }

    BinaryPrimitives.WriteUInt64BigEndian(
      super.Slice(0x58, 8),
      logPayloads.Count == 0 ? 0UL : (ulong)(5 + logPayloads.Count));
    BinaryPrimitives.WriteUInt32BigEndian(super.Slice(0x60, 4), (uint)logPayloads.Count);

    var bitmap = GetBlock(image, 5);
    foreach (var block in allocated)
      bitmap[block >> 3] |= (byte)(1 << (block & 7));

    return image;
  }

  private static Span<byte> GetBlock(byte[] image, int block)
    => image.AsSpan(block * NativeBlockSize, NativeBlockSize);

  private static ulong PackRoot(bool direct, ushort countOrDepth, ulong block)
    => (direct ? 1UL << 63 : 0) | ((ulong)countOrDepth << 48) | block;

  private static void WriteLogBlock(Span<byte> block, ulong previous, ReadOnlySpan<byte> payload) {
    BinaryPrimitives.WriteUInt16BigEndian(block.Slice(0, 2), 0x10AD);
    BinaryPrimitives.WriteUInt16BigEndian(block.Slice(2, 2), checked((ushort)payload.Length));
    BinaryPrimitives.WriteUInt64BigEndian(block.Slice(8, 8), previous);
    payload.CopyTo(block[16..]);
  }

  private static byte[] AllocationLog(Tux3JournalRecordType type, uint count, ulong block) {
    var result = new byte[11];
    result[0] = (byte)type;
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(1, 4), count);
    WriteUInt48BigEndian(result.AsSpan(5, 6), block);
    return result;
  }

  private static byte[] BNodeAddLog(ulong parent, ulong child, ulong key) {
    var result = new byte[19];
    result[0] = (byte)Tux3JournalRecordType.BNodeAdd;
    WriteUInt48BigEndian(result.AsSpan(1, 6), parent);
    WriteUInt48BigEndian(result.AsSpan(7, 6), child);
    WriteUInt48BigEndian(result.AsSpan(13, 6), key);
    return result;
  }

  private static byte[] Concat(params byte[][] parts) {
    var result = new byte[parts.Sum(static part => part.Length)];
    var offset = 0;
    foreach (var part in parts) {
      part.CopyTo(result, offset);
      offset += part.Length;
    }
    return result;
  }

  private static void WriteUInt48BigEndian(Span<byte> destination, ulong value) {
    Assert.That(value, Is.LessThan(1UL << 48));
    BinaryPrimitives.WriteUInt16BigEndian(destination[..2], (ushort)(value >> 32));
    BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(2, 4), (uint)value);
  }

  [Test, Category("Spec")]
  public void Reader_ParsesCanonicalPackedBigEndianDiskSuper() {
    var image = BuildNativeImage();
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux3Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.ValidSuperblock, Is.True);
      Assert.That(reader.Revision, Is.EqualTo("2014-05-06"));
      Assert.That(reader.Birthday, Is.EqualTo(0x0123_4567_89AB_CDEFUL));
      Assert.That(reader.Flags, Is.EqualTo(0x1020_3040_5060_7080UL));
      Assert.That(reader.BlockBits, Is.EqualTo(12));
      Assert.That(reader.VolBlocks, Is.EqualTo(0x1234UL));
      Assert.That(reader.IRoot, Is.EqualTo(0x0001_0000_0000_0042UL));
      Assert.That(reader.ORoot, Is.EqualTo(0x8002_0000_0000_0043UL));
      Assert.That(reader.UsedInodes, Is.EqualTo(0x40UL));
      Assert.That(reader.NextBlock, Is.EqualTo(0x80UL));
      Assert.That(reader.AtomDictionarySize, Is.EqualTo(0x100UL));
      Assert.That(reader.FreeAtom, Is.EqualTo(0x1020_3040U));
      Assert.That(reader.AtomGeneration, Is.EqualTo(0x5060_7080U));
      Assert.That(reader.LogChain, Is.EqualTo(0x2222UL));
      Assert.That(reader.LogCount, Is.EqualTo(3U));
      Assert.That(reader.AllocationMapValid, Is.False);
    });

    Assert.That(reader.Entries.Select(entry => entry.Name),
      Is.EquivalentTo(new[] { "FULL.tux3", "metadata.ini", "superblock.bin" }));
    var superblock = reader.Extract(reader.Entries.Single(entry => entry.Name == "superblock.bin"));
    Assert.That(superblock, Is.EqualTo(image.AsSpan(Tux3Reader.SuperblockOffset, Tux3Reader.DiskSuperSize).ToArray()));
  }

  [Test, Category("Spec")]
  public void Reader_AcceptsKnown2012DiskRevision() {
    using var stream = new MemoryStream(BuildNativeImage(legacy2012: true), writable: false);
    using var reader = new Tux3Reader(stream);
    Assert.That(reader.Revision, Is.EqualTo("2012-12-20"));
  }

  [Test, Category("Regression")]
  public void FormerPrivateTux3SuprMagic_IsRejected() {
    var image = new byte[16 * 1024];
    "TUX3SUPR"u8.CopyTo(image.AsSpan(Tux3Reader.SuperblockOffset));
    using var stream = new MemoryStream(image, writable: false);

    Assert.Throws<InvalidDataException>(() => _ = new Tux3Reader(stream));
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesNativeMetadataAndSafeMaintenanceSurface() {
    var descriptor = new Tux3FormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor.MagicSignatures, Has.Count.EqualTo(2));
      Assert.That(descriptor.MagicSignatures.All(signature => signature.Offset == Tux3Reader.SuperblockOffset), Is.True);
      Assert.That(descriptor.MagicSignatures[0].Bytes, Is.EqualTo(Tux3Reader.Magic));
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.False);
      Assert.That(descriptor, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveCreatable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveModifiable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor.Description, Does.Contain("native big-endian superblock"));
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExtractsNativeMetadataWithoutInventedFileTable() {
    var image = BuildNativeImage();
    using var stream = new MemoryStream(image, writable: false);
    var descriptor = new Tux3FormatDescriptor();
    var output = Path.Combine(Path.GetTempPath(), $"tux3-native-{Guid.NewGuid():N}");
    Directory.CreateDirectory(output);

    try {
      descriptor.Extract(stream, output, null, null);
      var metadata = File.ReadAllText(Path.Combine(output, "metadata.ini"));
      Assert.Multiple(() => {
        Assert.That(metadata, Does.Contain("parse_status=superblock-only"));
        Assert.That(metadata, Does.Contain("revision=2014-05-06"));
        Assert.That(metadata, Does.Contain("blockbits=12"));
        Assert.That(metadata, Does.Not.Contain("TUX3WORM"));
        Assert.That(Directory.EnumerateFiles(output).Select(Path.GetFileName),
          Is.EquivalentTo(new[] { "FULL.tux3", "metadata.ini", "superblock.bin" }));
      });
    } finally {
      Directory.Delete(output, recursive: true);
    }
  }

  [Test, Category("Spec")]
  public void AllocationTree_ParsesBNodeILeafDLeafAndBitmapRuns() {
    var image = BuildAllocationMappedImage();
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux3Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.JournalValid, Is.True);
      Assert.That(reader.JournalRecords, Is.Empty);
      Assert.That(reader.AllocationMapValid, Is.True);
      Assert.That(reader.NativeMetadataStatus, Is.EqualTo("allocation-map+journal"));
      Assert.That(reader.AllocationRuns, Is.EqualTo(new[] {
        new Tux3BlockRun(0, 6, true),
        new Tux3BlockRun(6, 10, false),
      }));
    });

    using var layoutStream = new MemoryStream(image, writable: false);
    var extents = new Tux3FormatDescriptor().EnumerateExtents(layoutStream).ToArray();
    Assert.That(extents, Has.Length.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(extents[0].Offset, Is.Zero);
      Assert.That(extents[0].Length, Is.EqualTo(6L * NativeBlockSize));
      Assert.That(extents[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
      Assert.That(extents[1].Offset, Is.EqualTo(6L * NativeBlockSize));
      Assert.That(extents[1].Length, Is.EqualTo(10L * NativeBlockSize));
      Assert.That(extents[1].Kind, Is.EqualTo(DefragBlockKind.Free));
    });
  }

  [Test, Category("Spec")]
  public void AllocationJournal_ReplaysAllocationOnlyRecordsForEffectiveBitmap() {
    var payload = Concat(
      AllocationLog(Tux3JournalRecordType.BlockAllocate, 1, 7),
      AllocationLog(Tux3JournalRecordType.BlockFree, 1, 8));
    var image = BuildAllocationMappedImage([payload], 8);
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux3Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.JournalValid, Is.True);
      Assert.That(reader.JournalRecords.Select(static record => record.Type), Is.EqualTo(new[] {
        Tux3JournalRecordType.BlockAllocate,
        Tux3JournalRecordType.BlockFree,
      }));
      Assert.That(reader.JournalRecords[0].Block, Is.EqualTo(7));
      Assert.That(reader.JournalRecords[0].Count, Is.EqualTo(1));
      Assert.That(reader.AllocationMapValid, Is.True);
      Assert.That(reader.AllocationRuns, Is.EqualTo(new[] {
        new Tux3BlockRun(0, 8, true),
        new Tux3BlockRun(8, 8, false),
      }));
    });
  }

  [Test, Category("HappyPath")]
  public void WipeUnusedSpace_UsesEffectiveNativeBitmapWithoutTouchingAllocatedBlocks() {
    var payload = Concat(
      AllocationLog(Tux3JournalRecordType.BlockAllocate, 1, 7),
      AllocationLog(Tux3JournalRecordType.BlockFree, 1, 8));
    var image = BuildAllocationMappedImage([payload], 8);
    image[7 * NativeBlockSize] = 0x5A;
    Array.Fill(image, (byte)0xA5, 8 * NativeBlockSize, 8 * NativeBlockSize);
    using var stream = new MemoryStream(image, writable: true);

    var wiped = ((IWipeEmpty)new Tux3FormatDescriptor()).WipeUnusedSpace(
      stream,
      wipeClusterTips: false,
      wipeDeletedEntries: false);

    Assert.Multiple(() => {
      Assert.That(wiped, Is.EqualTo(8L * NativeBlockSize));
      Assert.That(image[7 * NativeBlockSize], Is.EqualTo(0x5A));
      Assert.That(image.AsSpan(8 * NativeBlockSize, 8 * NativeBlockSize).ToArray(), Is.All.Zero);
    });
  }

  [Test, Category("Regression")]
  public void ActiveStructuralJournalRecord_DisablesAllocationMapAndWipe() {
    var image = BuildAllocationMappedImage([BNodeAddLog(2, 3, 0)]);
    var original = image.ToArray();
    using var readerStream = new MemoryStream(image, writable: false);
    using var reader = new Tux3Reader(readerStream);

    Assert.Multiple(() => {
      Assert.That(reader.JournalValid, Is.True);
      Assert.That(reader.JournalRecords.Single().Type, Is.EqualTo(Tux3JournalRecordType.BNodeAdd));
      Assert.That(reader.AllocationMapValid, Is.False);
      Assert.That(reader.NativeMetadataStatus, Is.EqualTo("journal-needs-structural-replay"));
    });

    using var wipeStream = new MemoryStream(image, writable: true);
    var wiped = ((IWipeEmpty)new Tux3FormatDescriptor()).WipeUnusedSpace(
      wipeStream,
      wipeClusterTips: false,
      wipeDeletedEntries: false);
    Assert.Multiple(() => {
      Assert.That(wiped, Is.Zero);
      Assert.That(image, Is.EqualTo(original));
    });
  }

  [Test, Category("Regression")]
  public void LatestUnify_MakesOlderStructuralLogRecordsInactive() {
    var newer = Concat(
      [(byte)Tux3JournalRecordType.Unify],
      AllocationLog(Tux3JournalRecordType.BlockAllocate, 1, 9));
    var image = BuildAllocationMappedImage([BNodeAddLog(2, 3, 0), newer]);
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux3Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.JournalValid, Is.True);
      Assert.That(reader.JournalRecords.Select(static record => record.Type), Is.EqualTo(new[] {
        Tux3JournalRecordType.BNodeAdd,
        Tux3JournalRecordType.Unify,
        Tux3JournalRecordType.BlockAllocate,
      }));
      Assert.That(reader.AllocationMapValid, Is.True);
      Assert.That(reader.AllocationRuns.Any(run => run.IsAllocated && run.StartBlock <= 9 && 9 < run.StartBlock + run.BlockCount), Is.True);
    });
  }

  [Test, Category("Regression")]
  public void MalformedJournal_DisablesAllocationMap() {
    var image = BuildAllocationMappedImage([[0x99]]);
    using var stream = new MemoryStream(image, writable: false);
    using var reader = new Tux3Reader(stream);

    Assert.Multiple(() => {
      Assert.That(reader.JournalValid, Is.False);
      Assert.That(reader.AllocationMapValid, Is.False);
      Assert.That(reader.NativeMetadataStatus, Is.EqualTo("journal-invalid"));
    });
  }

  [Test, Category("Spec")]
  public void ExtentMap_ReservesUndecodedVolumeAndMarksOnlyExternalTailFree() {
    const long declaredLength = 3L << 12;
    var image = BuildNativeImage(volBlocks: 3);
    using var stream = new MemoryStream(image, writable: false);

    var extents = new Tux3FormatDescriptor().EnumerateExtents(stream).ToArray();

    Assert.That(extents, Has.Length.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(extents[0].Offset, Is.Zero);
      Assert.That(extents[0].Length, Is.EqualTo(declaredLength));
      Assert.That(extents[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
      Assert.That(extents[1].Offset, Is.EqualTo(declaredLength));
      Assert.That(extents[1].Length, Is.EqualTo(image.LongLength - declaredLength));
      Assert.That(extents[1].Kind, Is.EqualTo(DefragBlockKind.Free));
    });
  }

  [Test, Category("HappyPath")]
  public void WipeUnusedSpace_ZeroesOnlyExternalTail() {
    const int declaredLength = 3 << 12;
    var image = BuildNativeImage(volBlocks: 3);
    image[declaredLength - 1] = 0x5A;
    Array.Fill(image, (byte)0xA5, declaredLength, image.Length - declaredLength);
    using var stream = new MemoryStream(image, writable: true);
    var descriptor = new Tux3FormatDescriptor();

    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(stream, wipeClusterTips: false, wipeDeletedEntries: false);

    Assert.Multiple(() => {
      Assert.That(wiped, Is.EqualTo(image.Length - declaredLength));
      Assert.That(image[declaredLength - 1], Is.EqualTo(0x5A));
      Assert.That(image.AsSpan(declaredLength).ToArray(), Is.All.Zero);
    });
  }

  [Test, Category("HappyPath")]
  public void Shrink_RemovesOnlyExternalTail() {
    const int declaredLength = 3 << 12;
    var image = BuildNativeImage(volBlocks: 3);
    Array.Fill(image, (byte)0xA5, declaredLength, image.Length - declaredLength);
    using var input = new MemoryStream(image, writable: false);
    using var output = new MemoryStream();

    new Tux3FormatDescriptor().Shrink(input, output);

    Assert.Multiple(() => {
      Assert.That(output.Length, Is.EqualTo(declaredLength));
      Assert.That(output.ToArray(), Is.EqualTo(image[..declaredLength]));
    });
  }

  [Test, Category("Regression")]
  public void TruncatedDeclaredVolume_IsNeverTreatedAsFreeOrShrunk() {
    var image = BuildNativeImage(volBlocks: 5);
    image[^1] = 0xA5;
    var descriptor = new Tux3FormatDescriptor();

    using var layoutStream = new MemoryStream(image, writable: false);
    var extents = descriptor.EnumerateExtents(layoutStream).ToArray();
    Assert.That(extents, Has.Length.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(extents[0].Offset, Is.Zero);
      Assert.That(extents[0].Length, Is.EqualTo(image.LongLength));
      Assert.That(extents[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    });

    var wipeCopy = image.ToArray();
    using var wipeStream = new MemoryStream(wipeCopy, writable: true);
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(wipeStream, wipeClusterTips: false, wipeDeletedEntries: false);
    Assert.Multiple(() => {
      Assert.That(wiped, Is.Zero);
      Assert.That(wipeCopy, Is.EqualTo(image));
    });

    using var shrinkInput = new MemoryStream(image, writable: false);
    using var shrinkOutput = new MemoryStream();
    descriptor.Shrink(shrinkInput, shrinkOutput);
    Assert.That(shrinkOutput.ToArray(), Is.EqualTo(image));
  }

  [Test, Category("Regression")]
  public void OverflowingDeclaredVolume_FailsClosedInsteadOfTrustingWrappedShift() {
    const ushort blockBits = 12;
    const ulong volBlocks = (1UL << 52) + 3;
    const ulong wrappedLength = volBlocks << blockBits;
    var image = BuildNativeImage(volBlocks: volBlocks, blockBits: blockBits);
    image[^1] = 0xA5;
    var descriptor = new Tux3FormatDescriptor();

    Assert.That(wrappedLength, Is.EqualTo(3UL << blockBits),
      "The regression fixture must wrap a mathematically >64-bit volume into a plausible small boundary.");

    using var layoutStream = new MemoryStream(image, writable: false);
    var extents = descriptor.EnumerateExtents(layoutStream).ToArray();
    Assert.That(extents, Has.Length.EqualTo(1));
    Assert.Multiple(() => {
      Assert.That(extents[0].Offset, Is.Zero);
      Assert.That(extents[0].Length, Is.EqualTo(image.LongLength));
      Assert.That(extents[0].Kind, Is.EqualTo(DefragBlockKind.MetadataReserved));
    });

    var wipeCopy = image.ToArray();
    using var wipeStream = new MemoryStream(wipeCopy, writable: true);
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(wipeStream, wipeClusterTips: false, wipeDeletedEntries: false);
    Assert.Multiple(() => {
      Assert.That(wiped, Is.Zero);
      Assert.That(wipeCopy, Is.EqualTo(image));
    });

    using var shrinkInput = new MemoryStream(image, writable: false);
    using var shrinkOutput = new MemoryStream();
    descriptor.Shrink(shrinkInput, shrinkOutput);
    Assert.That(shrinkOutput.ToArray(), Is.EqualTo(image));
  }
}

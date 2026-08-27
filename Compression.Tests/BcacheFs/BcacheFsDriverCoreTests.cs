#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public class BcacheFsDriverCoreTests {
  [Test, Category("Contract")]
  public void OnDiskCatalog_MatchesMetadataVersion138() {
    Assert.Multiple(() => {
      Assert.That(BcacheFsOnDiskCatalog.MetadataVersion, Is.EqualTo((1 << 10) | 38));
      Assert.That(BcacheFsOnDiskCatalog.MaxBtreeDepth, Is.EqualTo(4));
      Assert.That(BcacheFsOnDiskCatalog.KnownBtrees, Has.Count.EqualTo(28));
      Assert.That(BcacheFsOnDiskCatalog.KnownBtrees[0], Is.EqualTo(BcacheFsBtreeId.Extents));
      Assert.That(BcacheFsOnDiskCatalog.KnownBtrees[^1], Is.EqualTo(BcacheFsBtreeId.StripeBackpointers));
      Assert.That(BcacheFsOnDiskCatalog.KnownKeyTypes, Has.Count.EqualTo(38));
      Assert.That((byte)BcacheFsKeyType.LoggedOpStripeUpdate, Is.EqualTo(37));
      Assert.That(Enum.GetValues<BcacheFsSuperblockFieldType>(), Has.Length.EqualTo(17));
      Assert.That(Enum.GetValues<BcacheFsJournalEntryType>(), Has.Length.EqualTo(16));
      Assert.That(Enum.GetValues<BcacheFsChecksumType>(), Has.Length.EqualTo(8));
      Assert.That(Enum.GetValues<BcacheFsCompressionType>(), Has.Length.EqualTo(6));
    });
  }

  [Test, Category("Contract")]
  public void OnDiskCatalog_ReconstructabilityMatchesKernelRules() {
    var allocationTrees = Enum.GetValues<BcacheFsBtreeId>()
      .Where(BcacheFsOnDiskCatalog.IsAllocationTree)
      .ToArray();

    Assert.That(allocationTrees, Is.EquivalentTo(new[] {
      BcacheFsBtreeId.Alloc,
      BcacheFsBtreeId.Backpointers,
      BcacheFsBtreeId.StripeBackpointers,
      BcacheFsBtreeId.NeedDiscard,
      BcacheFsBtreeId.Freespace,
      BcacheFsBtreeId.BucketGens,
      BcacheFsBtreeId.Lru,
      BcacheFsBtreeId.Accounting,
      BcacheFsBtreeId.ReconcileWork,
      BcacheFsBtreeId.ReconcileHipri,
      BcacheFsBtreeId.ReconcilePending,
      BcacheFsBtreeId.ReconcileScan,
    }));

    Assert.Multiple(() => {
      Assert.That(BcacheFsOnDiskCatalog.CanReconstruct(BcacheFsBtreeId.SnapshotTrees), Is.True);
      Assert.That(BcacheFsOnDiskCatalog.CanReconstruct(BcacheFsBtreeId.DeletedInodes), Is.True);
      Assert.That(BcacheFsOnDiskCatalog.CanReconstruct(BcacheFsBtreeId.SubvolumeChildren), Is.True);
      Assert.That(BcacheFsOnDiskCatalog.CanReconstruct(BcacheFsBtreeId.Extents), Is.False);
      Assert.That(BcacheFsOnDiskCatalog.CanReconstruct(BcacheFsBtreeId.Inodes), Is.False);
      Assert.That(BcacheFsOnDiskCatalog.CanReconstruct(BcacheFsBtreeId.Xattrs), Is.False);
      Assert.That(BcacheFsOnDiskCatalog.CanReconstruct(BcacheFsBtreeId.Reflink), Is.False);
    });
  }

  [Test, Category("Journal")]
  public void JournalParser_PreservesKnownAndUnknownEntries() {
    const ulong filesystemMagic = 0x1122334455667788UL;
    var firstPayload = Enumerable.Range(0, 16).Select(i => (byte)(i * 7)).ToArray();
    var secondPayload = Enumerable.Range(0, 8).Select(i => (byte)(255 - i)).ToArray();
    var payloadBytes =
      BcacheFsJournalFormat.EntryHeaderBytes + firstPayload.Length
      + BcacheFsJournalFormat.EntryHeaderBytes + secondPayload.Length;
    var bytes = new byte[BcacheFsJournalFormat.SetHeaderBytes + payloadBytes];

    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16),
      BcacheFsJournalFormat.ExpectedMagic(filesystemMagic));
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 1234);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), BcacheFsOnDiskCatalog.MetadataVersion);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36),
      (uint)BcacheFsChecksumType.Crc32CNonzero | (1U << 5));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)(payloadBytes / 8));
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), 12);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(46), 34);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), 1200);

    var cursor = BcacheFsJournalFormat.SetHeaderBytes;
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(cursor), (ushort)(firstPayload.Length / 8));
    bytes[cursor + 2] = (byte)BcacheFsBtreeId.Extents;
    bytes[cursor + 3] = 0;
    bytes[cursor + 4] = (byte)BcacheFsJournalEntryType.BtreeKeys;
    firstPayload.CopyTo(bytes.AsSpan(cursor + BcacheFsJournalFormat.EntryHeaderBytes));
    cursor += BcacheFsJournalFormat.EntryHeaderBytes + firstPayload.Length;

    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(cursor), (ushort)(secondPayload.Length / 8));
    bytes[cursor + 2] = 61; // future/unknown btree id is structural data, not a parse failure
    bytes[cursor + 3] = 3;
    bytes[cursor + 4] = 0xFE; // future journal entry type
    secondPayload.CopyTo(bytes.AsSpan(cursor + BcacheFsJournalFormat.EntryHeaderBytes));

    Assert.That(BcacheFsJournalFormat.TryParse(bytes, filesystemMagic, out var set, out var error),
      Is.True, error);
    Assert.That(set, Is.Not.Null);

    Assert.Multiple(() => {
      Assert.That(set!.Header.Sequence, Is.EqualTo(1234));
      Assert.That(set.Header.LastSequence, Is.EqualTo(1200));
      Assert.That(set.Header.ChecksumType, Is.EqualTo(BcacheFsChecksumType.Crc32CNonzero));
      Assert.That(set.Header.NoFlush, Is.True);
      Assert.That(set.Entries, Has.Count.EqualTo(2));
      Assert.That(set.Entries[0].Header.Type, Is.EqualTo(BcacheFsJournalEntryType.BtreeKeys));
      Assert.That(set.Entries[0].Payload, Is.EqualTo(firstPayload).AsCollection);
      Assert.That(set.Entries[1].Header.Type, Is.Null);
      Assert.That(set.Entries[1].Header.BtreeId, Is.EqualTo(61));
      Assert.That(set.Entries[1].Header.Level, Is.EqualTo(3));
      Assert.That(set.Entries[1].Payload, Is.EqualTo(secondPayload).AsCollection);
    });
  }

  [Test, Category("Journal")]
  public void JournalParser_RejectsTruncatedEntryInsteadOfReadingPastSet() {
    const ulong filesystemMagic = 0x8877665544332211UL;
    var bytes = new byte[BcacheFsJournalFormat.SetHeaderBytes + 8];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16),
      BcacheFsJournalFormat.ExpectedMagic(filesystemMagic));
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 1);

    // One payload word is available, but the entry claims two after its header.
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(BcacheFsJournalFormat.SetHeaderBytes), 2);

    Assert.That(BcacheFsJournalFormat.TryParse(bytes, filesystemMagic, out _, out var error), Is.False);
    Assert.That(error, Does.Contain("past the set boundary"));
  }

  [Test, Category("Journal")]
  public void JournalParser_RejectsWrongFilesystemMagic() {
    var bytes = new byte[BcacheFsJournalFormat.SetHeaderBytes];
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0xDEADBEEFCAFEBABEUL);

    Assert.That(BcacheFsJournalFormat.TryParse(bytes, 7, out _, out var error), Is.False);
    Assert.That(error, Does.Contain("does not match filesystem magic"));
  }
}

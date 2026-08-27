using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public class RefsTests {

  /// <summary>Synthesizes a modern ReFS 3.14 VBR with a valid FSRS checksum.</summary>
  private static byte[] BuildMinimal() {
    var image = new byte[4096];
    Encoding.ASCII.GetBytes("ReFS").CopyTo(image.AsSpan(3));
    Encoding.ASCII.GetBytes("FSRS").CopyTo(image.AsSpan(0x10));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x14, 2), 0x200);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18, 8), 1024UL);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20, 4), 512);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x24, 4), 8);
    image[0x28] = 3;
    image[0x29] = 14;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x2A, 2), 2); // CRC64 metadata refs
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x2C, 4), 0x66);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x38, 8), 0x0123456789ABCDEFUL);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x40, 8), 64UL * 1024 * 1024);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x16, 2), ComputeVbrChecksum(image));
    return image;
  }

  private static ushort ComputeVbrChecksum(ReadOnlySpan<byte> vbr) {
    ushort sum = 0;
    for (var i = 3; i < 512; ++i) {
      if (i is 0x16 or 0x17) continue;
      sum = (ushort)((sum >> 1) | (sum << 15));
      sum = unchecked((ushort)(sum + vbr[i]));
    }
    return sum;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Refs"));
    Assert.That(d.DisplayName, Is.EqualTo("ReFS"));
    Assert.That(d.Extensions, Does.Contain(".refs"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.GreaterThanOrEqualTo(1));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(3));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.85).Within(0.01));
  }

  [Test, Category("HappyPath")]
  public void List_EmitsMinimumSurface() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.GreaterThanOrEqualTo(2));
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.refs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("volume_header.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesParsedHeader() {
    var img = BuildMinimal();
    using var ms = new MemoryStream(img);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    var outDir = Path.Combine(Path.GetTempPath(), "refs_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      d.Extract(ms, outDir, null, null);
      Assert.That(File.Exists(Path.Combine(outDir, "FULL.refs")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(outDir, "volume_header.bin")), Is.True);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("oem_id=ReFS"));
      Assert.That(meta, Does.Contain("sector_size=512"));
      Assert.That(meta, Does.Contain("sectors_per_cluster=8"));
      Assert.That(meta, Does.Contain("bytes_per_cluster=4096"));
      Assert.That(meta, Does.Contain("total_sectors=1024"));
      Assert.That(meta, Does.Contain("version_major=3"));
      Assert.That(meta, Does.Contain("version_minor=14"));
      Assert.That(meta, Does.Contain("checksum_algorithm=0x0002"));
      Assert.That(meta, Does.Contain("vbr_checksum_valid=True"));
      Assert.That(meta, Does.Contain("bytes_per_container=67108864"));
      Assert.That(meta, Does.Contain("fsrs_found=True"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_NoMagic_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[1024]);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.refs"));
  }

  [Test, Category("ErrorHandling")]
  public void List_TinyImage_DoesNotThrow() {
    using var ms = new MemoryStream(new byte[16]);
    var d = new FileSystem.Refs.RefsFormatDescriptor();
    Assert.DoesNotThrow(() => d.List(ms, null));
  }

  [Test, Category("HappyPath")]
  public void RedoCodec_RoundTripsRecords() {
    var records = new[] {
      RefsRedoRecord.Create(
        RefsRedoOpcode.OpenTableFromTablePath,
        0x600,
        new byte[] { 1, 2, 3, 4 },
        flags: RefsRedoFlags.TransactionStart),
      RefsRedoRecord.Create(
        RefsRedoOpcode.UpdateRow,
        0x1234,
        new byte[] { 0xAA, 0xBB, 0xCC },
        tableKeyPathLength: 2,
        valueComponentCount: 1),
    };

    var bytes = RefsRedoCodec.SerializeBlock(records);
    var decoded = RefsRedoCodec.ParseBlock(bytes);

    Assert.That(decoded, Has.Count.EqualTo(2));
    Assert.Multiple(() => {
      Assert.That(decoded[0].Opcode, Is.EqualTo(RefsRedoOpcode.OpenTableFromTablePath));
      Assert.That(decoded[0].ObjectId, Is.EqualTo(0x600));
      Assert.That(decoded[0].Flags, Is.EqualTo(RefsRedoFlags.TransactionStart));
      Assert.That(decoded[0].Payload, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
      Assert.That(decoded[1].Opcode, Is.EqualTo(RefsRedoOpcode.UpdateRow));
      Assert.That(decoded[1].ObjectId, Is.EqualTo(0x1234));
      Assert.That(decoded[1].TableKeyPathLength, Is.EqualTo(2));
      Assert.That(decoded[1].ValueComponentCount, Is.EqualTo(1));
      Assert.That(decoded[1].Payload, Is.EqualTo(new byte[] { 0xAA, 0xBB, 0xCC }));
    });
  }

  [Test, Category("HappyPath")]
  public void MLogRecovery_FiltersBeforeCheckpointBoundary() {
    var records = new[] {
      RecoveryRecord(0x00000001_00000001UL, 0),
      RecoveryRecord(0x00000001_00000002UL, 0x00000001_00000001UL),
      RecoveryRecord(0x00000001_00000003UL, 0x00000001_00000002UL),
    };

    var replay = RefsMLogRecovery.SelectForReplay(records, 0x00000001_00000002UL);

    Assert.That(replay.Select(r => r.Record.Lsn), Is.EqualTo(new[] {
      0x00000001_00000002UL,
      0x00000001_00000003UL,
    }));
  }

  [Test, Category("ErrorHandling")]
  public void MLogRecovery_RejectsBrokenLiveChain() {
    var records = new[] {
      RecoveryRecord(0x00000002_00000010UL, 0),
      RecoveryRecord(0x00000002_00000011UL, 0x00000002_0000000FUL),
    };

    Assert.Throws<InvalidDataException>(() => RefsMLogRecovery.SelectForReplay(records, 0));
  }

  [Test, Category("HappyPath")]
  public void MLogRecovery_AllowsCircularGenerationBoundary() {
    var records = new[] {
      RecoveryRecord(0x00000002_FFFFFFFFUL, 0),
      RecoveryRecord(0x00000003_00000000UL, 0),
      RecoveryRecord(0x00000003_00000001UL, 0x00000003_00000000UL),
    };

    Assert.DoesNotThrow(() => RefsMLogRecovery.SelectForReplay(records, 0));
  }

  [Test, Category("HappyPath")]
  public void CheckpointRootLocator_DecodesDirectOffsetList() {
    var checkpoint = BuildCheckpointRootOffsets(indirect: false, referenceSize: 48, descriptorBase: 0x200);

    Assert.That(RefsCheckpointCommitter.GetRootDescriptorOffset(checkpoint, 0, 48), Is.EqualTo(0x200));
    Assert.That(RefsCheckpointCommitter.GetRootDescriptorOffset(checkpoint, 12, 48), Is.EqualTo(0x200 + 12 * 48));
  }

  [Test, Category("HappyPath")]
  public void CheckpointRootLocator_DecodesIndirectOffsetList() {
    var checkpoint = BuildCheckpointRootOffsets(indirect: true, referenceSize: 48, descriptorBase: 0x300);

    Assert.That(RefsCheckpointCommitter.GetRootDescriptorOffset(checkpoint, 0, 48), Is.EqualTo(0x300));
    Assert.That(RefsCheckpointCommitter.GetRootDescriptorOffset(checkpoint, 7, 48), Is.EqualTo(0x300 + 7 * 48));
  }

  [Test, Category("ErrorHandling")]
  public void CheckpointRootLocator_RejectsOutOfRangeDescriptor() {
    var checkpoint = BuildCheckpointRootOffsets(indirect: false, referenceSize: 48, descriptorBase: 0x200);
    BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.AsSpan(0x94, 4), checked((uint)(checkpoint.Length - 8)));

    Assert.Throws<InvalidDataException>(() => RefsCheckpointCommitter.GetRootDescriptorOffset(checkpoint, 0, 48));
  }

  [Test, Category("HappyPath")]
  public void AllocatorRootVerifier_DecodesCompactRows() {
    var allocated = new byte[24];
    BinaryPrimitives.WriteUInt16LittleEndian(allocated.AsSpan(0x10, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(allocated.AsSpan(0x12, 2), 0x02);
    BinaryPrimitives.WriteUInt16LittleEndian(allocated.AsSpan(0x16, 2), 64);
    var free = new byte[24];
    BinaryPrimitives.WriteUInt16LittleEndian(free.AsSpan(0x10, 2), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(free.AsSpan(0x12, 2), 0x05);
    BinaryPrimitives.WriteUInt16LittleEndian(free.AsSpan(0x16, 2), 0);

    Assert.Multiple(() => {
      Assert.That(RefsAllocatorRootVerifier.IsStructurallyValid(allocated, 64), Is.True);
      Assert.That(RefsAllocatorRootVerifier.ReadAllocated(allocated, 64, 17), Is.True);
      Assert.That(RefsAllocatorRootVerifier.IsStructurallyValid(free, 64), Is.True);
      Assert.That(RefsAllocatorRootVerifier.ReadAllocated(free, 64, 17), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void AllocatorRootVerifier_DecodesPartialBitmapRows() {
    var value = new byte[0x18 + 2048];
    const ulong rangeLength = 16;
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x10, 2), 15);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x12, 2), 0x01);
    BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(0x16, 2), 1);
    value[0x18] = 1 << 7;

    Assert.Multiple(() => {
      Assert.That(RefsAllocatorRootVerifier.IsStructurallyValid(value, rangeLength), Is.True);
      Assert.That(RefsAllocatorRootVerifier.ReadAllocated(value, rangeLength, 7), Is.True);
      Assert.That(RefsAllocatorRootVerifier.ReadAllocated(value, rangeLength, 6), Is.False);
    });
  }

  private static byte[] BuildCheckpointRootOffsets(bool indirect, int referenceSize, int descriptorBase) {
    var checkpoint = new byte[4096];
    Encoding.ASCII.GetBytes("CHKP").CopyTo(checkpoint, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.AsSpan(0x78, 4), indirect ? 0x0202U : 0x0002U);
    BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.AsSpan(0x90, 4), 13);
    var listBase = indirect ? 0xB0 : 0x94;
    if (indirect)
      BinaryPrimitives.WriteUInt32LittleEndian(checkpoint.AsSpan(0x94, 4), checked((uint)listBase));
    for (var i = 0; i < 13; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(
        checkpoint.AsSpan(listBase + i * 4, 4),
        checked((uint)(descriptorBase + i * referenceSize)));
    return checkpoint;
  }

  private static RefsMLogRecoveryRecord RecoveryRecord(ulong lsn, ulong previousLsn) {
    var record = new RefsMLogDataRecord(
      FormatMagic: 0x12345678,
      Lsn: lsn,
      PreviousLsn: previousLsn,
      EntryChecksum: 1,
      EntryHeaderOffset: 0x78,
      PayloadOffset: 0x38,
      RedoRecords: []);
    return new RefsMLogRecoveryRecord(0, record, new byte[RefsMLogCodec.LogBlockSize]);
  }
}

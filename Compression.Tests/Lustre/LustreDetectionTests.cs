using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ext;
using FileSystem.Lustre;

namespace Compression.Tests.Lustre;

[TestFixture]
public class LustreDetectionTests {

  // ── Legacy detection-only header dump (Stage-0 path, preserved) ─────────

  private static byte[] BuildMinimalLegacy(int payloadLen = 128) {
    var image = new byte[16 + payloadLen];
    Encoding.ASCII.GetBytes("LUSTRE").CopyTo(image.AsSpan(0, 6));
    for (var i = 0; i < payloadLen; i++) image[16 + i] = (byte)(i & 0xFF);
    return image;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IdentifiesByMagicAndExtension() {
    var d = new LustreFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Lustre"));
    Assert.That(d.Extensions, Does.Contain(".lustre"));
    Assert.That(d.Extensions, Does.Contain(".ost"));
    Assert.That(d.Extensions, Does.Contain(".mdt"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("LUSTRE"u8.ToArray()));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(new byte[] { 0x4C, 0x55, 0x73, 0x74 }));

    // The namespace projection remains read-only: Lustre LMA/LOV/FID xattrs are opaque,
    // so CanModify would over-promise add/remove/purge semantics. Maintenance below that
    // layer is still safe where it only touches proven-free blocks / trailing geometry.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.Not.InstanceOf<IArchiveModifiable>());
    Assert.That(d, Is.InstanceOf<IWipeEmpty>());
    Assert.That(d, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
    Assert.That(d, Is.Not.InstanceOf<IArchiveDefragmentable>());
  }

  [Test, Category("HappyPath")]
  public void Legacy_Dump_ListsMetadataAndRawObject() {
    var d = new LustreFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalLegacy(payloadLen: 256));
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "metadata.ini", "lustre-object.bin" }));
  }

  [Test, Category("HappyPath")]
  public void Legacy_Dump_MetadataDocumentsStageZeroPath() {
    var d = new LustreFormatDescriptor();
    using var ms = new MemoryStream(BuildMinimalLegacy());
    var stream = ((IArchiveFormatOperations)d).OpenEntry(ms, "metadata.ini", password: null);
    using var reader = new StreamReader(stream);
    var meta = reader.ReadToEnd();
    Assert.That(meta, Does.Contain("parse_status=detection-only"));
    Assert.That(meta, Does.Contain("magic_tag=LUSTRE"));
  }

  [Test, Category("ExceptionalCase")]
  public void NoMagic_NoExt4_Throws() {
    var d = new LustreFormatDescriptor();
    var noise = new byte[1024];
    Array.Fill(noise, (byte)0xAA);
    using var ms = new MemoryStream(noise);
    Assert.Throws<InvalidDataException>(() => d.List(ms, password: null));
  }

  [Test, Category("ExceptionalCase")]
  public void Legacy_Dump_MaintenanceRefusesNonLdiskfsBytes() {
    var d = new LustreFormatDescriptor();
    using var wipe = new MemoryStream(BuildMinimalLegacy(), writable: true);
    Assert.Throws<InvalidDataException>(() => ((IWipeEmpty)d).WipeUnusedSpace(wipe));

    using var source = new MemoryStream(BuildMinimalLegacy(), writable: false);
    using var target = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => ((IArchiveShrinkable)d).Shrink(source, target));
  }

  [Test, Category("HappyPath")]
  public void Description_FlagsLdiskfsDelegationAndOutOfScope() {
    var d = new LustreFormatDescriptor();
    var desc = d.Description.ToLowerInvariant();
    Assert.That(desc, Does.Contain("ldiskfs"));
    Assert.That(desc, Does.Contain("ext4"));
    Assert.That(desc, Does.Contain("out of scope"));
    Assert.That(desc, Does.Contain("shrink"));
    Assert.That(desc, Does.Contain("wipe"));
  }

  // ── ldiskfs (ext4) delegation + conservative maintenance ────────────────

  private static byte[] BuildLdiskfsImage(string volumeLabel, params (string Name, byte[] Data)[] files)
    => BuildLdiskfsImage(4096, volumeLabel, files);

  private static byte[] BuildLdiskfsImage(int totalBlocks, string volumeLabel, params (string Name, byte[] Data)[] files) {
    var writer = new ExtWriter();
    foreach (var (name, data) in files) writer.AddFile(name, data);
    // ext4 + journal, 4 KB blocks, volume label set (Lustre convention: "lustre-OST0000" / "MGS" / "lustre-MDT0000").
    return writer.Build(
      blockSize: 4096,
      totalBlocks: totalBlocks,
      version: ExtWriter.ExtVersion.Ext4,
      journal: true,
      volumeLabel: volumeLabel,
      inodeSize: 256);
  }

  private static (int BlockSize, long BlockOffset, long DescriptorOffset) FindInitializedFreeBlock(byte[] image) {
    var sb = image.AsSpan(1024, 1024);
    var blockSize = 1024 << (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(24, 4));
    var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(20, 4));
    var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(32, 4));
    var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(96, 4));
    ulong totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(4, 4));
    if ((featureIncompat & 0x80) != 0)
      totalBlocks |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x150, 4)) << 32;
    var descriptorSize = (featureIncompat & 0x80) != 0
      ? Math.Max(32, (int)BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(0xFE, 2)))
      : 32;
    var groupCount = (totalBlocks - firstDataBlock + blocksPerGroup - 1) / blocksPerGroup;
    var descriptorTableOffset = (long)(firstDataBlock + 1UL) * blockSize;

    for (ulong group = 0; group < groupCount; ++group) {
      var descriptorOffset = descriptorTableOffset + (long)group * descriptorSize;
      var descriptor = image.AsSpan((int)descriptorOffset, descriptorSize);
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(0x12, 2));
      if ((flags & 0x0002) != 0) continue; // BLOCK_UNINIT

      ulong bitmapBlock = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[..4]);
      if (descriptorSize >= 64)
        bitmapBlock |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.Slice(0x20, 4)) << 32;
      var bitmapOffset = checked((long)bitmapBlock * blockSize);
      var bitmap = image.AsSpan((int)bitmapOffset, blockSize);
      var groupFirst = firstDataBlock + group * blocksPerGroup;
      var blocksInGroup = (int)Math.Min((ulong)blocksPerGroup, totalBlocks - groupFirst);
      for (var bit = 0; bit < blocksInGroup; ++bit)
        if ((bitmap[bit >> 3] & (1 << (bit & 7))) == 0)
          return (blockSize, checked((long)(groupFirst + (uint)bit) * blockSize), descriptorOffset);
    }

    throw new AssertionException("Test image unexpectedly contains no initialized free ldiskfs block.");
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Image_RecognisedAndDelegated() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("lustre-OST0000",
      ("CONFIGS/mountdata", "lustre OST config"u8.ToArray()),
      ("OBJECTS/0_0", new byte[256]));

    using var ms = new MemoryStream(data);
    var entries = d.List(ms, password: null);

    // metadata.ini + raw ldiskfs image + the two files surfaced under ldiskfs/.
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("lustre-object.bin"));
    Assert.That(names.Any(n => n.StartsWith("ldiskfs/", StringComparison.Ordinal)), Is.True,
      "Expected at least one ldiskfs/* entry from ext4 reader delegation.");
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Metadata_DocumentsPartialBackingStoreStatus() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("lustre-MDT0000",
      ("CONFIGS/mountdata", "lustre MDT config"u8.ToArray()));

    using var ms = new MemoryStream(data);
    using var meta = ((IArchiveFormatOperations)d).OpenEntry(ms, "metadata.ini", password: null);
    using var sr = new StreamReader(meta);
    var text = sr.ReadToEnd();
    Assert.That(text, Does.Contain("parse_status=partial-ldiskfs"));
    Assert.That(text, Does.Contain("backing_fs=ldiskfs (ext4-compatible)"));
    Assert.That(text, Does.Contain("ldiskfs_volume_label=lustre-MDT0000"));
    Assert.That(text, Does.Contain("bitmap-proven free blocks"));
    Assert.That(text, Does.Contain("not the Lustre logical view").IgnoreCase
      .Or.Contain("NOT the Lustre logical view"));
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Extract_ReturnsFileBytesFromDelegatedReader() {
    var d = new LustreFormatDescriptor();
    var payload = new byte[1024];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
    var data = BuildLdiskfsImage("lustre-OST0000", ("OBJECTS/0_42", payload));

    using var ms = new MemoryStream(data);
    var entries = d.List(ms, password: null);
    var objectEntry = entries.FirstOrDefault(e => e.Name == "ldiskfs/OBJECTS/0_42");
    Assert.That(objectEntry, Is.Not.Null,
      "Expected ldiskfs/OBJECTS/0_42 entry surfaced via ext4 reader delegation. "
      + "Names: " + string.Join(",", entries.Select(e => e.Name)));

    using var ms2 = new MemoryStream(data);
    using var stream = ((IArchiveFormatOperations)d).OpenEntry(ms2, "ldiskfs/OBJECTS/0_42", password: null);
    using var mem = new MemoryStream();
    stream.CopyTo(mem);
    Assert.That(mem.ToArray(), Is.EqualTo(payload),
      "ldiskfs delegation must return the exact file bytes the ext4 reader produces.");
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Wipe_ZeroesBitmapFreeBlocks_WithoutTouchingLiveObjects() {
    var d = new LustreFormatDescriptor();
    var payload = Enumerable.Range(0, 1536).Select(i => (byte)(i * 17)).ToArray();
    var data = BuildLdiskfsImage("lustre-OST0000", ("OBJECTS/0_42", payload));
    var (blockSize, freeOffset, _) = FindInitializedFreeBlock(data);
    data.AsSpan((int)freeOffset, blockSize).Fill(0xA5);

    using var image = new MemoryStream(data, writable: true);
    var wiped = ((IWipeEmpty)d).WipeUnusedSpace(image, wipeClusterTips: true, wipeDeletedEntries: true);

    Assert.That(wiped, Is.GreaterThanOrEqualTo(blockSize));
    var after = image.ToArray();
    Assert.That(after.AsSpan((int)freeOffset, blockSize).ToArray(), Is.All.EqualTo((byte)0));

    using var verify = new MemoryStream(after, writable: false);
    using var entry = ((IArchiveFormatOperations)d).OpenEntry(verify, "ldiskfs/OBJECTS/0_42", password: null);
    using var actual = new MemoryStream();
    entry.CopyTo(actual);
    Assert.That(actual.ToArray(), Is.EqualTo(payload),
      "Bitmap-driven wipe must not change any allocated object bytes.");
  }

  [Test, Category("BoundaryCase")]
  public void Ldiskfs_Wipe_FailsClosedForBlockUninitGroup() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("lustre-OST0000", ("OBJECTS/0_1", "live"u8.ToArray()));
    var (blockSize, freeOffset, descriptorOffset) = FindInitializedFreeBlock(data);
    data.AsSpan((int)freeOffset, blockSize).Fill(0x5A);

    var flags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)descriptorOffset + 0x12, 2));
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan((int)descriptorOffset + 0x12, 2), (ushort)(flags | 0x0002));

    using var image = new MemoryStream(data, writable: true);
    _ = ((IWipeEmpty)d).WipeUnusedSpace(image);

    Assert.That(image.ToArray().AsSpan((int)freeOffset, blockSize).ToArray(), Is.All.EqualTo((byte)0x5A),
      "BLOCK_UNINIT means the on-disk bitmap is not authoritative; the whole group must be left alone.");
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_Shrink_TrimsTrailingFreeBlocks_AndPreservesTargetIdentityAndPayload() {
    var d = new LustreFormatDescriptor();
    var payload = Enumerable.Range(0, 8192).Select(i => (byte)(i ^ (i >> 8))).ToArray();
    var data = BuildLdiskfsImage(8192, "lustre-MDT0000", ("OBJECTS/0_7", payload));

    using var source = new MemoryStream(data, writable: false);
    using var target = new MemoryStream();
    ((IArchiveShrinkable)d).Shrink(source, target);

    Assert.That(target.Length, Is.LessThan(data.LongLength),
      "A small target in an oversized one-group ldiskfs image should lose its trailing free blocks.");

    target.Position = 0;
    using var meta = ((IArchiveFormatOperations)d).OpenEntry(target, "metadata.ini", password: null);
    using var sr = new StreamReader(meta);
    Assert.That(sr.ReadToEnd(), Does.Contain("ldiskfs_volume_label=lustre-MDT0000"),
      "Shrink must retain the Lustre target label rather than rebuilding as generic ext.");

    target.Position = 0;
    using var entry = ((IArchiveFormatOperations)d).OpenEntry(target, "ldiskfs/OBJECTS/0_7", password: null);
    using var actual = new MemoryStream();
    entry.CopyTo(actual);
    Assert.That(actual.ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Ldiskfs_LayoutAnalysis_ReportsGeometryWithoutClaimingStructuralRelayout() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("lustre-OST0000", ("OBJECTS/0_1", "payload"u8.ToArray()));
    using var image = new MemoryStream(data, writable: false);

    var analysis = ((ILayoutOptimizable)d).AnalyzeLayout(image);

    Assert.That(analysis.CurrentUnitSize, Is.EqualTo(4096));
    Assert.That(analysis.OptimalUnitSize, Is.EqualTo(4096));
    Assert.That(analysis.PotentialSavingsBytes, Is.Zero);
    Assert.That(analysis.RequiresRebuild, Is.Empty);
    Assert.That(analysis.Notes.Any(n => n.Contains("not offered", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  [Test, Category("BoundaryCase")]
  public void Ldiskfs_Reader_PreservesRawImageAsLustreObjectBin() {
    var d = new LustreFormatDescriptor();
    var data = BuildLdiskfsImage("MGS", ("CONFIGS/mountdata", "x"u8.ToArray()));

    using var ms = new MemoryStream(data);
    var entries = d.List(ms, password: null);
    var raw = entries.First(e => e.Name == "lustre-object.bin");
    Assert.That(raw.OriginalSize, Is.EqualTo(data.Length),
      "Raw image must be surfaced byte-for-byte so forensic / re-mount callers retain the original.");
  }

  [Test, Category("BoundaryCase")]
  public void Ldiskfs_DoesNotShadowGenericExt4_Detection() {
    // Critical invariant: the Lustre descriptor must NOT register ext4 magic in
    // its MagicSignatures — otherwise FormatDetector would mis-route plain ext4
    // images through Lustre. Confidence + content match would steal ext detection.
    var d = new LustreFormatDescriptor();
    foreach (var sig in d.MagicSignatures) {
      // ext4 magic is 0xEF53 LE at offset 1080 — neither tag should be at that offset.
      Assert.That(sig.Offset, Is.EqualTo(0),
        "Lustre magic must remain at offset 0 (LUSTRE/LUst) so ext4 superblock magic isn't shadowed.");
    }
  }
}

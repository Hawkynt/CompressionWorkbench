#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zoo;

namespace Compression.Tests.Zoo;

/// <summary>
/// Locks the byte-identity contract for <see cref="ZooModifier.AddFile"/>.
/// Zoo is not a strict pure-append format: every entry carries an explicit
/// <c>nextOffset</c> field that has to be patched when a new tail entry is
/// inserted. The contract this fixture pins is the <em>minimum-mutation</em>
/// shape:
/// <list type="bullet">
///   <item><description>Exactly one 4-byte little-endian slot in
///     <c>[0, oldLength)</c> is rewritten (the link slot — either the
///     archive header's <c>firstEntryOffset</c> at byte 24 for an
///     empty seed, or the tail entry's <c>nextOffset</c> field at
///     <c>tailHeaderOffset + 6</c>).</description></item>
///   <item><description>All other <c>[0, oldLength)</c> bytes stay
///     bitwise identical — no entry body, no CRC, no name, no comment
///     offset is rewritten.</description></item>
/// </list>
/// A future agent who silently converts <see cref="ZooModifier.AddFile"/>
/// into a rebuild-and-replace would mutate bytes outside the link slot
/// and trip this fixture loudly.
/// </summary>
[TestFixture]
public class ZooByteIdentityTests {

  // Archive header layout: 20-byte text + 4-byte magic + 4-byte
  // firstEntryOffset + 4-byte minusOffset + 1 majorVer + 1 minorVer.
  private const int ArchiveFirstEntryOffsetField = 24;

  // Within a directory entry header: tag(4) + type(1) + method(1) +
  // nextOffset(4) + ...  — so nextOffset starts at +6.
  private const int EntryNextOffsetField = 6;

  [Test, Category("ByteIdentity")]
  public void AddFile_ToSeededArchive_OnlyTailNextOffsetSlotIsRewritten() {
    var seed = BuildSeedZooBytes();
    var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    ZooModifier.AddFile(ms, "added.txt", "byte-identity-payload"u8.ToArray());

    Assert.That(ms.Length, Is.GreaterThan(seed.Length),
      "Add must enlarge the stream — new entry header + data appended.");

    // The seed entry's header starts at offset 34 (archive header size).
    // Its nextOffset field lives at offset 34 + 6 = 40.
    const int seedHeaderOffset = ZooConstants.ArchiveHeaderSize;
    const int linkOffset = seedHeaderOffset + EntryNextOffsetField;
    AssertExactlyOneFourByteSlotChanged(seed, ms.ToArray(), linkOffset);
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_ToEmptyArchive_OnlyFirstEntryOffsetSlotIsRewritten() {
    var seed = BuildEmptyZooBytes();
    var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    ZooModifier.AddFile(ms, "first.txt", "primum"u8.ToArray());

    Assert.That(ms.Length, Is.GreaterThan(seed.Length),
      "Add must enlarge the stream — first entry header + data appended.");

    // Empty seed: the archive header's firstEntryOffset (offset 24) is the
    // only link slot that gets patched.
    AssertExactlyOneFourByteSlotChanged(seed, ms.ToArray(), ArchiveFirstEntryOffsetField);
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_ViaDescriptor_OnlyTailNextOffsetSlotIsRewritten() {
    var seed = BuildSeedZooBytes();
    var ms = new MemoryStream();
    ms.Write(seed);
    ms.Position = 0;

    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-descriptor"u8.ToArray());
      ((IArchiveModifiable)new ZooFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "via.txt", false)]);
    } finally { File.Delete(tmp); }

    const int seedHeaderOffset = ZooConstants.ArchiveHeaderSize;
    const int linkOffset = seedHeaderOffset + EntryNextOffsetField;
    AssertExactlyOneFourByteSlotChanged(seed, ms.ToArray(), linkOffset);
  }

  [Test, Category("Descriptor")]
  public void Descriptor_AdvertisesInPlaceModify() {
    var d = new ZooFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  /// <summary>
  /// Verifies that, in <c>[0, before.Length)</c>, only the four bytes at
  /// <paramref name="slotOffset"/> changed — every other byte is bitwise
  /// identical to the original. Surfaces an ordered list of all mutated
  /// offsets when the contract breaks, so the failure mode is diagnosable.
  /// </summary>
  private static void AssertExactlyOneFourByteSlotChanged(
      byte[] before, byte[] after, int slotOffset) {
    Assert.That(after.Length, Is.GreaterThanOrEqualTo(before.Length),
      "Add must not shrink the archive.");

    var changedOffsets = new List<int>();
    for (var i = 0; i < before.Length; ++i)
      if (before[i] != after[i])
        changedOffsets.Add(i);

    Assert.That(changedOffsets, Is.Not.Empty,
      "Expected the 4-byte link slot to be rewritten; saw no mutation.");

    // Every mutated byte must lie inside the [slotOffset, slotOffset+4) window.
    foreach (var o in changedOffsets)
      Assert.That(o, Is.InRange(slotOffset, slotOffset + 3),
        $"Byte at offset {o} changed but lies outside the link slot " +
        $"[{slotOffset}..{slotOffset + 4}) — this means Add mutated unrelated " +
        "bytes, suggesting it has silently become a rebuild-and-replace.");
  }

  private static byte[] BuildSeedZooBytes() {
    var ms = new MemoryStream();
    using (var w = new ZooWriter(ms, leaveOpen: true, defaultMethod: ZooCompressionMethod.Store))
      w.AddEntry("seed.txt", "seed-content"u8.ToArray());
    return ms.ToArray();
  }

  private static byte[] BuildEmptyZooBytes() {
    var ms = new MemoryStream();
    using (var _ = new ZooWriter(ms, leaveOpen: true)) {
      // No entries — Finish() patches firstEntryOffset = 0.
    }
    return ms.ToArray();
  }
}

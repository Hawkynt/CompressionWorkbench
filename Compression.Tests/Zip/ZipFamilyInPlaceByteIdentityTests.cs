#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>
/// Locks the in-place byte-identity contract for every ZIP-family descriptor that
/// delegates Add/Remove to <see cref="ZipModifier"/>. Each descriptor must:
/// <list type="bullet">
///   <item>Implement <see cref="IArchiveModifiable"/>.</item>
///   <item>Advertise <see cref="FormatCapabilities.CanModify"/>.</item>
///   <item>Preserve every byte at <c>[0, oldCdStart)</c> across an <c>Add</c> —
///         pre-existing local file headers + payloads stay at their original
///         offsets verbatim. Only the central directory + EOCD are rewritten.</item>
/// </list>
/// The textbook in-place semantic: a 1-byte Add on an N-byte archive touches
/// only the trailing CD region, never re-encodes earlier entries.
/// </summary>
[TestFixture]
public class ZipFamilyInPlaceByteIdentityTests {

  /// <summary>
  /// Each row is (descriptor, archiveExtension). The extension only affects how
  /// the host tests structure on-disk inputs; the underlying container is ZIP for
  /// every row so the byte-identity check applies uniformly.
  /// </summary>
  private static IEnumerable<TestCaseData> ZipFamilyDescriptors() {
    yield return new TestCaseData(new FileFormat.Zip.ZipFormatDescriptor()).SetName("Zip");
    yield return new TestCaseData(new FileFormat.Jar.JarFormatDescriptor()).SetName("Jar");
    yield return new TestCaseData(new FileFormat.War.WarFormatDescriptor()).SetName("War");
    yield return new TestCaseData(new FileFormat.Ear.EarFormatDescriptor()).SetName("Ear");
    yield return new TestCaseData(new FileFormat.Apk.ApkFormatDescriptor()).SetName("Apk");
    yield return new TestCaseData(new FileFormat.Ipa.IpaFormatDescriptor()).SetName("Ipa");
    yield return new TestCaseData(new FileFormat.Xpi.XpiFormatDescriptor()).SetName("Xpi");
    yield return new TestCaseData(new FileFormat.Epub.EpubFormatDescriptor()).SetName("Epub");
    yield return new TestCaseData(new FileFormat.Odt.OdtFormatDescriptor()).SetName("Odt");
    yield return new TestCaseData(new FileFormat.Ods.OdsFormatDescriptor()).SetName("Ods");
    yield return new TestCaseData(new FileFormat.Odp.OdpFormatDescriptor()).SetName("Odp");
    yield return new TestCaseData(new FileFormat.Docx.DocxFormatDescriptor()).SetName("Docx");
    yield return new TestCaseData(new FileFormat.Xlsx.XlsxFormatDescriptor()).SetName("Xlsx");
    yield return new TestCaseData(new FileFormat.Pptx.PptxFormatDescriptor()).SetName("Pptx");
    yield return new TestCaseData(new FileFormat.Cbz.CbzFormatDescriptor()).SetName("Cbz");
    yield return new TestCaseData(new FileFormat.Maff.MaffFormatDescriptor()).SetName("Maff");
    yield return new TestCaseData(new FileFormat.Kmz.KmzFormatDescriptor()).SetName("Kmz");
    yield return new TestCaseData(new FileFormat.Appx.AppxFormatDescriptor()).SetName("Appx");
    yield return new TestCaseData(new FileFormat.NuPkg.NuPkgFormatDescriptor()).SetName("NuPkg");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(ZipFamilyDescriptors))]
  public void Descriptor_Implements_IArchiveModifiable(IFormatDescriptor descriptor) {
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>(),
      $"{descriptor.Id} must implement IArchiveModifiable to advertise an Add/Remove API");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(ZipFamilyDescriptors))]
  public void Descriptor_Advertises_CanModify(IFormatDescriptor descriptor) {
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      $"{descriptor.Id}.Capabilities must include CanModify");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(ZipFamilyDescriptors))]
  public void Add_PreservesPreCentralDirectoryBytes_ByteIdentical(IFormatDescriptor descriptor) {
    // Build a 2-entry ZIP. Two entries are enough to detect any rewriter that
    // shifts earlier LFHs — the second entry's LFH must stay at the same offset
    // as before Add was called.
    var archive = BuildTwoEntryZip();
    var beforeBytes = archive.ToArray();
    var oldCdStart = FindCentralDirectoryStart(beforeBytes);

    // Pre-CD region (everything from byte 0 up to but not including the first
    // central directory header signature `PK\x01\x02`).
    var preCdRegionBefore = beforeBytes[..(int)oldCdStart];

    // Invoke the descriptor's Add through the IArchiveModifiable contract so
    // we exercise the same code path callers see.
    var modifiable = (IArchiveModifiable)descriptor;
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "extra-payload-bytes-marker"u8.ToArray());
      modifiable.Add(archive, [new ArchiveInputInfo(tmp, "extra.txt", false)]);
    } finally {
      File.Delete(tmp);
    }

    var afterBytes = archive.ToArray();
    Assert.That(afterBytes.Length, Is.GreaterThan(beforeBytes.Length),
      $"{descriptor.Id}: Add should grow the archive");

    // The same pre-CD region in the post-Add stream must be byte-identical.
    // This proves the new LFH was appended at the old CD offset and the prior
    // entries' bytes were not re-encoded.
    Assert.That(afterBytes.AsSpan(0, preCdRegionBefore.Length).SequenceEqual(preCdRegionBefore), Is.True,
      $"{descriptor.Id}: bytes [0, {preCdRegionBefore.Length}) changed after Add — " +
      $"pre-existing local file headers + payloads must remain byte-identical");

    // Sanity: the new central directory should start at a higher offset than
    // the old one — proof that an LFH was inserted between them.
    var newCdStart = FindCentralDirectoryStart(afterBytes);
    Assert.That(newCdStart, Is.GreaterThan(oldCdStart),
      $"{descriptor.Id}: new CD start ({newCdStart}) should be past old CD start ({oldCdStart})");
  }

  [Test, Category("Contract"), TestCaseSource(nameof(ZipFamilyDescriptors))]
  public void Remove_PreservesKeptEntries_AndDropsNamedOne(IFormatDescriptor descriptor) {
    var archive = BuildTwoEntryZip();
    var modifiable = (IArchiveModifiable)descriptor;

    modifiable.Remove(archive, ["alpha.txt"]);

    archive.Position = 0;
    var reader = new ZipReader(archive);
    var names = reader.Entries.Select(e => e.FileName).ToHashSet();
    Assert.That(names, Does.Not.Contain("alpha.txt"), $"{descriptor.Id}: alpha.txt should be gone");
    Assert.That(names, Contains.Item("beta.bin"), $"{descriptor.Id}: beta.bin should survive");
  }

  // ── helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildTwoEntryZip() {
    var ms = new MemoryStream();
    using (var w = new ZipWriter(ms, leaveOpen: true)) {
      // Use Store so the marker bytes are visible at the raw stream level —
      // simplifies any future forensic inspection of the wiping behaviour.
      w.AddEntry("alpha.txt", "alpha-content-MARKER"u8.ToArray(), ZipCompressionMethod.Store);
      w.AddEntry("beta.bin", "beta-payload-MARKER"u8.ToArray(), ZipCompressionMethod.Store);
    }
    return ms;
  }

  /// <summary>
  /// Locates the first central directory file header signature (<c>PK\x01\x02</c>)
  /// by linear scan. Robust for tests because our seed archives contain no
  /// comments / data descriptors / archives with this byte pattern in payloads.
  /// </summary>
  private static long FindCentralDirectoryStart(byte[] bytes) {
    // PK\x01\x02 = 0x50 0x4B 0x01 0x02
    for (var i = 0; i <= bytes.Length - 4; i++) {
      if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02)
        return i;
    }
    throw new InvalidOperationException("Central directory signature not found in archive bytes.");
  }
}

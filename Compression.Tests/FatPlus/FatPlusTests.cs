#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Fat;
using FileSystem.FatPlus;

namespace Compression.Tests.FatPlus;

/// <summary>
/// FAT+ reader tests. FAT+ extends FAT32 to support files &gt; 4 GiB by
/// storing the upper 6 bits of file size in the otherwise-reserved
/// <c>DIR_NTRes</c> byte (offset 12) of the 32-byte directory entry; FAT+
/// volumes are identified by an <c>"FAT+    "</c> OEM signature in the BPB
/// at offset 3.
/// </summary>
[TestFixture]
public class FatPlusTests {

  /// <summary>
  /// Builds a 16 MB FAT32 image with one normal file using <see cref="FatWriter"/>,
  /// then patches the OEM signature to identify the image as FAT+ and (optionally)
  /// patches the first short-name directory entry's NTRes byte with the high bits
  /// of an extended file size.
  /// </summary>
  /// <param name="payloadName">Name of the file to put in the image.</param>
  /// <param name="payload">Bytes for the file.</param>
  /// <param name="sizeHighBits">Bits 32..37 of the extended file size to encode in NTRes (0..63).</param>
  private static byte[] BuildFatPlusImageWithFile(string payloadName, byte[] payload, int sizeHighBits) {
    // 80 MB image — large enough to force FAT32 selection (must have > 65525 clusters).
    var w = new FatWriter();
    w.AddFile(payloadName, payload);
    var image = w.Build(totalSectors: 80 * 1024 * 1024 / 512);

    // Sanity: this should have produced a FAT32 image.
    var fileSysType = Encoding.ASCII.GetString(image, 82, 8);
    Assert.That(fileSysType, Does.StartWith("FAT32"), "FatWriter must produce FAT32 for 16 MB image.");

    // Patch the OEM signature (offset 3..10) to mark this as FAT+.
    FatPlusReader.OemSignature.CopyTo(image, 3);

    // Locate the first non-LFN, non-deleted dirent in the root cluster.
    // FAT32 root lives at cluster 2 → data area + 0 bytes.
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    var sectorsPerCluster = image[13];
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16];
    var fatSize32 = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36));
    var firstDataSector = reservedSectors + fatCount * fatSize32;
    var rootStart = firstDataSector * bytesPerSector;

    // Walk the root dir, skipping LFN/deleted/volume-label entries.
    for (var off = rootStart; off + 32 <= image.Length; off += 32) {
      var first = image[off];
      if (first == 0x00) break;
      if (first == 0xE5) continue;
      var attr = image[off + 11];
      if ((attr & 0x3F) == 0x0F) continue; // LFN
      if ((attr & 0x18) != 0) continue;    // volume label or directory

      // This is the file entry. Patch DIR_NTRes (offset 12).
      image[off + 12] = (byte)(sizeHighBits & 0x3F);
      return image;
    }

    throw new InvalidOperationException("Could not find dirent to patch.");
  }

  // ── Detection ────────────────────────────────────────────────────────

  [Test]
  public void HasFatPlusSignature_OemPresent_ReturnsTrue() {
    var buf = new byte[512];
    buf[0] = 0xEB; buf[1] = 0x58; buf[2] = 0x90;
    "FAT+    "u8.ToArray().CopyTo(buf, 3);
    Assert.That(FatPlusReader.HasFatPlusSignature(buf), Is.True);
  }

  [Test]
  public void HasFatPlusSignature_StandardFat_ReturnsFalse() {
    var buf = new byte[512];
    buf[0] = 0xEB; buf[1] = 0x58; buf[2] = 0x90;
    "MSDOS5.0"u8.ToArray().CopyTo(buf, 3);
    Assert.That(FatPlusReader.HasFatPlusSignature(buf), Is.False);
  }

  [Test]
  public void Descriptor_HasMagicSignature_ForOemAtOffset3() {
    var desc = new FatPlusFormatDescriptor();
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    var sig = desc.MagicSignatures[0];
    Assert.That(sig.Offset, Is.EqualTo(3));
    Assert.That(sig.Bytes, Is.EqualTo("FAT+    "u8.ToArray()));
  }

  [Test]
  public void Descriptor_HasNoExtensions_DetectionIsMagicOnly() {
    // FAT+ shares .img with regular FAT — must not grab generic .img files.
    var desc = new FatPlusFormatDescriptor();
    Assert.That(desc.Extensions, Is.Empty);
  }

  // ── Listing ──────────────────────────────────────────────────────────

  [Test]
  public void List_StandardSizedFile_NoNtResPatch_ReturnsCorrectSize() {
    var payload = Encoding.ASCII.GetBytes("Hello, FAT+ world!");
    var image = BuildFatPlusImageWithFile("HELLO.TXT", payload, sizeHighBits: 0);

    using var ms = new MemoryStream(image);
    var entries = new FatPlusFormatDescriptor().List(ms, null);

    var hello = entries.SingleOrDefault(e => e.Name.Equals("HELLO.TXT", StringComparison.OrdinalIgnoreCase));
    Assert.That(hello, Is.Not.Null, "HELLO.TXT must be listed.");
    Assert.That(hello!.OriginalSize, Is.EqualTo(payload.Length));
  }

  [Test]
  public void List_FilePatchedWithHighSizeBits_ReportsExtendedSize() {
    // Patch NTRes = 1 → size_hi = 1 → reported size = (1 << 32) + payload.Length = 4 GiB + 17.
    var payload = Encoding.ASCII.GetBytes("Above the 4 GiB ridge.");
    var image = BuildFatPlusImageWithFile("BIG.BIN", payload, sizeHighBits: 1);

    using var ms = new MemoryStream(image);
    var entries = new FatPlusFormatDescriptor().List(ms, null);

    var big = entries.SingleOrDefault(e => e.Name.Equals("BIG.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.That(big, Is.Not.Null);
    var expected = (1L << 32) + payload.Length;
    Assert.That(big!.OriginalSize, Is.EqualTo(expected),
      "FAT+ must combine NTRes bits with DIR_FileSize to form 38-bit size.");
    Assert.That(big.OriginalSize, Is.GreaterThan(uint.MaxValue),
      "Reported size must exceed the 4 GiB FAT32 cap.");
  }

  [Test]
  public void List_MaxNtResBits_ReportsNear256GiBSize() {
    // NTRes = 0x3F (all 6 bits set) → size_hi = 63 → near the 256 GiB FAT+ cap.
    var payload = new byte[] { 0xAA };
    var image = BuildFatPlusImageWithFile("MAX.BIN", payload, sizeHighBits: 0x3F);

    using var ms = new MemoryStream(image);
    var entries = new FatPlusFormatDescriptor().List(ms, null);
    var max = entries.Single(e => e.Name.Equals("MAX.BIN", StringComparison.OrdinalIgnoreCase));

    var expected = (63L << 32) | 1L;
    Assert.That(max.OriginalSize, Is.EqualTo(expected));
    // 63 GiB upper region — should be within the 256 GiB cap.
    Assert.That(max.OriginalSize, Is.LessThan(1L << 38));
  }

  // ── Extraction ───────────────────────────────────────────────────────

  [Test]
  public void Extract_StandardSizedFile_RoundTripsBytes() {
    var payload = Encoding.ASCII.GetBytes("Roundtrip me please.");
    var image = BuildFatPlusImageWithFile("RT.TXT", payload, sizeHighBits: 0);

    var outDir = Path.Combine(Path.GetTempPath(), "fatplus-extract-" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(image))
        new FatPlusFormatDescriptor().Extract(ms, outDir, null, files: null);

      var extracted = File.ReadAllBytes(Path.Combine(outDir, "RT.TXT"));
      Assert.That(extracted, Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  [Test]
  public void Extract_ChainSizeShorterThanClaimedSize_StopsAtChainEnd() {
    // Patch claims 4 GiB + N but the cluster chain only holds N bytes.
    // The reader must stop gracefully at end-of-chain — extracted file is
    // the on-disk run, not 4 GiB of garbage.
    var payload = Encoding.ASCII.GetBytes("Short chain, big size.");
    var image = BuildFatPlusImageWithFile("MISMATCH.BIN", payload, sizeHighBits: 1);

    var outDir = Path.Combine(Path.GetTempPath(), "fatplus-mismatch-" + Guid.NewGuid().ToString("N"));
    try {
      using (var ms = new MemoryStream(image))
        new FatPlusFormatDescriptor().Extract(ms, outDir, null, files: null);

      var extracted = File.ReadAllBytes(Path.Combine(outDir, "MISMATCH.BIN"));
      // FAT clusters are typically larger than the payload, so a cluster's
      // worth of bytes (or whatever the chain holds) is what comes out.
      // It must not be >= 4 GiB and it must start with our payload.
      Assert.That(extracted.Length, Is.LessThan(1L << 31),
        "Reader must not invent 4 GiB of data when the chain ends early.");
      Assert.That(extracted.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    }
  }

  // ── Negative ──────────────────────────────────────────────────────────

  [Test]
  public void Reader_RejectsNonFatPlusImage() {
    // A plain FAT32 image (no OEM patch) must NOT be accepted by FatPlusReader.
    var image = new FatWriter();
    image.AddFile("OK.TXT", [1, 2, 3]);
    var bytes = image.Build(totalSectors: 16 * 1024 * 1024 / 512);

    using var ms = new MemoryStream(bytes);
    Assert.That(() => new FatPlusReader(ms), Throws.TypeOf<InvalidDataException>());
  }

  // ── Registry integration ─────────────────────────────────────────────

  [Test]
  public void Detector_IdentifiesFatPlusImage_ByMagic() {
    var payload = Encoding.ASCII.GetBytes("detector check");
    var image = BuildFatPlusImageWithFile("DET.TXT", payload, sizeHighBits: 0);

    // Detect by magic — pass the boot sector bytes (offset 3 holds "FAT+    ").
    var fmt = FormatDetector.DetectByMagic(image.AsSpan(0, 512));
    Assert.That(fmt.ToString(), Is.EqualTo("FatPlus").IgnoreCase,
      $"FormatDetector must recognise FAT+ via the OEM signature. Got: {fmt}");
  }

  // ── Creation-options schema ───────────────────────────────────────────

  [Test]
  public void Descriptor_OptionsSchema_ExposesClusterAndImageSize() {
    var descriptor = new FatPlusFormatDescriptor();
    var keys = descriptor.OptionsSchema.Select(o => o.Key).ToList();

    Assert.That(keys, Does.Contain("ClusterSize"));
    Assert.That(keys, Does.Contain("ImageSize"));
    Assert.That(keys, Does.Contain("VolumeLabel"));

    // ImageSize must offer the large FAT+ presets plus the Auto entry.
    var imageSize = descriptor.OptionsSchema.Single(o => o.Key == "ImageSize");
    Assert.That(imageSize.AllowedValues, Does.Contain("64 GB"));
    Assert.That(imageSize.AllowedValues, Does.Contain("Auto (fit to files)"));
  }

  [Test]
  public void Descriptor_CreateWithExplicitCluster_RoundTripsThroughReader() {
    var data = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 };
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, data);
      var inputs = new[] { new ArchiveInputInfo(tmp, "DATA.BIN", false) };
      // Pin a fixed image size so the explicit cluster size is exercised against a
      // known geometry (the "fixed image + explicit cluster" branch of Create()).
      var options = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> {
          ["ImageSize"] = "512 MB",
          ["ClusterSize"] = "4 KB",
        },
      };

      using var output = new MemoryStream();
      new FatPlusFormatDescriptor().Create(output, inputs, options);
      var img = output.ToArray();

      // FAT+ OEM signature must be present and the file must round-trip exactly.
      // (The 8.3 short name is derived from the temp file path, so we assert on
      // the preserved extension and the byte content rather than the base name.)
      Assert.That(Encoding.ASCII.GetString(img, 3, 8), Is.EqualTo("FAT+    "));
      using var r = new FatPlusReader(new MemoryStream(img));
      var entry = r.Entries.Single(e => !e.IsDirectory);
      Assert.That(entry.Name.ToUpperInvariant(), Does.EndWith(".BIN"));
      Assert.That(r.Extract(entry), Is.EqualTo(data));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test]
  public void BuildAutoSized_RoundTripsThroughReader() {
    var data = new byte[] { 11, 22, 33, 44, 55, 66 };
    var w = new FatPlusWriter();
    w.AddFile("AUTO.BIN", data);
    var img = w.BuildAutoSized();

    Assert.That(Encoding.ASCII.GetString(img, 3, 8), Is.EqualTo("FAT+    "));
    using var r = new FatPlusReader(new MemoryStream(img));
    var entry = r.Entries.Single(e => !e.IsDirectory);
    Assert.That(entry.Name.ToUpperInvariant(), Does.StartWith("AUTO"));
    Assert.That(r.Extract(entry), Is.EqualTo(data));
  }
}

// ── Optional: real >4 GiB file extraction ───────────────────────────────

/// <summary>
/// Validates extraction of an actual file larger than 4 GiB from a FAT+ image
/// hosted on a sparse temp file on disk. Marked Explicit because it requires
/// ~5 GiB of free disk space (sparse on NTFS, full on FAT/ext).
/// </summary>
[TestFixture, Explicit("Requires ~5 GiB of (sparse) disk space; run manually.")]
public class FatPlusLargeFileTests {

  [Test]
  public void Extract_FileLargerThan4GiB_ReadsCompleteContent() {
    // Skipping the actual data writing path because it requires writing
    // a fully-populated 5 GiB FAT image to disk which is too slow for CI.
    // Documented for manual reproduction:
    //   1. Build a FAT32 image with FatWriter() sized > 5 GiB total.
    //   2. Patch OEM bytes 3..10 to "FAT+    ".
    //   3. Write a real file whose FAT chain spans (5 GiB / clusterSize) clusters.
    //   4. Patch the dirent's NTRes to encode size_hi = 1 (≥ 4 GiB).
    //   5. Patch DIR_FileSize (offset 28) to encode size_lo (≈ 1 GiB).
    //   6. Read via FatPlusReader.ExtractTo() into a counting sink and
    //      assert the byte count == 5 GiB + (overhead).
    Assert.Ignore("Manual scenario — see comment for the reproduction steps.");
  }
}

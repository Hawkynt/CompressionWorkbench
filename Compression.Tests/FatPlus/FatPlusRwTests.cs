#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.FatPlus;

namespace Compression.Tests.FatPlus;

/// <summary>
/// Read/write tests for the FAT+ filesystem implementation
/// (<see cref="FatPlusWriter"/>, <see cref="FatPlusModifier"/>, and the
/// Create/Add/Remove/Defragment paths of <see cref="FatPlusFormatDescriptor"/>).
/// Tests use small synthetic images and the optional <c>extendedSize</c>
/// parameter on <see cref="FatPlusWriter.AddFile"/> to exercise the 38-bit
/// FAT+ size encoding without writing actual &gt;4 GiB payloads.
/// </summary>
[TestFixture]
public class FatPlusRwTests {

  // ── FatPlusWriter direct tests ──────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Writer_StampsFatPlusOemSignature() {
    var w = new FatPlusWriter();
    w.AddFile("HELLO.TXT", "hi"u8.ToArray());
    var image = w.Build();
    var oem = image.AsSpan(3, 8).ToArray();
    Assert.That(oem, Is.EqualTo(FatPlusReader.OemSignature),
      "FAT+ writer must place 'FAT+    ' at offset 3 of the BPB for detection.");
  }

  [Test, Category("RoundTrip")]
  public void Writer_RoundTripsSmallFile_PreservesDataAndSize() {
    var payload = Encoding.ASCII.GetBytes("Round-trip me through FAT+.");
    var w = new FatPlusWriter();
    w.AddFile("RT.TXT", payload);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new FatPlusReader(ms, leaveOpen: true);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("RT.TXT"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Writer_EncodesExtendedSizeAbove4GiB_RoundTripsDeclaredSize() {
    // We hand the writer a tiny payload but declare a > 4 GiB extended size.
    // The reader (and List) must see the declared size, proving the 38-bit
    // encoding (NTRes high 6 bits + DIR_FileSize low 32 bits) round-trips.
    var payload = Encoding.ASCII.GetBytes("Tiny on disk, huge in dirent.");
    const long declared = (1L << 32) + 12345; // 4 GiB + 12345
    var w = new FatPlusWriter();
    w.AddFile("HUGE.BIN", payload, extendedSize: declared);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new FatPlusReader(ms, leaveOpen: true);
    var entry = r.Entries.Single();
    Assert.That(entry.Size, Is.EqualTo(declared),
      "FAT+ writer must encode the 38-bit extended size verbatim.");
    Assert.That(entry.Size, Is.GreaterThan(uint.MaxValue),
      "Encoded size must exceed FAT32's 4 GiB cap.");
  }

  [Test, Category("RoundTrip")]
  public void Writer_EncodesMaxExtendedSize_BoundaryCheck() {
    // (1 << 38) - 1 = 256 GiB - 1 = the documented FAT+ ceiling.
    const long max = (1L << 38) - 1;
    var w = new FatPlusWriter();
    w.AddFile("MAX.BIN", [0xAA], extendedSize: max);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new FatPlusReader(ms, leaveOpen: true);
    Assert.That(r.Entries.Single().Size, Is.EqualTo(max));
  }

  [Test, Category("Spec")]
  public void Writer_PreservesTopTwoBitsOfNtRes() {
    // Top 2 bits of NTRes are reserved for Windows NT case-flag use.
    // FAT+ must not stomp on them — verify they stay clear (writer default)
    // even when the high 6 bits are all set.
    var w = new FatPlusWriter();
    w.AddFile("CASE.TXT", [0x01], extendedSize: ((long)0x3F << 32) | 1);
    var image = w.Build();

    var rootStart = LocateRootStart(image);
    var entryOffset = WalkToFirstShortEntry(image, rootStart);
    var ntRes = image[entryOffset + 12];
    Assert.That(ntRes & 0xC0, Is.EqualTo(0),
      "FAT+ must leave the top 2 bits of NTRes clear; they're reserved for NT.");
    Assert.That(ntRes & 0x3F, Is.EqualTo(0x3F),
      "FAT+ must place the high 6 bits of extended size in the low 6 bits of NTRes.");
  }

  [Test]
  public void Writer_RejectsSizeAbove256GiB() {
    var w = new FatPlusWriter();
    Assert.That(() => w.AddFile("OOB.BIN", [0x01], extendedSize: 1L << 38),
      Throws.TypeOf<ArgumentOutOfRangeException>());
  }

  [Test, Category("RoundTrip")]
  public void Writer_MultipleFiles_AllSizesAreEncoded() {
    var w = new FatPlusWriter();
    w.AddFile("A.TXT", [1, 2, 3]);
    w.AddFile("B.TXT", "hello"u8.ToArray(), extendedSize: (1L << 33) + 7);
    w.AddFile("C.TXT", [9]);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new FatPlusReader(ms, leaveOpen: true);
    var byName = r.Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    Assert.That(byName["A.TXT"].Size, Is.EqualTo(3));
    Assert.That(byName["B.TXT"].Size, Is.EqualTo((1L << 33) + 7));
    Assert.That(byName["C.TXT"].Size, Is.EqualTo(1));
  }

  // ── FatPlusFormatDescriptor.Create / Add / Remove ────────────────────────

  [Test, Category("Descriptor")]
  public void Descriptor_Create_RoundTripsViaList() {
    var desc = new FatPlusFormatDescriptor();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "create-me"u8.ToArray());
      using var ms = new MemoryStream();
      ((IArchiveCreatable)desc).Create(ms, [new ArchiveInputInfo(tmp, "FILE.TXT", false)],
        new FormatCreateOptions());
      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("FILE.TXT"));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Descriptor")]
  public void Descriptor_Add_AppendsFile_PreservesExisting() {
    // Build an initial image with one file, then Add() a second.
    var desc = new FatPlusFormatDescriptor();
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "first"u8.ToArray());
      File.WriteAllBytes(tmp2, "second"u8.ToArray());

      // Use a backing FileStream (read+write) so Add can patch in place.
      var imageFile = Path.GetTempFileName();
      try {
        using (var fs = File.Open(imageFile, FileMode.Create, FileAccess.ReadWrite)) {
          ((IArchiveCreatable)desc).Create(fs, [new ArchiveInputInfo(tmp1, "FIRST.TXT", false)],
            new FormatCreateOptions());
        }
        using (var fs = File.Open(imageFile, FileMode.Open, FileAccess.ReadWrite)) {
          ((IArchiveModifiable)desc).Add(fs, [new ArchiveInputInfo(tmp2, "SECOND.TXT", false)]);
        }
        using (var fs = File.OpenRead(imageFile)) {
          var entries = desc.List(fs, null);
          var names = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
          Assert.That(names.Contains("FIRST.TXT"), Is.True);
          Assert.That(names.Contains("SECOND.TXT"), Is.True);
        }
      } finally {
        File.Delete(imageFile);
      }
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
    }
  }

  [Test, Category("Descriptor")]
  public void Descriptor_Add_PreservesExtendedSizesOfExistingEntries() {
    // Build an image with a > 4 GiB declared file via FatPlusWriter directly.
    var w = new FatPlusWriter();
    var bigPayload = new byte[64];
    Array.Fill(bigPayload, (byte)0x77);
    const long declaredBig = (1L << 33) + 4242; // 8 GiB + 4242
    w.AddFile("BIG.DAT", bigPayload, extendedSize: declaredBig);
    var image = w.Build();

    var imageFile = Path.GetTempFileName();
    var inputFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(imageFile, image);
      File.WriteAllBytes(inputFile, "added"u8.ToArray());

      var desc = new FatPlusFormatDescriptor();
      using (var fs = File.Open(imageFile, FileMode.Open, FileAccess.ReadWrite)) {
        ((IArchiveModifiable)desc).Add(fs, [new ArchiveInputInfo(inputFile, "ADDED.TXT", false)]);
      }

      using var verify = File.OpenRead(imageFile);
      var entries = desc.List(verify, null);
      var big = entries.Single(e => e.Name.Equals("BIG.DAT", StringComparison.OrdinalIgnoreCase));
      Assert.That(big.OriginalSize, Is.EqualTo(declaredBig),
        "FAT+ Add must preserve the existing > 4 GiB declared size.");
    } finally {
      File.Delete(imageFile);
      File.Delete(inputFile);
    }
  }

  [Test, Category("Descriptor")]
  public void Descriptor_Remove_DeletesFile_AndPreservesOemSignature() {
    var desc = new FatPlusFormatDescriptor();
    var tmp1 = Path.GetTempFileName();
    var tmp2 = Path.GetTempFileName();
    var imageFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp1, "alpha"u8.ToArray());
      File.WriteAllBytes(tmp2, "bravo"u8.ToArray());

      using (var fs = File.Open(imageFile, FileMode.Create, FileAccess.ReadWrite)) {
        ((IArchiveCreatable)desc).Create(fs, [
          new ArchiveInputInfo(tmp1, "ALPHA.TXT", false),
          new ArchiveInputInfo(tmp2, "BRAVO.TXT", false),
        ], new FormatCreateOptions());
      }

      using (var fs = File.Open(imageFile, FileMode.Open, FileAccess.ReadWrite)) {
        ((IArchiveModifiable)desc).Remove(fs, ["ALPHA.TXT"]);
      }

      using var verify = File.OpenRead(imageFile);
      var entries = desc.List(verify, null);
      var names = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
      Assert.That(names.Contains("ALPHA.TXT"), Is.False, "Removed file must be gone.");
      Assert.That(names.Contains("BRAVO.TXT"), Is.True, "Other files must survive.");

      // OEM signature must still mark this as FAT+ after Remove.
      verify.Position = 0;
      Span<byte> bpb = stackalloc byte[512];
      verify.ReadExactly(bpb);
      Assert.That(FatPlusReader.HasFatPlusSignature(bpb), Is.True,
        "FAT+ OEM signature must survive Remove operation.");
    } finally {
      File.Delete(tmp1);
      File.Delete(tmp2);
      File.Delete(imageFile);
    }
  }

  [Test, Category("Descriptor")]
  public void Descriptor_Remove_NonExistentFile_Throws() {
    var desc = new FatPlusFormatDescriptor();
    var w = new FatPlusWriter();
    w.AddFile("KEEP.TXT", [1, 2, 3]);
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    Assert.That(() => ((IArchiveModifiable)desc).Remove(ms, ["GHOST.TXT"]),
      Throws.TypeOf<FileNotFoundException>());
  }

  // ── Defragment ──────────────────────────────────────────────────────────

  [Test, Category("Defrag")]
  public void Descriptor_Defragment_PreservesAllFilesAndExtendedSizes() {
    // Construct a multi-file FAT+ image where one entry declares a >4 GiB size.
    // After defrag, every file's name + declared size must round-trip.
    var w = new FatPlusWriter();
    w.AddFile("ONE.TXT", "one"u8.ToArray());
    w.AddFile("TWO.BIN", new byte[256], extendedSize: (1L << 33) | 999);
    w.AddFile("THR.DAT", new byte[100]);
    var image = w.Build();

    var imageFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(imageFile, image);

      var desc = new FatPlusFormatDescriptor();
      using (var fs = File.Open(imageFile, FileMode.Open, FileAccess.ReadWrite)) {
        ((IArchiveDefragmentable)desc).Defragment(fs);
      }

      using var verify = File.OpenRead(imageFile);
      var entries = desc.List(verify, null);
      Assert.That(entries, Has.Count.EqualTo(3));
      var byName = entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
      Assert.That(byName["ONE.TXT"].OriginalSize, Is.EqualTo(3));
      Assert.That(byName["TWO.BIN"].OriginalSize, Is.EqualTo((1L << 33) | 999),
        "Defrag must preserve > 4 GiB declared size.");
      Assert.That(byName["THR.DAT"].OriginalSize, Is.EqualTo(100));

      // OEM signature must still mark this as FAT+ after Defragment.
      verify.Position = 0;
      Span<byte> bpb = stackalloc byte[512];
      verify.ReadExactly(bpb);
      Assert.That(FatPlusReader.HasFatPlusSignature(bpb), Is.True,
        "FAT+ OEM signature must survive Defragment.");
    } finally {
      File.Delete(imageFile);
    }
  }

  [Test, Category("Defrag")]
  public void Descriptor_Defragment_ConsolidateAtEnd_AllFilesSurvive() {
    var w = new FatPlusWriter();
    w.AddFile("SMALL.TXT", "x"u8.ToArray());
    w.AddFile("LARGE.BIN", new byte[2000]);
    w.AddFile("MED.DAT", new byte[500]);
    var image = w.Build();

    var imageFile = Path.GetTempFileName();
    try {
      File.WriteAllBytes(imageFile, image);

      var desc = new FatPlusFormatDescriptor();
      using (var fs = File.Open(imageFile, FileMode.Open, FileAccess.ReadWrite)) {
        ((IArchiveDefragmentable)desc).Defragment(fs,
          new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });
      }

      using var verify = File.OpenRead(imageFile);
      var entries = desc.List(verify, null);
      Assert.That(entries.Select(e => e.Name).ToHashSet(),
        Is.EquivalentTo(new[] { "SMALL.TXT", "LARGE.BIN", "MED.DAT" }));
    } finally {
      File.Delete(imageFile);
    }
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Computes the byte offset of the FAT32 root-directory cluster in a freshly
  /// built FAT+ image.
  /// </summary>
  private static int LocateRootStart(byte[] image) {
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    var reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(14));
    var fatCount = image[16];
    var fatSize32 = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(36));
    var firstDataSector = reservedSectors + fatCount * fatSize32;
    return firstDataSector * bytesPerSector;
  }

  /// <summary>
  /// Walks the root-directory area until it finds the first short-name
  /// (non-LFN, non-deleted, non-volume-label, non-directory) dirent and
  /// returns its byte offset.
  /// </summary>
  private static int WalkToFirstShortEntry(byte[] image, int rootStart) {
    for (var off = rootStart; off + 32 <= image.Length; off += 32) {
      var first = image[off];
      if (first == 0x00) break;
      if (first == 0xE5) continue;
      var attr = image[off + 11];
      if ((attr & 0x3F) == 0x0F) continue;
      if ((attr & 0x18) != 0) continue;
      return off;
    }
    throw new InvalidOperationException("No short-name dirent found in root.");
  }
}

using System.Buffers.Binary;
using FileFormat.Zoom;

namespace Compression.Tests.Zoom;

[TestFixture]
public class ZoomTests {
  // ── Builders: a "ZOM5" archive whose records store their payload unpacked ─────

  private sealed record Record(byte[] Cylinders, uint[] Masks, byte[] Payload, ushort Middle = 0, ushort Flag = 0);

  private static byte[] Build(byte first, byte last, uint flags, params Record[] records) {
    var header = new byte[72];
    "ZOM5"u8.CopyTo(header);
    header[4] = first;
    header[5] = last;
    header[6] = 5;
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), flags);
    using var ms = new MemoryStream();
    ms.Write(header);
    WriteBe32(ms, ZoomReader.Crc32Zoom(header));
    foreach (var r in records) {
      var rec = new byte[38];
      for (var i = 0; i < 5; ++i) rec[i] = i < r.Cylinders.Length ? r.Cylinders[i] : (byte)0xFF;
      for (var i = 0; i < 5; ++i) BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(6 + 4 * i), i < r.Masks.Length ? r.Masks[i] : 0);
      BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(26), (ushort)r.Payload.Length);
      BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(28), r.Middle);
      BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(32), r.Flag);
      BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(34), ZoomReader.Crc32Zoom(r.Payload));
      ms.Write(rec);
      WriteBe32(ms, ZoomReader.Crc32Zoom(rec));
      ms.Write(r.Payload);
    }
    return ms.ToArray();
  }

  private static void WriteBe32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(b, v);
    s.Write(b);
  }

  private static byte[] Sectors(int count, byte seed) {
    var data = new byte[count * 512];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(seed + i / 512);
    return data;
  }

  // ── Synthetic layouts ─────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void StoredSectors_LandInPlace_AndHolesStayZero() {
    // Cylinder 0: sectors 0 and 2; cylinder 3: sector 21. Cylinders 1, 2 and 4..79 are never stored.
    var payload = Sectors(3, 0x40);
    var archive = Build(0, 79, 0, new Record([0, 3], [0b101u, 1u << 21], payload));
    var image = new ZoomReader(new MemoryStream(archive)).ExtractDisk();

    Assert.That(image, Has.Length.EqualTo(901120));
    Assert.That(image.AsSpan(0, 512).ToArray(), Is.All.EqualTo(0x40));
    Assert.That(image.AsSpan(512, 512).ToArray(), Is.All.EqualTo(0), "sector 1 was not stored");
    Assert.That(image.AsSpan(1024, 512).ToArray(), Is.All.EqualTo(0x41));
    Assert.That(image.AsSpan(3 * 11264 + 21 * 512, 512).ToArray(), Is.All.EqualTo(0x42));
    Assert.That(image.AsSpan(11264, 2 * 11264).ToArray(), Is.All.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void RunLengthStage_EscapeRules() {
    // 512 bytes: "A", escape literal, 510 × 'B'. Escape 0x99: 99 00 = literal 0x99; 99 n b = n+1 copies.
    byte[] rle = [0x00, 0x02, 0x00, 0x99, (byte)'A', 0x99, 0x00, 0x99, 0xFD, (byte)'B', 0x99, 0xFF, (byte)'B'];
    var archive = Build(0, 0, 0, new Record([0], [1u], rle, Middle: (ushort)rle.Length));
    var image = new ZoomReader(new MemoryStream(archive)).ExtractDisk();
    Assert.That(image[0], Is.EqualTo((byte)'A'));
    Assert.That(image[1], Is.EqualTo(0x99));
    Assert.That(image.AsSpan(2, 510).ToArray(), Is.All.EqualTo((byte)'B'));
  }

  [Test, Category("Exception")]
  public void RunLengthStage_WrongDeclaredLength_Throws() {
    byte[] rle = [0x00, 0x01, 0xFF, 0x99, 0x99, 0xFF, 0x00];
    var archive = Build(0, 0, 0, new Record([0], [1u], rle, Middle: (ushort)rle.Length));
    Assert.Throws<InvalidDataException>(() => new ZoomReader(new MemoryStream(archive)).ExtractDisk());
  }

  [Test, Category("Exception")]
  public void HeaderCheck_Mismatch_Throws() {
    var archive = Build(0, 0, 0, new Record([0], [1u], Sectors(1, 1)));
    archive[0x10] ^= 1;
    Assert.Throws<InvalidDataException>(() => _ = new ZoomReader(new MemoryStream(archive)));
  }

  [Test, Category("Exception")]
  public void PayloadCheck_Mismatch_Throws() {
    var archive = Build(0, 0, 0, new Record([0], [1u], Sectors(1, 1)));
    archive[^1] ^= 1;
    Assert.Throws<InvalidDataException>(() => new ZoomReader(new MemoryStream(archive)).ExtractDisk());
  }

  [Test, Category("Exception")]
  public void Encrypted_IsRefused() {
    var archive = Build(0, 0, 0, new Record([0], [1u], Sectors(1, 1)));
    archive[0x24] = 1;
    BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(72), ZoomReader.Crc32Zoom(archive.AsSpan(0, 72)));
    var r = new ZoomReader(new MemoryStream(archive));
    Assert.That(r.IsEncrypted, Is.True);
    Assert.Throws<NotSupportedException>(() => r.ExtractDisk());
  }

  [Test, Category("Boundary")]
  public void SectorLabels_PartialCylinder_IsRefused() {
    var archive = Build(0, 0, 1, new Record([0], [1u], new byte[528]));
    Assert.Throws<InvalidDataException>(() => new ZoomReader(new MemoryStream(archive)).ExtractDisk());
  }

  [Test, Category("Exception")]
  [TestCase(new byte[] { (byte)'Z', (byte)'O', (byte)'O', 0xDC, 0xA7, 0xC4, 0xFD })]
  [TestCase(new byte[0])]
  public void NotZoom_Throws_ButListFallsBack(byte[] data) {
    Assert.Throws<InvalidDataException>(() => _ = new ZoomReader(new MemoryStream(data)));
    var list = new ZoomFormatDescriptor().List(new MemoryStream(data), null);
    Assert.That(list.Single().Name, Is.EqualTo("FULL.zom"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListsTheDiskImage() {
    var archive = Build(0, 1, 0, new Record([0, 1], [1u, 1u], Sectors(2, 7)));
    var list = new ZoomFormatDescriptor().List(new MemoryStream(archive), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "disk.adf" }));
    Assert.That(list[0].OriginalSize, Is.EqualTo(901120));
  }

  [Test, Category("HappyPath")]
  [TestCase("ZOM5")]
  [TestCase("ZOOM")]
  public void Magic_IsDetectedAsZoom_NotZoo(string magic) {
    var header = new byte[64];
    System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(header, 0);
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(header).ToString(), Is.EqualTo("Zoom"));
  }

  // ── Real archives (gated: test-corpus/zoom, not committed) ──────────────────

  private static uint BlockSum(byte[] image, int block) {
    var sum = 0u;
    for (var i = 0; i < 512; i += 4) sum += BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(block * 512 + i));
    return sum;
  }

  /// <summary>
  /// Real Zoom archives from public-domain CD collections (Fish, Kickpack, Saar PD, magazine cover
  /// disks; fetched from discmaster.textfiles.com, "format=zoomDiskImage"). Every header, record and
  /// payload check must pass, and every AmigaDOS volume must come out with an intact root block and
  /// file headers whose sizes our ADF reader reproduces.
  /// </summary>
  [Test, Category("RealFiles")]
  public void RealArchives_InTestCorpus_DecodeToIntactVolumes() {
    var corpus = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "zoom"));
    if (!Directory.Exists(corpus) || !Directory.EnumerateFiles(corpus, "*.zom").Any())
      Assert.Ignore($"No real .zom files under {corpus}.");

    var volumes = 0;
    foreach (var file in Directory.EnumerateFiles(corpus, "*.zom").Order()) {
      var image = new ZoomReader(File.OpenRead(file)).ExtractDisk();
      Assert.That(image, Has.Length.EqualTo(901120), file);
      // A track-loader disk carries no AmigaDOS root; only volumes are walked.
      if (BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(880 * 512)) != 2 || BlockSum(image, 880) != 0)
        continue;
      using var adf = new FileSystem.Adf.AdfReader(new MemoryStream(image));
      foreach (var entry in adf.Entries) {
        Assert.That(BlockSum(image, entry.HeaderBlock), Is.Zero, $"{Path.GetFileName(file)}: header of {entry.FullPath}");
        if (!entry.IsDirectory)
          Assert.That(adf.Extract(entry), Has.Length.EqualTo(entry.Size), $"{Path.GetFileName(file)}: {entry.FullPath}");
      }
      ++volumes;
    }
    Assert.That(volumes, Is.GreaterThan(0));
  }
}

using System.Text;

namespace Compression.Tests.VppV2;

[TestFixture]
public class VppV2Tests {

  // 0x51890ACE little-endian on disk = CE 0A 89 51.
  private static readonly byte[] MagicBytes = [0xCE, 0x0A, 0x89, 0x51];
  private const int SectionAlignment = 0x800;
  private const int DataAlignment    = 0x10;
  private const int HeaderStructSize = 392;
  // Header field offsets — must match VppV2Constants.
  private const int HeaderSizeFieldOffset     = 8 + 256 + 96; // 360
  private const int FileCountFieldOffset      = HeaderSizeFieldOffset + 4;
  private const int ArchiveSizeFieldOffset    = HeaderSizeFieldOffset + 8;
  private const int TocSizeFieldOffset        = HeaderSizeFieldOffset + 12;
  private const int NameTableSizeFieldOffset  = HeaderSizeFieldOffset + 16;
  private const int DataSizeFieldOffset       = HeaderSizeFieldOffset + 20;
  private const int CompressedSizeFieldOffset = HeaderSizeFieldOffset + 24;
  private const int FlagsFieldOffset          = HeaderSizeFieldOffset + 28;

  // ──────────────────────────── Magic / sanity ────────────────────────────

  [Test, Category("HappyPath")]
  public void Magic_LittleEndianBytes() {
    const uint expectedMagic = 0x51890ACEu;
    var raw = BitConverter.GetBytes(expectedMagic);
    Assert.That(raw, Is.EqualTo(MagicBytes));
  }

  // ──────────────────────────── Round-trip: stored ────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleStoredFile() {
    var data = "incompressible-tiny"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.VppV2.VppV2Writer(ms, leaveOpen: true))
      w.AddEntry("notes.txt", data);
    ms.Position = 0;

    var r = new FileFormat.VppV2.VppV2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("notes.txt"));
    Assert.That(r.Entries[0].DataSize, Is.EqualTo(data.Length));
    Assert.That(r.Entries[0].IsCompressed, Is.False, "tiny non-redundant data should not compress");
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  // ──────────────────────────── Round-trip: zlib ────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleCompressedFile() {
    var data = new byte[64 * 1024];
    // All-zero buffer compresses extremely well via zlib/deflate.

    using var ms = new MemoryStream();
    using (var w = new FileFormat.VppV2.VppV2Writer(ms, leaveOpen: true))
      w.AddEntry("zeros.bin", data);
    ms.Position = 0;

    var r = new FileFormat.VppV2.VppV2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].DataSize, Is.EqualTo(data.Length));
    Assert.That(r.Entries[0].IsCompressed, Is.True);
    Assert.That(r.Entries[0].CompressedSize, Is.LessThan(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFiles() {
    var d1 = "Hello, Saint's Row!"u8.ToArray();
    var d2 = new byte[16 * 1024]; // zeros — compresses
    var d3 = MakeRandomFilled(257, seed: 7); // pseudo-random — likely won't compress

    using var ms = new MemoryStream();
    using (var w = new FileFormat.VppV2.VppV2Writer(ms, leaveOpen: true)) {
      w.AddEntry("misc/spam.dat", d1);
      w.AddEntry("zeros/big.bin", d2);
      w.AddEntry("rand/noise.dat", d3);
    }
    ms.Position = 0;

    var r = new FileFormat.VppV2.VppV2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("misc/spam.dat"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("zeros/big.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("rand/noise.dat"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d3));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AlignmentPreserved() {
    var d1 = MakeRandomFilled(123, seed: 1);
    var d2 = new byte[32 * 1024]; // zeros — will be compressed
    using var ms = new MemoryStream();
    using (var w = new FileFormat.VppV2.VppV2Writer(ms, leaveOpen: true)) {
      w.AddEntry("a.dat", d1);
      w.AddEntry("b.bin", d2);
    }
    ms.Position = 0;

    var r = new FileFormat.VppV2.VppV2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    foreach (var e in r.Entries)
      Assert.That(e.DataOffset % DataAlignment, Is.EqualTo(0),
        $"Entry '{e.Name}' offset {e.DataOffset} not 0x10-aligned.");

    // TOC declared at HeaderSize == 0x800. Verify by re-reading the field directly.
    var headerBuf = ms.ToArray();
    var headerSizeField = BitConverter.ToUInt32(headerBuf, HeaderSizeFieldOffset);
    Assert.That(headerSizeField, Is.EqualTo(0x800u));
  }

  // ──────────────────────────── Reader error handling ────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[SectionAlignment];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.VppV2.VppV2Reader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsV1Version() {
    var buf = BuildSyntheticHeader(version: 1u, fileCount: 0u, flags: 0u, headerSizeFieldValue: 0x800u);
    using var ms = new MemoryStream(buf);
    var ex = Assert.Throws<NotSupportedException>(() => _ = new FileFormat.VppV2.VppV2Reader(ms));
    Assert.That(ex!.Message, Does.Contain("version").IgnoreCase);
    Assert.That(ex.Message, Does.Contain("v1").IgnoreCase);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsCondensedFlag() {
    var buf = BuildSyntheticHeader(version: 2u, fileCount: 0u, flags: 0x2u, headerSizeFieldValue: 0x800u);
    using var ms = new MemoryStream(buf);
    var ex = Assert.Throws<NotSupportedException>(() => _ = new FileFormat.VppV2.VppV2Reader(ms));
    Assert.That(ex!.Message, Does.Contain("Condensed"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadHeaderSize() {
    var buf = BuildSyntheticHeader(version: 2u, fileCount: 0u, flags: 0u, headerSizeFieldValue: 0x400u);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.VppV2.VppV2Reader(ms));
  }

  // ──────────────────────────── Writer fallback to stored ────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_StoresWhenCompressionDoesntHelp() {
    var data = MakeRandomFilled(4096, seed: 42);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.VppV2.VppV2Writer(ms, leaveOpen: true))
      w.AddEntry("entropy.bin", data);
    ms.Position = 0;

    var r = new FileFormat.VppV2.VppV2Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].IsCompressed, Is.False, "random data should fall back to stored");
    Assert.That(r.Entries[0].CompressedSize, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  // ──────────────────────────── Descriptor ────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.VppV2.VppV2FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("VppV2"));
    Assert.That(d.DisplayName, Is.EqualTo("Volition VPP v2 (Saint's Row 2)"));
    Assert.That(d.Extensions, Contains.Item(".vpp_pc"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".vpp_pc"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(MagicBytes));
    Assert.That(d.MagicSignatures[0].Confidence, Is.EqualTo(0.93));
    Assert.That(d.Methods, Has.Count.EqualTo(2));
    Assert.That(d.Methods[0].Name, Is.EqualTo("stored"));
    Assert.That(d.Methods[1].Name, Is.EqualTo("zlib"));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ListReportsOriginalAndCompressedSizes() {
    var data = new byte[16 * 1024]; // zeros — compresses.

    using var ms = new MemoryStream();
    using (var w = new FileFormat.VppV2.VppV2Writer(ms, leaveOpen: true))
      w.AddEntry("payload.dat", data);
    ms.Position = 0;

    var d = new FileFormat.VppV2.VppV2FormatDescriptor();
    var entries = d.List(ms, password: null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(data.Length));
    Assert.That(entries[0].CompressedSize, Is.LessThan(data.Length));
    Assert.That(entries[0].Method, Is.EqualTo("Zlib"));
  }

  // ──────────────────────────── Helpers ────────────────────────────

  private static byte[] MakeRandomFilled(int length, int seed) {
    var rng = new Random(seed);
    var buf = new byte[length];
    rng.NextBytes(buf);
    return buf;
  }

  /// <summary>Builds a 0x800-byte zero-padded header block with the given fields populated.</summary>
  private static byte[] BuildSyntheticHeader(uint version, uint fileCount, uint flags, uint headerSizeFieldValue) {
    var buf = new byte[SectionAlignment];
    BitConverter.GetBytes(0x51890ACEu).CopyTo(buf, 0);
    BitConverter.GetBytes(version).CopyTo(buf, 4);
    BitConverter.GetBytes(headerSizeFieldValue).CopyTo(buf, HeaderSizeFieldOffset);
    BitConverter.GetBytes(fileCount).CopyTo(buf, FileCountFieldOffset);
    BitConverter.GetBytes((uint)SectionAlignment).CopyTo(buf, ArchiveSizeFieldOffset);
    BitConverter.GetBytes((uint)(fileCount * 28)).CopyTo(buf, TocSizeFieldOffset);
    BitConverter.GetBytes(0u).CopyTo(buf, NameTableSizeFieldOffset);
    BitConverter.GetBytes(0u).CopyTo(buf, DataSizeFieldOffset);
    BitConverter.GetBytes(0u).CopyTo(buf, CompressedSizeFieldOffset);
    BitConverter.GetBytes(flags).CopyTo(buf, FlagsFieldOffset);
    return buf;
  }
}

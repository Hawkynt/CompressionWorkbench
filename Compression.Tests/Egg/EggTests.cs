using System.Text;
using Compression.Registry;

namespace Compression.Tests.Egg;

/// <summary>
/// Struct-parity tests for the EGG (ALZip) reader. There is no local EGG creator
/// and no real .egg sample here, so these tests hand-craft minimal spec-conformant
/// byte buffers (per the published EGG Format Specification v1.0) and assert the
/// reader parses and decodes them. They do not claim to read real ALZip output.
/// </summary>
[TestFixture]
public class EggTests {

  // Magics (little-endian uint32) from the EGG Format Specification v1.0.
  private const uint EggMagic = 0x41474745;
  private const uint EndMarker = 0x08E28222;
  private const uint FileHeaderMagic = 0x0A8590E3;
  private const uint BlockHeaderMagic = 0x02B50C13;
  private const uint FilenameMagic = 0x0A8591AC;
  private const uint WindowsInfoMagic = 0x2C86950B;

  /// <summary>Builds a single-file, single-block archive with explicit stored bytes.</summary>
  private static byte[] BuildStoreOrRaw(string name, byte[] uncompressed, byte[] stored, byte algorithm) {
    using var ms = new MemoryStream();
    var w = new BinaryWriter(ms, Encoding.UTF8);

    w.Write(EggMagic);
    w.Write((ushort)0x0100);
    w.Write(1u);
    w.Write(0u);
    w.Write(EndMarker);

    w.Write(FileHeaderMagic);
    w.Write(0u);
    w.Write((long)uncompressed.Length);

    var nameBytes = Encoding.UTF8.GetBytes(name);
    w.Write(FilenameMagic);
    w.Write((byte)0x00);
    w.Write((ushort)nameBytes.Length);
    w.Write(nameBytes);
    w.Write(EndMarker);

    w.Write(BlockHeaderMagic);
    w.Write(algorithm);
    w.Write((byte)0);
    w.Write((uint)uncompressed.Length);
    w.Write((uint)stored.Length);
    w.Write(0u);
    w.Write(EndMarker);
    w.Write(stored);        // compressed data

    w.Write(EndMarker);     // end of archive
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Egg.EggFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Egg"));
    Assert.That(d.Extensions, Contains.Item(".egg"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    // Read-only: must NOT advertise create/modify.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("EGGA"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Store_ListsAndExtracts() {
    var payload = "hello.txt"u8.ToArray();
    var archive = BuildStoreOrRaw("hello.txt", payload, payload, algorithm: 0);

    var d = new FileFormat.Egg.EggFormatDescriptor();
    using var ms = new MemoryStream(archive);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(payload.Length));
    Assert.That(entries[0].Method, Is.EqualTo("Store"));
    Assert.That(entries[0].IsDirectory, Is.False);

    ms.Position = 0;
    var extracted = ((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "hello.txt", null);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Deflate_ListsAndExtracts() {
    var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("EGG deflate round trip payload. ", 20)));
    byte[] deflated;
    using (var comp = new MemoryStream()) {
      using (var ds = new System.IO.Compression.DeflateStream(comp, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        ds.Write(payload, 0, payload.Length);
      deflated = comp.ToArray();
    }

    var archive = BuildStoreOrRaw("data.bin", payload, deflated, algorithm: 1);

    var d = new FileFormat.Egg.EggFormatDescriptor();
    using var ms = new MemoryStream(archive);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Method, Is.EqualTo("Deflate"));

    ms.Position = 0;
    var extracted = ((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "data.bin", null);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void UnsupportedMethod_ListsButThrowsOnExtract() {
    var payload = "ignored"u8.ToArray();
    // Algorithm 4 = LZMA — listed, but extraction must throw rather than fake bytes.
    var archive = BuildStoreOrRaw("movie.lzma", payload, payload, algorithm: 4);

    var d = new FileFormat.Egg.EggFormatDescriptor();
    using var ms = new MemoryStream(archive);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Method, Is.EqualTo("LZMA"));

    ms.Position = 0;
    Assert.Throws<NotSupportedException>(() =>
      _ = ((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "movie.lzma", null));
  }

  [Test, Category("HappyPath")]
  public void Directory_DetectedFromWindowsAttribute() {
    // A directory entry: file length 0, no data block, Windows attribute Directory bit set.
    using var ms = new MemoryStream();
    var w = new BinaryWriter(ms, Encoding.UTF8);
    w.Write(EggMagic);
    w.Write((ushort)0x0100);
    w.Write(1u);
    w.Write(0u);
    w.Write(EndMarker);
    w.Write(FileHeaderMagic);
    w.Write(0u);
    w.Write(0L); // file length
    var nameBytes = "folder"u8.ToArray();
    w.Write(FilenameMagic);
    w.Write((byte)0x00);
    w.Write((ushort)nameBytes.Length);
    w.Write(nameBytes);
    w.Write(WindowsInfoMagic);
    w.Write((byte)0x00);
    w.Write((ushort)9);
    w.Write(0L);
    w.Write((byte)0x80); // Directory
    w.Write(EndMarker);  // end of file-header extras (no block)
    w.Write(EndMarker);  // end of archive

    var d = new FileFormat.Egg.EggFormatDescriptor();
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("folder"));
    Assert.That(entries[0].IsDirectory, Is.True);
  }
}

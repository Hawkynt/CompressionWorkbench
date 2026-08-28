using System.Text;
using Compression.Core.Checksums;
using Compression.Registry;
using FileFormat.Egg;

namespace Compression.Tests.Egg;

/// <summary>
/// Struct-parity and round-trip tests for the EGG (ALZip) implementation.
/// Hand-crafted buffers follow the published EGG Format Specification; writer tests
/// additionally verify that newly-created archives round-trip through the native reader.
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

    var crc = new Crc32();
    crc.Update(uncompressed);
    w.Write(BlockHeaderMagic);
    w.Write(algorithm);
    w.Write((byte)0);
    w.Write((uint)uncompressed.Length);
    w.Write((uint)stored.Length);
    w.Write(crc.Value);
    w.Write(EndMarker);
    w.Write(stored);

    w.Write(EndMarker);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new EggFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("Egg"));
      Assert.That(d.Extensions, Contains.Item(".egg"));
      Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
        "rebuild-backed add/remove is not advertised as genuine in-place R/W");
      Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("EGGA"u8.ToArray()).AsCollection);
      Assert.That(d.Methods.Select(method => method.Name), Is.EquivalentTo(new[] { "store", "deflate" }));
    });
  }

  [Test, Category("HappyPath")]
  public void Store_ListsAndExtracts() {
    var payload = "hello.txt"u8.ToArray();
    var archive = BuildStoreOrRaw("hello.txt", payload, payload, algorithm: 0);

    var d = new EggFormatDescriptor();
    using var ms = new MemoryStream(archive);
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(payload.Length));
    Assert.That(entries[0].Method, Is.EqualTo("Store"));
    Assert.That(entries[0].IsDirectory, Is.False);

    ms.Position = 0;
    var extracted = ((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "hello.txt", null);
    Assert.That(extracted, Is.EqualTo(payload).AsCollection);
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

    var d = new EggFormatDescriptor();
    using var ms = new MemoryStream(archive);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Method, Is.EqualTo("Deflate"));

    ms.Position = 0;
    var extracted = ((IArchiveFormatOperations)d).ExtractEntryToMemory(ms, "data.bin", null);
    Assert.That(extracted, Is.EqualTo(payload).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_Deflate_RoundTripsNestedUnicodeAndDirectoryEntries() {
    var repeated = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compress me please. ", 100)));
    byte[] binary = [0, 1, 2, 3, 4, 5, 0xFE, 0xFF];
    ArchiveInputInfo[] inputs = [
      new("folder", "folder", true),
      ArchiveInputInfo.InMemory("folder/über.txt", repeated),
      ArchiveInputInfo.InMemory("binary.bin", binary),
    ];

    using var output = new MemoryStream();
    var descriptor = new EggFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions { MethodName = "deflate", Level = 9 });

    output.Position = 0;
    var listed = descriptor.List(output, null);
    Assert.Multiple(() => {
      Assert.That(listed.Single(entry => entry.Name == "folder").IsDirectory, Is.True);
      Assert.That(listed.Single(entry => entry.Name == "folder/über.txt").Method, Is.EqualTo("Deflate"));
      Assert.That(listed.Single(entry => entry.Name == "binary.bin").Method, Is.EqualTo("Deflate"));
    });

    output.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(output, "folder/über.txt", null), Is.EqualTo(repeated).AsCollection);
    output.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(output, "binary.bin", null), Is.EqualTo(binary).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_Auto_StoresTinyIncompressibleAndDeflatesRepeatedData() {
    byte[] tiny = [0x00, 0x7F, 0x80, 0xFF];
    var repeated = Enumerable.Repeat((byte)'A', 4096).ToArray();
    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("tiny.bin", tiny),
      ArchiveInputInfo.InMemory("repeat.bin", repeated),
    ];

    using var output = new MemoryStream();
    var descriptor = new EggFormatDescriptor();
    descriptor.Create(output, inputs, new FormatCreateOptions());

    output.Position = 0;
    var listed = descriptor.List(output, null);
    Assert.Multiple(() => {
      Assert.That(listed.Single(entry => entry.Name == "tiny.bin").Method, Is.EqualTo("Store"));
      Assert.That(listed.Single(entry => entry.Name == "repeat.bin").Method, Is.EqualTo("Deflate"));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_LongUtf8Filename_UsesExtendedExtraFieldLength() {
    var longName = new string('x', 70_000) + ".bin";
    using var output = new MemoryStream();
    using (var writer = new EggWriter(output, leaveOpen: true)) {
      writer.AddEntry(longName, [1, 2, 3], EggCompressionMethod.Store);
      writer.Finish();
    }

    output.Position = 0;
    using var reader = new EggReader(output, leaveOpen: true);
    Assert.That(reader.Entries.Single().Name, Is.EqualTo(longName));
    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(new byte[] { 1, 2, 3 }).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void UnsupportedMethod_ListsButThrowsOnExtract() {
    var payload = "ignored"u8.ToArray();
    // Algorithm 4 = LZMA — listed, but extraction must throw rather than fake bytes.
    var archive = BuildStoreOrRaw("movie.lzma", payload, payload, algorithm: 4);

    var d = new EggFormatDescriptor();
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
    w.Write(0L);
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

    var d = new EggFormatDescriptor();
    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("folder"));
    Assert.That(entries[0].IsDirectory, Is.True);
  }

  [Test, Category("EdgeCase")]
  public void CorruptBlockCrc_IsRejected() {
    var payload = "crc protected"u8.ToArray();
    var archive = BuildStoreOrRaw("crc.bin", payload, payload, algorithm: 0);
    var dataStart = archive.Length - payload.Length - sizeof(uint); // final archive end marker follows payload
    archive[dataStart - 8] ^= 0x01; // CRC starts eight bytes before payload (CRC32 + block END)

    using var stream = new MemoryStream(archive);
    using var reader = new EggReader(stream, leaveOpen: true);
    Assert.That(() => reader.Extract(reader.Entries.Single()), Throws.TypeOf<InvalidDataException>());
  }

  [Test, Category("EdgeCase")]
  public void Writer_RejectsUnsafeDuplicateAndUnsupportedCreationOptions() {
    using var output = new MemoryStream();
    using (var writer = new EggWriter(output, leaveOpen: true)) {
      writer.AddEntry("file.bin", [1]);
      Assert.Multiple(() => {
        Assert.That(() => writer.AddEntry("file.bin", [2]), Throws.TypeOf<ArgumentException>());
        Assert.That(() => writer.AddEntry("file.bin/child", [3]), Throws.TypeOf<ArgumentException>());
        Assert.That(() => writer.AddEntry("../escape.bin", [4]), Throws.TypeOf<ArgumentException>());
      });
    }

    using var encrypted = new MemoryStream();
    Assert.That(
      () => new EggFormatDescriptor().Create(encrypted,
        [ArchiveInputInfo.InMemory("file.bin", new byte[] { 1 })],
        new FormatCreateOptions { Password = "secret" }),
      Throws.TypeOf<NotSupportedException>());
  }
}

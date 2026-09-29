#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using FileFormat.Pst;

namespace Compression.Tests.Pst;

[TestFixture]
public class PstTests {

  // Specification-shaped header fixture. Unicode headers occupy 564 bytes;
  // ANSI headers occupy 512 bytes.
  private static byte[] MakeMinimalPst(ushort wVer, ulong rootBbt, ulong rootNbt, byte cryptMethod = 0) {
    var unicode = wVer >= 23;
    var blob = new byte[unicode ? 564 : 512];
    blob[0] = 0x21; blob[1] = 0x42; blob[2] = 0x44; blob[3] = 0x4E; // "!BDN"
    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), 0xDEADBEEFu);  // dwCRCPartial (reported, not verified)
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(8), 0x4D53);       // wMagicClient "SM"
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(10), wVer);
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(12), 19);          // wVerClient

    if (unicode) {
      // Unicode ROOT: NBT BREF at +36 and BBT BREF at +52.
      const int rootOff = 180;
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(rootOff + 44), rootNbt);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(rootOff + 60), rootBbt);
      blob[512] = 0x80; // bSentinel
    } else {
      // ANSI ROOT: NBT BREF at +20 and BBT BREF at +28.
      const int rootOff = 164;
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(rootOff + 24), (uint)rootNbt);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(rootOff + 32), (uint)rootBbt);
      blob[460] = 0x80; // bSentinel
    }
    blob[unicode ? 513 : 461] = cryptMethod;
    return blob;
  }

  [Category("HappyPath")]
  [Test]
  public void List_UnicodePst_ReturnsCanonicalEntries() {
    var data = MakeMinimalPst(0x17, rootBbt: 0x1000UL, rootNbt: 0x2000UL);
    using var ms = new MemoryStream(data);
    var entries = new PstFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.pst"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "header.bin"), Is.True);

    var headerEntry = entries.Single(e => e.Name == "header.bin");
    Assert.That(headerEntry.OriginalSize, Is.EqualTo(564));
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_UnicodePst_WritesFilesAndMetadata() {
    var data = MakeMinimalPst(0x17, rootBbt: 0x1000UL, rootNbt: 0x2000UL);
    var tmp = Path.Combine(Path.GetTempPath(), "pst_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PstFormatDescriptor().Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.pst")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "header.bin")), Is.True);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=unicode"));
      Assert.That(ini, Does.Contain("version=23"));
      Assert.That(ini, Does.Contain("root_bbt_offset=4096"));
      Assert.That(ini, Does.Contain("root_nbt_offset=8192"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("HappyPath")]
  [Test]
  public void List_AnsiPst_RecognizedAsAnsi() {
    var data = MakeMinimalPst(0x0E, rootBbt: 0x100UL, rootNbt: 0x200UL);
    var tmp = Path.Combine(Path.GetTempPath(), "pst_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PstFormatDescriptor().Extract(ms, tmp, null, null);

      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("format=ansi"));
      Assert.That(ini, Does.Contain("version=14"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Category("Regression")]
  [TestCase((ushort)14)]
  [TestCase((ushort)15)]
  public void AnsiVersions14And15UseTheAnsiHeaderLayout(ushort version) {
    var data = MakeMinimalPst(version, rootBbt: 0x100, rootNbt: 0x200);
    using var ms = new MemoryStream(data);
    var entries = new PstFormatDescriptor().List(ms, null);
    Assert.That(entries.Single(e => e.Name == "header.bin").OriginalSize, Is.EqualTo(512));
    Assert.That(Encoding.UTF8.GetString(new PstFormatDescriptor().ExtractEntryToMemory(ms, "metadata.ini", null)),
      Does.Contain("root_nbt_offset=512"));
  }

  [Category("RoundTrip")]
  [TestCase((ushort)15)]
  [TestCase((ushort)23)]
  [TestCase((ushort)37)]
  public void Create_ReemitsExistingPstBytesExactly(ushort version) {
    var cryptMethod = version == 37 ? (byte)0x10 : (byte)0x01;
    var extension = version == 37 ? ".ost" : ".pst";
    var source = MakeMinimalPst(version, rootBbt: 0x1000, rootNbt: 0x2000, cryptMethod: cryptMethod)
      .Concat(Enumerable.Range(0, 257).Select(static i => (byte)(i * 31))).ToArray();
    using var output = new MemoryStream();
    new PstFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("mailbox" + extension, source)], new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(source));
  }

  [Category("RoundTrip")]
  [Test]
  public void CreateFromStreams_PreservesNonSeekableOstInput() {
    var source = MakeMinimalPst(23, rootBbt: 0x1000, rootNbt: 0x2000)
      .Concat(Enumerable.Range(0, 511).Select(static i => (byte)(i * 17))).ToArray();
    using var output = new MemoryStream();
    new PstFormatDescriptor().CreateFromStreams(output,
      [new StreamingArchiveInput("mailbox.ost", source.Length, false, () => new ForwardOnlyStream(source))],
      new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(source));
  }

  [Category("Validation")]
  [Test]
  public void Create_RejectsLooseFilesInsteadOfInventingMailboxContents() {
    var descriptor = new PstFormatDescriptor();
    using var output = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => descriptor.Create(output,
      [ArchiveInputInfo.InMemory("message.eml", "mail"u8.ToArray())], new FormatCreateOptions()));
  }

  [Category("Validation")]
  [Test]
  public void List_RejectsUnsupportedVersionsAndBadSentinel() {
    var invalidVersion = MakeMinimalPst(21, 0, 0);
    using (var ms = new MemoryStream(invalidVersion))
      Assert.Throws<InvalidDataException>(() => new PstFormatDescriptor().List(ms, null));

    var invalidSentinel = MakeMinimalPst(23, 0, 0);
    invalidSentinel[512] = 0;
    using var bad = new MemoryStream(invalidSentinel);
    Assert.Throws<InvalidDataException>(() => new PstFormatDescriptor().List(bad, null));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicAndExtensions() {
    var d = new PstFormatDescriptor();
    Assert.That(d.Extensions, Does.Contain(".pst"));
    Assert.That(d.Extensions, Does.Contain(".ost"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x21, 0x42, 0x44, 0x4E }));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  private sealed class ForwardOnlyStream(byte[] bytes) : Stream {
    private readonly MemoryStream _inner = new(bytes, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => this._inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => this._inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) {
      if (disposing) this._inner.Dispose();
      base.Dispose(disposing);
    }
  }
}

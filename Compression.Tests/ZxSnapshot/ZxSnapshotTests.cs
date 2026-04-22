using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Tests.ZxSnapshot;

[TestFixture]
public class ZxSnapshotTests {

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("ZxSnapshot"));
    Assert.That(d.Extensions, Does.Contain(".sna"));
    Assert.That(d.Extensions, Does.Contain(".z80"));
    Assert.That(d.Extensions, Does.Contain(".tap"));
    Assert.That(d.Extensions, Does.Contain(".tzx"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("HappyPath")]
  public void List_Sna48K_ReturnsExpectedEntries() {
    var blob = new byte[49179];
    // fill registers with a pattern
    for (var i = 0; i < 27; i++) blob[i] = (byte)i;
    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    using var ms = new MemoryStream(blob);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("FULL.sna"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("registers.bin"));
    Assert.That(names, Does.Contain("memory_16k.bin"));
    Assert.That(names, Does.Contain("memory_32k.bin"));
    Assert.That(names, Does.Contain("memory_48k.bin"));
  }

  [Test, Category("HappyPath")]
  public void Extract_Sna48K_WritesFiles() {
    var blob = new byte[49179];
    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      using var ms = new MemoryStream(blob);
      d.Extract(ms, tmpDir, null, null);
      Assert.That(new FileInfo(Path.Combine(tmpDir, "registers.bin")).Length, Is.EqualTo(27));
      Assert.That(new FileInfo(Path.Combine(tmpDir, "memory_16k.bin")).Length, Is.EqualTo(16384));
      Assert.That(new FileInfo(Path.Combine(tmpDir, "memory_32k.bin")).Length, Is.EqualTo(16384));
      Assert.That(new FileInfo(Path.Combine(tmpDir, "memory_48k.bin")).Length, Is.EqualTo(16384));
      var meta = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("format=sna"));
      Assert.That(meta, Does.Contain("model=48K"));
    } finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
  }

  [Test, Category("HappyPath")]
  public void List_Tap_ReturnsBlocks() {
    // Two tap blocks: length 3 header + 4 data bytes each.
    using var msBuild = new MemoryStream();
    var b1 = new byte[] { 0x00, 0xAA, 0xBB, 0xCC, 0xDD };
    var b2 = new byte[] { 0xFF, 0x11, 0x22, 0x33, 0x44 };
    var lb1 = new byte[2 + b1.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(lb1, (ushort)b1.Length);
    Array.Copy(b1, 0, lb1, 2, b1.Length);
    var lb2 = new byte[2 + b2.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(lb2, (ushort)b2.Length);
    Array.Copy(b2, 0, lb2, 2, b2.Length);
    msBuild.Write(lb1);
    msBuild.Write(lb2);
    var blob = msBuild.ToArray();

    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    using var ms = new MemoryStream(blob);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToArray();
    Assert.That(names.Any(n => n == "blocks/block_000.bin"), Is.True);
    Assert.That(names.Any(n => n == "blocks/block_001.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void List_Tzx_ReturnsBlocks() {
    // TZX magic + version 1.20 + one 0x30 text-description block with "Hi".
    using var msBuild = new MemoryStream();
    msBuild.Write(FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor.TzxMagic);
    msBuild.WriteByte(1);
    msBuild.WriteByte(20);
    // Block id 0x30: one byte length, then text
    var text = "Hi"u8.ToArray();
    msBuild.WriteByte(0x30);
    msBuild.WriteByte((byte)text.Length);
    msBuild.Write(text);
    var blob = msBuild.ToArray();

    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    using var ms = new MemoryStream(blob);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("FULL.tzx"));
    Assert.That(names.Any(n => n.StartsWith("blocks/block_000_id30")), Is.True);
    var meta = entries.First(e => e.Name == "metadata.ini");
    Assert.That(meta.OriginalSize, Is.GreaterThan(0));
  }

  [Test, Category("HappyPath")]
  public void Extract_Tzx_WritesBlocks() {
    using var msBuild = new MemoryStream();
    msBuild.Write(FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor.TzxMagic);
    msBuild.WriteByte(1);
    msBuild.WriteByte(20);
    var text = "Hi"u8.ToArray();
    msBuild.WriteByte(0x30);
    msBuild.WriteByte((byte)text.Length);
    msBuild.Write(text);
    var blob = msBuild.ToArray();

    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    var tmpDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmpDir);
    try {
      using var ms = new MemoryStream(blob);
      d.Extract(ms, tmpDir, null, null);
      Assert.That(File.Exists(Path.Combine(tmpDir, "FULL.tzx")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmpDir, "metadata.ini")), Is.True);
      Assert.That(Directory.EnumerateFiles(Path.Combine(tmpDir, "blocks")).Any(), Is.True);
      var meta = File.ReadAllText(Path.Combine(tmpDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("format=tzx"));
    } finally { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
  }

  [Test, Category("HappyPath")]
  public void List_Z80v1_WithPC_DetectedAsZ80() {
    // Minimal v1 z80 header: 30 bytes, PC at offset 6-7 != 0.
    var blob = new byte[30 + 16];
    BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(6, 2), 0x8000);
    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    using var ms = new MemoryStream(blob);
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.z80"), Is.True);
    Assert.That(entries.Any(e => e.Name == "header.bin"), Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void List_Unknown_DoesNotThrow() {
    var d = new FileFormat.ZxSnapshot.ZxSnapshotFormatDescriptor();
    using var ms = new MemoryStream(new byte[10]);
    var entries = d.List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }
}

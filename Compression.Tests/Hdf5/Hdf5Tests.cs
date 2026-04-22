using System.Text;
using FileFormat.Hdf5;

namespace Compression.Tests.Hdf5;

[TestFixture]
public class Hdf5Tests {
  // Build a minimal HDF5-looking file: magic + V2 superblock prefix.
  private static byte[] BuildMinimalHdf5() {
    // [0..7]   HDF5 signature
    // [8]      version = 2
    // [9]      offset size = 8
    // [10]     length size = 8
    // [11]     flags = 0
    // [12..19] base address (0)
    // [20..27] superblock extension address (0xFF...FF = undef)
    // [28..35] end-of-file address
    // [36..43] root group object header address = 96
    // [44..47] checksum (ignored)
    // Then pad to some useful length and include an OHDR signature.
    var ms = new MemoryStream();
    ms.Write([0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A]);
    ms.WriteByte(2);   // version
    ms.WriteByte(8);   // offset size
    ms.WriteByte(8);   // length size
    ms.WriteByte(0);   // flags
    WriteLong(ms, 0);  // base addr
    WriteLong(ms, 0xFFFFFFFFFFFFFFFFUL); // ext addr
    WriteLong(ms, 256);                   // eof addr
    WriteLong(ms, 96);                    // root group ohdr addr
    WriteLong(ms, 0);                     // "checksum" (dummy)
    // Pad to 96, then write OHDR signature
    while (ms.Length < 96) ms.WriteByte(0);
    ms.Write("OHDR"u8);
    // Pad out to 256 total.
    while (ms.Length < 256) ms.WriteByte(0);
    return ms.ToArray();
  }

  private static void WriteLong(Stream s, ulong v) {
    Span<byte> buf = stackalloc byte[8];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, v);
    s.Write(buf);
  }

  // ── Descriptor metadata ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_ReportsHdf5Extensions() {
    var d = new Hdf5FormatDescriptor();
    Assert.That(d.DefaultExtension, Is.EqualTo(".h5"));
    Assert.That(d.Extensions, Does.Contain(".h5"));
    Assert.That(d.Extensions, Does.Contain(".hdf5"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_MagicSignature_IsHdf5Signature() {
    var d = new Hdf5FormatDescriptor();
    Assert.That(d.MagicSignatures, Is.Not.Empty);
    Assert.That(d.MagicSignatures[0].Bytes[0], Is.EqualTo((byte)0x89));
    Assert.That(d.MagicSignatures[0].Bytes[1], Is.EqualTo((byte)0x48));
  }

  // ── List / Extract on minimal synthetic HDF5 ─────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void List_ReturnsFullAndMetadata_WithoutThrowing() {
    var blob = BuildMinimalHdf5();
    using var ms = new MemoryStream(blob);
    var d = new Hdf5FormatDescriptor();

    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.h5"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("objects.txt"));
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_WritesMetadataReportingSuperblockVersion() {
    var blob = BuildMinimalHdf5();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(blob);
      var d = new Hdf5FormatDescriptor();
      d.Extract(ms, tmp, null, null);

      Assert.That(File.Exists(Path.Combine(tmp, "FULL.h5")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "objects.txt")), Is.True);

      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Does.Contain("superblock_version=2"));
      Assert.That(meta, Does.Contain("offset_size=8"));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  // ── Robustness ───────────────────────────────────────────────────────────

  [Category("Robustness")]
  [Test]
  public void List_CorruptedMagic_DoesNotThrow() {
    var blob = new byte[64];
    Array.Fill(blob, (byte)0xAB);
    using var ms = new MemoryStream(blob);
    var d = new Hdf5FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.h5"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }

  [Category("Robustness")]
  [Test]
  public void List_JustMagicBytes_ReportsPartial() {
    var blob = new byte[] { 0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A };
    using var ms = new MemoryStream(blob);
    var d = new Hdf5FormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}

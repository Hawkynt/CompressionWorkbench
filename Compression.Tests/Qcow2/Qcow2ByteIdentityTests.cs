using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Qcow2;

/// <summary>
/// Byte-identity contract tests for QCOW2 containers.
///
/// <para>QCOW2 starts every image with a 72-byte fixed header at offset 0:
/// "QFI\xFB" magic, version, backing-file pointer, cluster bits, disk size,
/// L1/refcount table pointers. The L1 table sits at cluster 1 (offset
/// 0x10000) and the refcount table follows the L2 tables. When the inner
/// filesystem is mutated through the IArchiveModifiable pass-through, this
/// header structure and the magic must survive byte-identical — qemu-img
/// and every QCOW2 consumer reads them first to dispatch the image.</para>
/// </summary>
[TestFixture]
public class Qcow2ByteIdentityTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // QCOW2 header is 72 bytes (v2) per the qcow2 spec; magic at [0..4).
  private const int Qcow2HeaderSize = 72;

  [Test, Category("ContractLock")]
  public void Qcow2_Magic_Survives_Add_ByteIdentical() {
    using var img = BuildQcow2WithFat("EXIST.TXT", "existing"u8.ToArray());
    var preMagic = ReadAt(img, 0, 4);

    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(img, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postMagic = ReadAt(img, 0, 4);
    Assert.That(postMagic, Is.EqualTo(preMagic),
      "QFI\\xFB magic at offset 0 must survive byte-identical");
    Assert.That(postMagic, Is.EqualTo(new byte[] { 0x51, 0x46, 0x49, 0xFB }));
  }

  [Test, Category("ContractLock")]
  public void Qcow2_Header_Survives_Add_ByteIdentical() {
    using var img = BuildQcow2WithFat("EXIST.TXT", "existing"u8.ToArray());
    var preHeader = ReadAt(img, 0, Qcow2HeaderSize);

    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(img, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postHeader = ReadAt(img, 0, Qcow2HeaderSize);
    Assert.That(postHeader, Is.EqualTo(preHeader),
      "QCOW2 v2 72-byte header (magic + version + ptrs + cluster_bits + disk_size) " +
      "must survive byte-identical");
  }

  [Test, Category("ContractLock")]
  public void Qcow2_Magic_Survives_Remove_ByteIdentical() {
    using var img = BuildQcow2WithFat("KEEP.TXT", "keep"u8.ToArray(),
                                       "DROP.TXT", "drop"u8.ToArray());
    var preMagic = ReadAt(img, 0, 4);

    var desc = new FileFormat.Qcow2.Qcow2FormatDescriptor();
    ((IArchiveModifiable)desc).Remove(img, ["DROP.TXT"]);

    var postMagic = ReadAt(img, 0, 4);
    Assert.That(postMagic, Is.EqualTo(preMagic),
      "QFI\\xFB magic must survive Remove byte-identical");
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static byte[] ReadAt(Stream s, long offset, int length) {
    var prev = s.Position;
    try {
      s.Position = offset;
      var buf = new byte[length];
      s.ReadExactly(buf);
      return buf;
    } finally {
      s.Position = prev;
    }
  }

  private static MemoryStream BuildQcow2WithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var w = new FileFormat.Qcow2.Qcow2Writer();
    w.SetDiskImage(fatImage);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}

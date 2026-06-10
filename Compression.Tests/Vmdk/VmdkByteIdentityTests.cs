using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Vmdk;

/// <summary>
/// Byte-identity contract tests for VMDK containers.
///
/// <para>VMDK monolithic-sparse images start with a 512-byte sparse header
/// (KDMV magic at offset 0) followed by an embedded ASCII descriptor at
/// sector 1. When the inner filesystem is mutated through the
/// IArchiveModifiable pass-through, the sparse-header magic at offset 0
/// must survive byte-identical — that's what every tool (qemu-img / VMware)
/// uses to identify the container. The Add/Remove fallback path may rebuild
/// the whole VMDK via VmdkWriter, in which case the header is re-emitted
/// identically because the writer is deterministic for a given disk size.</para>
/// </summary>
[TestFixture]
public class VmdkByteIdentityTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  [Test, Category("ContractLock")]
  public void Vmdk_KdmvMagic_Survives_Add_ByteIdentical() {
    using var vmdk = BuildVmdkWithFat("EXIST.TXT", "existing"u8.ToArray());
    var preMagic = ReadAt(vmdk, 0, 4);

    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(vmdk, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postMagic = ReadAt(vmdk, 0, 4);
    Assert.That(postMagic, Is.EqualTo(preMagic),
      "KDMV sparse-header magic at offset 0 must survive byte-identical");
    Assert.That(postMagic, Is.EqualTo(new byte[] { 0x4B, 0x44, 0x4D, 0x56 }));
  }

  [Test, Category("ContractLock")]
  public void Vmdk_KdmvMagic_Survives_Remove_ByteIdentical() {
    using var vmdk = BuildVmdkWithFat("KEEP.TXT", "keep"u8.ToArray(),
                                       "DROP.TXT", "drop"u8.ToArray());
    var preMagic = ReadAt(vmdk, 0, 4);

    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    ((IArchiveModifiable)desc).Remove(vmdk, ["DROP.TXT"]);

    var postMagic = ReadAt(vmdk, 0, 4);
    Assert.That(postMagic, Is.EqualTo(preMagic),
      "KDMV sparse-header magic must survive Remove byte-identical");
  }

  [Test, Category("ContractLock")]
  public void Vmdk_VersionField_Survives_Add_ByteIdentical() {
    using var vmdk = BuildVmdkWithFat("EXIST.TXT", "existing"u8.ToArray());
    // Version field at offset 4 (4 bytes LE).
    var preVersion = ReadAt(vmdk, 4, 4);

    var desc = new FileFormat.Vmdk.VmdkFormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(vmdk, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postVersion = ReadAt(vmdk, 4, 4);
    Assert.That(postVersion, Is.EqualTo(preVersion),
      "Sparse-header version field must survive byte-identical");
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

  private static MemoryStream BuildVmdkWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var vmdkWriter = new FileFormat.Vmdk.VmdkWriter();
    vmdkWriter.SetDiskData(fatImage);
    var vmdkBytes = vmdkWriter.Build();
    var ms = new MemoryStream();
    ms.Write(vmdkBytes);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}

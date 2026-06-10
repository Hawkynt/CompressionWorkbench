using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Vdi;

/// <summary>
/// Byte-identity contract tests for VDI (VirtualBox) containers.
///
/// <para>VDI begins with a 64-byte ASCII pre-header followed by the 4-byte
/// 0xBEDA107F signature at offset 64, then the 400-byte VDI header
/// structure. When the inner filesystem is mutated through the
/// IArchiveModifiable pass-through, the pre-header + signature must survive
/// byte-identical — that's the contract every consumer (VirtualBox, qemu)
/// relies on to dispatch the image.</para>
/// </summary>
[TestFixture]
public class VdiByteIdentityTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // Pre-header (40 bytes "<<< Oracle VM VirtualBox Disk Image >>>\n" + 24 zero
  // pad) is 64 bytes; signature is 4 bytes at offset 64. Total: first 68 bytes
  // are content-addressable identity bytes.
  private const int VdiIdentitySize = 68;

  [Test, Category("ContractLock")]
  public void Vdi_Signature_Survives_Add_ByteIdentical() {
    using var img = BuildVdiWithFat("EXIST.TXT", "existing"u8.ToArray());
    var preSig = ReadAt(img, 64, 4);
    var preHeader = ReadAt(img, 0, 40);

    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(img, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postSig = ReadAt(img, 64, 4);
    var postHeader = ReadAt(img, 0, 40);
    Assert.That(postSig, Is.EqualTo(preSig),
      "VDI 4-byte signature 0xBEDA107F at offset 64 must survive byte-identical");
    Assert.That(postHeader, Is.EqualTo(preHeader),
      "VDI 40-byte ASCII pre-header (\"<<< Oracle VM VirtualBox Disk Image >>>\\n\") must survive byte-identical");
  }

  [Test, Category("ContractLock")]
  public void Vdi_PreHeaderAndSignature_Survive_Add_ByteIdentical() {
    using var img = BuildVdiWithFat("EXIST.TXT", "existing"u8.ToArray());
    var preIdentity = ReadAt(img, 0, VdiIdentitySize);

    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(img, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postIdentity = ReadAt(img, 0, VdiIdentitySize);
    Assert.That(postIdentity, Is.EqualTo(preIdentity),
      "VDI pre-header + signature (bytes 0..68) must survive byte-identical");
  }

  [Test, Category("ContractLock")]
  public void Vdi_Signature_Survives_Remove_ByteIdentical() {
    using var img = BuildVdiWithFat("KEEP.TXT", "keep"u8.ToArray(),
                                     "DROP.TXT", "drop"u8.ToArray());
    var preSig = ReadAt(img, 64, 4);

    var desc = new FileFormat.Vdi.VdiFormatDescriptor();
    ((IArchiveModifiable)desc).Remove(img, ["DROP.TXT"]);

    var postSig = ReadAt(img, 64, 4);
    Assert.That(postSig, Is.EqualTo(preSig),
      "VDI signature must survive Remove byte-identical");
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

  private static MemoryStream BuildVdiWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var ms = new MemoryStream();
    using (var w = new FileFormat.Vdi.VdiWriter(ms, leaveOpen: true, virtualSize: fatImage.Length))
      w.Write(fatImage);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}

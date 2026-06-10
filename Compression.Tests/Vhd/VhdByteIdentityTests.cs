using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Vhd;

/// <summary>
/// Byte-identity contract tests for VHD containers.
///
/// <para>VHD wraps an inner filesystem image and stores a fixed 512-byte
/// "conectix" footer at the end of file. When the inner filesystem is
/// modified via the IArchiveModifiable pass-through, the VHD container
/// shape itself — magic, size, and total file length for a fixed VHD —
/// must remain byte-identical. This fixture locks those invariants so a
/// future change to the inner-FS modifier path can't silently corrupt
/// the outer container.</para>
/// </summary>
[TestFixture]
public class VhdByteIdentityTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  [Test, Category("ContractLock")]
  public void FixedVhd_FooterMagic_Survives_Add_ByteIdentical() {
    using var vhd = BuildFixedVhdWithFat("EXIST.TXT", "existing"u8.ToArray());
    var preFooterMagic = ReadAt(vhd, vhd.Length - 512, 8);
    var preLength = vhd.Length;

    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    var modifiable = (IArchiveModifiable)desc;
    var tmpFile = WriteTempBytes("new"u8.ToArray());
    try {
      modifiable.Add(vhd, [new ArchiveInputInfo(tmpFile, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmpFile);
    }

    // Fixed VHDs reserve exactly 512 bytes for the footer at EOF; total
    // file length must not grow because we only mutated the inner FS.
    Assert.That(vhd.Length, Is.EqualTo(preLength),
      "Fixed VHD total file length must stay constant after inner-FS mutation");

    var postFooterMagic = ReadAt(vhd, vhd.Length - 512, 8);
    Assert.That(postFooterMagic, Is.EqualTo(preFooterMagic),
      "Footer magic bytes at EOF-512 must survive byte-identical");
  }

  [Test, Category("ContractLock")]
  public void FixedVhd_FooterMagic_Survives_Remove_ByteIdentical() {
    using var vhd = BuildFixedVhdWithFat("KEEP.TXT", "keep"u8.ToArray(),
                                          "DROP.TXT", "drop"u8.ToArray());
    var preFooter = ReadAt(vhd, vhd.Length - 512, 512);
    var preLength = vhd.Length;

    var desc = new FileFormat.Vhd.VhdFormatDescriptor();
    ((IArchiveModifiable)desc).Remove(vhd, ["DROP.TXT"]);

    Assert.That(vhd.Length, Is.EqualTo(preLength),
      "Fixed VHD total file length must stay constant after inner-FS remove");

    var postFooter = ReadAt(vhd, vhd.Length - 512, 512);
    Assert.That(postFooter, Is.EqualTo(preFooter),
      "Entire 512-byte footer (magic + geometry + CRC) must survive byte-identical");
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

  private static MemoryStream BuildFixedVhdWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var vhdWriter = new FileFormat.Vhd.VhdWriter();
    vhdWriter.SetDiskData(fatImage);
    var vhdBytes = vhdWriter.Build();
    var ms = new MemoryStream();
    ms.Write(vhdBytes);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}

using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Vhdx;

/// <summary>
/// Byte-identity contract tests for VHDX containers.
///
/// <para>VHDX places the File Type Identifier ("vhdxfile" + creator string) at
/// offset 0 and headers ("head" magic) at 0x10000 / 0x20000. The Region
/// Tables at 0x30000 / 0x40000 describe BAT and Metadata regions which are
/// fixed-layout for a no-log fixed-payload image. When the inner filesystem
/// is mutated via the IArchiveModifiable pass-through, every byte in those
/// reserved header / region-table / metadata regions must remain
/// byte-identical — only the payload-block bytes in the BAT-pointed data
/// area may change. This fixture locks that invariant.</para>
/// </summary>
[TestFixture]
public class VhdxByteIdentityTests {

  [OneTimeSetUp]
  public void EnsureRegistry() => FormatRegistration.EnsureInitialized();

  // VHDX reserved-region anchors (see VhdxWriter remarks):
  //   0x000000 file type identifier
  //   0x010000 header 1
  //   0x020000 header 2
  //   0x030000 region table 1
  //   0x040000 region table 2
  private const int FileTypeIdSize = 0x10000;
  private const long Header1Offset = 0x10000;
  private const long Header2Offset = 0x20000;
  private const long RegionTable1Offset = 0x30000;
  private const long RegionTable2Offset = 0x40000;
  private const int RegionSize = 0x10000;

  [Test, Category("ContractLock")]
  public void FixedVhdx_FileTypeIdentifier_Survives_Add_ByteIdentical() {
    using var vhdx = BuildFixedVhdxWithFat("EXIST.TXT", "existing"u8.ToArray());
    var preFti = ReadAt(vhdx, 0, FileTypeIdSize);

    var desc = new FileFormat.Vhdx.VhdxFormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(vhdx, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postFti = ReadAt(vhdx, 0, FileTypeIdSize);
    Assert.That(postFti, Is.EqualTo(preFti),
      "File Type Identifier (vhdxfile + creator + reserved) must survive byte-identical");

    // Sanity: still has "vhdxfile" magic at offset 0.
    Assert.That(postFti.AsSpan(0, 8).SequenceEqual("vhdxfile"u8), Is.True);
  }

  [Test, Category("ContractLock")]
  public void FixedVhdx_HeaderRegions_Survive_Add_ByteIdentical() {
    using var vhdx = BuildFixedVhdxWithFat("EXIST.TXT", "existing"u8.ToArray());
    var preH1 = ReadAt(vhdx, Header1Offset, RegionSize);
    var preH2 = ReadAt(vhdx, Header2Offset, RegionSize);

    var desc = new FileFormat.Vhdx.VhdxFormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(vhdx, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postH1 = ReadAt(vhdx, Header1Offset, RegionSize);
    var postH2 = ReadAt(vhdx, Header2Offset, RegionSize);
    Assert.That(postH1, Is.EqualTo(preH1),
      "Header1 (CRC + sequence + GUIDs) at 0x10000 must survive byte-identical");
    Assert.That(postH2, Is.EqualTo(preH2),
      "Header2 (active, CRC + sequence + GUIDs) at 0x20000 must survive byte-identical");

    Assert.That(postH1.AsSpan(0, 4).SequenceEqual("head"u8), Is.True, "head magic at Header1");
    Assert.That(postH2.AsSpan(0, 4).SequenceEqual("head"u8), Is.True, "head magic at Header2");
  }

  [Test, Category("ContractLock")]
  public void FixedVhdx_RegionTables_Survive_Add_ByteIdentical() {
    using var vhdx = BuildFixedVhdxWithFat("EXIST.TXT", "existing"u8.ToArray());
    var preRt1 = ReadAt(vhdx, RegionTable1Offset, RegionSize);
    var preRt2 = ReadAt(vhdx, RegionTable2Offset, RegionSize);

    var desc = new FileFormat.Vhdx.VhdxFormatDescriptor();
    var tmp = WriteTempBytes("new"u8.ToArray());
    try {
      ((IArchiveModifiable)desc).Add(vhdx, [new ArchiveInputInfo(tmp, "NEW.TXT", false)]);
    } finally {
      File.Delete(tmp);
    }

    var postRt1 = ReadAt(vhdx, RegionTable1Offset, RegionSize);
    var postRt2 = ReadAt(vhdx, RegionTable2Offset, RegionSize);
    Assert.That(postRt1, Is.EqualTo(preRt1),
      "Region Table 1 (regi magic + BAT/Metadata entries + CRC) must survive byte-identical");
    Assert.That(postRt2, Is.EqualTo(preRt2),
      "Region Table 2 (duplicate of Region Table 1) must survive byte-identical");

    Assert.That(postRt1.AsSpan(0, 4).SequenceEqual("regi"u8), Is.True, "regi magic at RT1");
    Assert.That(postRt2.AsSpan(0, 4).SequenceEqual("regi"u8), Is.True, "regi magic at RT2");
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

  private static MemoryStream BuildFixedVhdxWithFat(params object[] nameDataPairs) {
    var fatWriter = new FileSystem.Fat.FatWriter();
    for (var i = 0; i < nameDataPairs.Length; i += 2) {
      var name = (string)nameDataPairs[i];
      var data = (byte[])nameDataPairs[i + 1];
      fatWriter.AddFile(name, data);
    }
    var fatImage = fatWriter.Build();

    var vhdxWriter = new FileFormat.Vhdx.VhdxWriter();
    vhdxWriter.SetDiskData(fatImage);
    var vhdxBytes = vhdxWriter.Build();
    var ms = new MemoryStream();
    ms.Write(vhdxBytes);
    ms.Position = 0;
    return ms;
  }

  private static string WriteTempBytes(byte[] data) {
    var path = Path.GetTempFileName();
    File.WriteAllBytes(path, data);
    return path;
  }
}

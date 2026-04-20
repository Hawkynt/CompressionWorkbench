namespace Compression.Tests.Cdi;

[TestFixture]
public class CdiTests {

  // Builds a minimal CDI-like image: flat ISO 9660 sectors followed by the
  // CDI v3 footer (version identifier + 4-byte LE offset from EOF).
  private static byte[] BuildCdi(string fileName, byte[] fileData) {
    const int sectorSize = 2048;
    const int pvdLba = 16;
    const int rootDirLba = 18;
    const int fileLba = 19;
    const uint CdiV3 = 0x80000005;

    var totalSectors = fileLba + 2;
    var isoBuf = new byte[totalSectors * sectorSize];

    // File data
    fileData.AsSpan().CopyTo(isoBuf.AsSpan(fileLba * sectorSize));

    // Root directory
    var dirPos = rootDirLba * sectorSize;
    var dot = new byte[34]; dot[0] = 34; dot[2] = rootDirLba; dot[25] = 0x02; dot[32] = 1; dot[33] = 0x00;
    dot.AsSpan().CopyTo(isoBuf.AsSpan(dirPos)); dirPos += 34;
    var dotdot = new byte[34]; dotdot[0] = 34; dotdot[2] = rootDirLba; dotdot[25] = 0x02; dotdot[32] = 1; dotdot[33] = 0x01;
    dotdot.AsSpan().CopyTo(isoBuf.AsSpan(dirPos)); dirPos += 34;

    var isoName = fileName.ToUpperInvariant() + ";1";
    var idLen = (byte)isoName.Length;
    var recLen = (byte)(33 + idLen + ((33 + idLen) % 2));
    var rec = new byte[recLen];
    rec[0] = recLen; rec[2] = (byte)fileLba; rec[6] = (byte)fileLba;
    var sz = (uint)fileData.Length; BitConverter.GetBytes(sz).CopyTo(rec, 10); // LE at offset 10
    rec[32] = idLen; System.Text.Encoding.ASCII.GetBytes(isoName).CopyTo(rec, 33);
    rec.AsSpan().CopyTo(isoBuf.AsSpan(dirPos));
    dirPos += recLen;

    var pvd = new byte[sectorSize];
    pvd[0] = 1; System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1); pvd[6] = 1;
    pvd[156] = 34; pvd[158] = rootDirLba; pvd[162] = rootDirLba;
    pvd[166] = (byte)(dirPos - rootDirLba * sectorSize); pvd[170] = pvd[166];
    pvd[156 + 25] = 0x02; pvd[156 + 32] = 1;
    pvd.AsSpan().CopyTo(isoBuf.AsSpan(pvdLba * sectorSize));

    // CDI footer: 8 bytes — 4-byte LE version ID + 4-byte LE offset from EOF to session descriptor
    // For our minimal image the session descriptor doesn't exist; offset is 0.
    var footer = new byte[8];
    footer[0] = unchecked((byte)CdiV3);
    footer[1] = unchecked((byte)(CdiV3 >> 8));
    footer[2] = unchecked((byte)(CdiV3 >> 16));
    footer[3] = unchecked((byte)(CdiV3 >> 24));
    // offset bytes 4-7 = 0 (no session data follows)

    var result = new byte[isoBuf.Length + footer.Length];
    isoBuf.AsSpan().CopyTo(result);
    footer.AsSpan().CopyTo(result.AsSpan(isoBuf.Length));
    return result;
  }

  [Test, Category("HappyPath")]
  public void Read_Cdi_DetectsV3Footer() {
    var data = "CDI content"u8.ToArray();
    var cdi = BuildCdi("test.txt", data);
    using var ms = new MemoryStream(cdi);

    var r = new FileFormat.Cdi.CdiReader(ms);
    Assert.That(r.CdiVersion, Is.EqualTo(0x80000005u));
  }

  [Test, Category("HappyPath")]
  public void Read_Cdi_ListsFile() {
    var data = "DiscJuggler content"u8.ToArray();
    var cdi = BuildCdi("readme.txt", data);
    using var ms = new MemoryStream(cdi);

    var r = new FileFormat.Cdi.CdiReader(ms);
    Assert.That(r.Entries, Has.Count.GreaterThan(0));
    var file = r.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);
    Assert.That(file!.Size, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Read_Cdi_ExtractReturnsData() {
    var data = new byte[200];
    Random.Shared.NextBytes(data);
    var cdi = BuildCdi("data.bin", data);
    using var ms = new MemoryStream(cdi);

    var r = new FileFormat.Cdi.CdiReader(ms);
    var file = r.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);

    var extracted = r.Extract(file!);
    Assert.That(extracted[..data.Length], Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Read_NoFooter_VersionIsZero() {
    using var ms = new MemoryStream(new byte[2352 * 32]);
    var r = new FileFormat.Cdi.CdiReader(ms);
    Assert.That(r.CdiVersion, Is.EqualTo(0u));
  }

  [Test, Category("HappyPath")]
  public void CdiEntry_Properties() {
    var entry = new FileFormat.Cdi.CdiEntry {
      Name = "GAME.EXE",
      FullPath = "GAME/GAME.EXE",
      IsDirectory = false,
      Size = 4096,
      StartLba = 22,
    };
    Assert.That(entry.Name, Is.EqualTo("GAME.EXE"));
    Assert.That(entry.FullPath, Is.EqualTo("GAME/GAME.EXE"));
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(entry.Size, Is.EqualTo(4096));
    Assert.That(entry.StartLba, Is.EqualTo(22));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("disc.cdi");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Cdi));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Cdi.CdiFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_HasCdiFooter_AndRoundTrips() {
    var payload = "discjuggler-payload"u8.ToArray();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, payload);
      var d = new FileFormat.Cdi.CdiFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "data.bin", false)],
        new Compression.Registry.FormatCreateOptions());

      // Verify the CDI v2 footer (uint32 LE 0x80000004 + uint32 LE 0).
      var bytes = ms.ToArray();
      Assert.That(BitConverter.ToUInt32(bytes, bytes.Length - 8), Is.EqualTo(0x80000004u));

      ms.Position = 0;
      var r = new FileFormat.Cdi.CdiReader(ms);
      Assert.That(r.CdiVersion, Is.EqualTo(0x80000004u));
      var fileEntry = r.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.StartsWith("DATA"));
      Assert.That(fileEntry, Is.Not.Null);
      Assert.That(r.Extract(fileEntry!)[..payload.Length], Is.EqualTo(payload));
    } finally {
      File.Delete(tmp);
    }
  }
}

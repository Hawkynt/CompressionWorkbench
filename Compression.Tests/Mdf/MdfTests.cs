namespace Compression.Tests.Mdf;

[TestFixture]
public class MdfTests {

  // Builds a minimal flat ISO 9660 image (2048-byte sectors) with one file.
  private static byte[] BuildFlatIso(string fileName, byte[] fileData) {
    const int sectorSize = 2048;
    const int pvdLba = 16;
    const int rootDirLba = 18;
    const int fileLba = 19;

    var totalSectors = fileLba + 2;
    var buf = new byte[totalSectors * sectorSize];

    // Write file data
    fileData.AsSpan().CopyTo(buf.AsSpan(fileLba * sectorSize));

    // Build root directory
    var dirPos = rootDirLba * sectorSize;

    // Dot
    var dot = new byte[34];
    dot[0] = 34; dot[2] = rootDirLba; dot[25] = 0x02; dot[32] = 1; dot[33] = 0x00;
    dot.AsSpan().CopyTo(buf.AsSpan(dirPos)); dirPos += 34;

    // Dotdot
    var dotdot = new byte[34];
    dotdot[0] = 34; dotdot[2] = rootDirLba; dotdot[25] = 0x02; dotdot[32] = 1; dotdot[33] = 0x01;
    dotdot.AsSpan().CopyTo(buf.AsSpan(dirPos)); dirPos += 34;

    // File record
    var isoName = fileName.ToUpperInvariant() + ";1";
    var idLen = (byte)isoName.Length;
    var recLen = (byte)(33 + idLen + ((33 + idLen) % 2));
    var rec = new byte[recLen];
    rec[0] = recLen;
    rec[2] = (byte)fileLba;
    rec[6] = (byte)fileLba;
    var sz = (uint)fileData.Length;
    BitConverter.GetBytes(sz).CopyTo(rec, 10); // LE at offset 10
    rec[32] = idLen;
    System.Text.Encoding.ASCII.GetBytes(isoName).CopyTo(rec, 33);
    rec.AsSpan().CopyTo(buf.AsSpan(dirPos));
    dirPos += recLen;

    // PVD
    var pvd = new byte[sectorSize];
    pvd[0] = 1;
    System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1);
    pvd[6] = 1;
    pvd[156] = 34;
    pvd[158] = rootDirLba;
    pvd[162] = rootDirLba;
    pvd[166] = (byte)(dirPos - rootDirLba * sectorSize);
    pvd[170] = pvd[166];
    pvd[156 + 25] = 0x02;
    pvd[156 + 32] = 1;
    pvd.AsSpan().CopyTo(buf.AsSpan(pvdLba * sectorSize));

    return buf;
  }

  [Test, Category("HappyPath")]
  public void Read_FlatIso_ListsFile() {
    var data = "MDF test content"u8.ToArray();
    var iso = BuildFlatIso("test.txt", data);
    using var ms = new MemoryStream(iso);

    var r = new FileFormat.Mdf.MdfReader(ms);
    Assert.That(r.Entries, Has.Count.GreaterThan(0));
    var file = r.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);
    Assert.That(file!.Size, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Read_FlatIso_ExtractReturnsData() {
    var data = new byte[256];
    Random.Shared.NextBytes(data);
    var iso = BuildFlatIso("payload.bin", data);
    using var ms = new MemoryStream(iso);

    var r = new FileFormat.Mdf.MdfReader(ms);
    var file = r.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);

    var extracted = r.Extract(file!);
    Assert.That(extracted[..data.Length], Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void MdfEntry_Properties() {
    var entry = new FileFormat.Mdf.MdfEntry {
      Name = "SETUP.EXE",
      FullPath = "INSTALL/SETUP.EXE",
      IsDirectory = false,
      Size = 65536,
      StartLba = 30,
    };
    Assert.That(entry.Name, Is.EqualTo("SETUP.EXE"));
    Assert.That(entry.FullPath, Is.EqualTo("INSTALL/SETUP.EXE"));
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(entry.Size, Is.EqualTo(65536));
    Assert.That(entry.StartLba, Is.EqualTo(30));
  }

  [Test, Category("HappyPath")]
  public void EmptyStream_ReturnsNoEntries() {
    using var ms = new MemoryStream(new byte[2352 * 32]);
    var r = new FileFormat.Mdf.MdfReader(ms);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension_Mdf() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("image.mdf");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Mdf));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension_Mds() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("image.mds");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Mdf));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Mdf.MdfFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsThroughReader() {
    var payload = "alcohol-mdf-payload"u8.ToArray();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, payload);
      var d = new FileFormat.Mdf.MdfFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "data.bin", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;

      var r = new FileFormat.Mdf.MdfReader(ms);
      var fileEntry = r.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.StartsWith("DATA"));
      Assert.That(fileEntry, Is.Not.Null);
      Assert.That(r.Extract(fileEntry!)[..payload.Length], Is.EqualTo(payload));
    } finally {
      File.Delete(tmp);
    }
  }
}

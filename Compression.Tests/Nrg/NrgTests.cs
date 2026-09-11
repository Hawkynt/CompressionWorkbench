namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgTests {

  // Builds a minimal NRG v2 image: flat ISO sectors followed by the NER5 footer.
  // The footer is 12 bytes: "NER5" + uint64 BE offset to chunk table (appended after data).
  private static byte[] BuildNrg(string fileName, byte[] fileData) {
    const int sectorSize = 2048;
    const int pvdLba = 16;
    const int rootDirLba = 18;
    const int fileLba = 19;

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

    // NRG v2 footer: "NER5" + uint64 BE (chunk table offset = end of data area)
    var chunkOffset = (ulong)isoBuf.Length;
    var footer = new byte[12];
    footer[0] = (byte)'N'; footer[1] = (byte)'E'; footer[2] = (byte)'R'; footer[3] = (byte)'5';
    footer[4]  = (byte)(chunkOffset >> 56);
    footer[5]  = (byte)(chunkOffset >> 48);
    footer[6]  = (byte)(chunkOffset >> 40);
    footer[7]  = (byte)(chunkOffset >> 32);
    footer[8]  = (byte)(chunkOffset >> 24);
    footer[9]  = (byte)(chunkOffset >> 16);
    footer[10] = (byte)(chunkOffset >> 8);
    footer[11] = (byte)chunkOffset;

    var result = new byte[isoBuf.Length + footer.Length];
    isoBuf.AsSpan().CopyTo(result);
    footer.AsSpan().CopyTo(result.AsSpan(isoBuf.Length));
    return result;
  }

  [Test, Category("HappyPath")]
  public void Read_Nrg_DetectsVersion2Footer() {
    var data = "NRG content"u8.ToArray();
    var nrg = BuildNrg("readme.txt", data);
    using var ms = new MemoryStream(nrg);

    var r = new FileFormat.Nrg.NrgReader(ms);
    Assert.That(r.Version, Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Read_Nrg_ListsFile() {
    var data = "Nero disc data"u8.ToArray();
    var nrg = BuildNrg("file.dat", data);
    using var ms = new MemoryStream(nrg);

    var r = new FileFormat.Nrg.NrgReader(ms);
    Assert.That(r.Entries, Has.Count.GreaterThan(0));
    var file = r.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);
    Assert.That(file!.Size, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Read_Nrg_ExtractReturnsData() {
    var data = new byte[128];
    Random.Shared.NextBytes(data);
    var nrg = BuildNrg("sample.bin", data);
    using var ms = new MemoryStream(nrg);

    var r = new FileFormat.Nrg.NrgReader(ms);
    var file = r.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);

    var extracted = r.Extract(file!);
    Assert.That(extracted[..data.Length], Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Read_NoFooter_VersionIsZero() {
    // Stream with no NRG footer — version should be 0
    using var ms = new MemoryStream(new byte[2352 * 32]);
    var r = new FileFormat.Nrg.NrgReader(ms);
    Assert.That(r.Version, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void NrgEntry_Properties() {
    var entry = new FileFormat.Nrg.NrgEntry {
      Name = "AUTORUN.INF",
      FullPath = "AUTORUN.INF",
      IsDirectory = false,
      Size = 128,
      StartLba = 20,
    };
    Assert.That(entry.Name, Is.EqualTo("AUTORUN.INF"));
    Assert.That(entry.FullPath, Is.EqualTo("AUTORUN.INF"));
    Assert.That(entry.IsDirectory, Is.False);
    Assert.That(entry.Size, Is.EqualTo(128));
    Assert.That(entry.StartLba, Is.EqualTo(20));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("disc.nrg");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Nrg));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new FileFormat.Nrg.NrgFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_HasNer5Footer_AndRoundTrips() {
    var payload = "nero-payload"u8.ToArray();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, payload);
      var d = new FileFormat.Nrg.NrgFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveCreatable)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "data.bin", false)],
        new Compression.Registry.FormatCreateOptions());

      // Verify the NER5 footer was emitted (last 12 bytes: "NER5" + uint64 BE).
      var bytes = ms.ToArray();
      Assert.That(bytes[^12..^8], Is.EqualTo(new byte[] { (byte)'N', (byte)'E', (byte)'R', (byte)'5' }));

      ms.Position = 0;
      var r = new FileFormat.Nrg.NrgReader(ms);
      Assert.That(r.Version, Is.EqualTo(2));
      var fileEntry = r.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.StartsWith("DATA"));
      Assert.That(fileEntry, Is.Not.Null);
      Assert.That(r.Extract(fileEntry!)[..payload.Length], Is.EqualTo(payload));
    } finally {
      File.Delete(tmp);
    }
  }
}

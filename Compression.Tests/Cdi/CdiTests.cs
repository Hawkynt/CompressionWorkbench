using Compression.Registry;

namespace Compression.Tests.Cdi;

[TestFixture]
public class CdiTests {

  // Backward-compatibility fixture for the footer-only profile emitted by older
  // CompressionWorkbench builds.
  private static byte[] BuildLegacyCdi(string fileName, byte[] fileData) {
    const int sectorSize = 2048;
    const int pvdLba = 16;
    const int rootDirLba = 18;
    const int fileLba = 19;
    const uint cdiV3 = 0x80000005;

    var totalSectors = fileLba + 2;
    var isoBuf = new byte[totalSectors * sectorSize];
    fileData.AsSpan().CopyTo(isoBuf.AsSpan(fileLba * sectorSize));

    var dirPos = rootDirLba * sectorSize;
    var dot = new byte[34];
    dot[0] = 34; dot[2] = rootDirLba; dot[25] = 0x02; dot[32] = 1; dot[33] = 0x00;
    dot.AsSpan().CopyTo(isoBuf.AsSpan(dirPos)); dirPos += 34;
    var dotdot = new byte[34];
    dotdot[0] = 34; dotdot[2] = rootDirLba; dotdot[25] = 0x02; dotdot[32] = 1; dotdot[33] = 0x01;
    dotdot.AsSpan().CopyTo(isoBuf.AsSpan(dirPos)); dirPos += 34;

    var isoName = fileName.ToUpperInvariant() + ";1";
    var idLen = (byte)isoName.Length;
    var recLen = (byte)(33 + idLen + ((33 + idLen) % 2));
    var rec = new byte[recLen];
    rec[0] = recLen; rec[2] = fileLba; rec[6] = fileLba;
    BitConverter.GetBytes((uint)fileData.Length).CopyTo(rec, 10);
    rec[32] = idLen;
    System.Text.Encoding.ASCII.GetBytes(isoName).CopyTo(rec, 33);
    rec.AsSpan().CopyTo(isoBuf.AsSpan(dirPos));
    dirPos += recLen;

    var pvd = new byte[sectorSize];
    pvd[0] = 1; System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1); pvd[6] = 1;
    pvd[156] = 34; pvd[158] = rootDirLba; pvd[162] = rootDirLba;
    pvd[166] = (byte)(dirPos - rootDirLba * sectorSize); pvd[170] = pvd[166];
    pvd[156 + 25] = 0x02; pvd[156 + 32] = 1;
    pvd.AsSpan().CopyTo(isoBuf.AsSpan(pvdLba * sectorSize));

    var result = new byte[isoBuf.Length + 8];
    isoBuf.AsSpan().CopyTo(result);
    BitConverter.GetBytes(cdiV3).CopyTo(result, isoBuf.Length);
    return result;
  }

  [Test, Category("HappyPath")]
  public void Read_Cdi_DetectsLegacyV3Footer() {
    var cdi = BuildLegacyCdi("test.txt", "CDI content"u8.ToArray());
    using var ms = new MemoryStream(cdi);
    using var reader = new FileFormat.Cdi.CdiReader(ms);
    Assert.That(reader.CdiVersion, Is.EqualTo(0x80000005u));
  }

  [Test, Category("HappyPath")]
  public void Read_Cdi_ListsAndExtractsFile() {
    var data = "DiscJuggler content"u8.ToArray();
    var cdi = BuildLegacyCdi("readme.txt", data);
    using var ms = new MemoryStream(cdi);
    using var reader = new FileFormat.Cdi.CdiReader(ms);

    var file = reader.Entries.FirstOrDefault(entry => !entry.IsDirectory);
    Assert.That(file, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(file!.Size, Is.EqualTo(data.Length));
      Assert.That(reader.Extract(file), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void Read_NoFooter_VersionIsZero() {
    using var ms = new MemoryStream(new byte[2352 * 32]);
    using var reader = new FileFormat.Cdi.CdiReader(ms);
    Assert.That(reader.CdiVersion, Is.EqualTo(0u));
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
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo("GAME.EXE"));
      Assert.That(entry.FullPath, Is.EqualTo("GAME/GAME.EXE"));
      Assert.That(entry.IsDirectory, Is.False);
      Assert.That(entry.Size, Is.EqualTo(4096));
      Assert.That(entry.StartLba, Is.EqualTo(22));
    });
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("disc.cdi");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Cdi));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsReadWriteAndMaintenanceCapabilities() {
    var descriptor = new FileFormat.Cdi.CdiFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_WritesV35Descriptor_AndRoundTrips() {
    var payload = "discjuggler-payload"u8.ToArray();
    var descriptor = new FileFormat.Cdi.CdiFormatDescriptor();
    using var ms = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      ms,
      [ArchiveInputInfo.InMemory("data.bin", payload)],
      new FormatCreateOptions());

    var bytes = ms.ToArray();
    var version = BitConverter.ToUInt32(bytes, bytes.Length - 8);
    var descriptorLength = BitConverter.ToUInt32(bytes, bytes.Length - 4);
    var descriptorOffset = bytes.Length - descriptorLength;

    Assert.Multiple(() => {
      Assert.That(version, Is.EqualTo(0x80000006u));
      Assert.That(descriptorLength, Is.GreaterThan(8u));
      Assert.That(descriptorOffset, Is.GreaterThan(0));
      Assert.That(BitConverter.ToUInt16(bytes, checked((int)descriptorOffset)), Is.EqualTo(1));
      Assert.That(BitConverter.ToUInt16(bytes, checked((int)descriptorOffset) + 2), Is.EqualTo(1));
    });

    ms.Position = 0;
    var geometry = FileFormat.Cdi.CdiInPlaceModifier.DetectGeometry(ms);
    Assert.That(geometry.DataAreaLength, Is.EqualTo(descriptorOffset));

    ms.Position = 0;
    using var reader = new FileFormat.Cdi.CdiReader(ms, leaveOpen: true);
    var file = reader.Entries.FirstOrDefault(entry => !entry.IsDirectory && entry.Name.Equals("DATA.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.That(file, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(reader.CdiVersion, Is.EqualTo(0x80000006u));
      Assert.That(reader.Extract(file!), Is.EqualTo(payload));
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_AddReplaceRemoveAndPurge_RoundTripFiles() {
    var descriptor = new FileFormat.Cdi.CdiFormatDescriptor();
    var creator = (IArchiveCreatable)descriptor;
    var modifier = (IArchiveModifiable)descriptor;
    using var ms = new MemoryStream();

    creator.Create(ms, [ArchiveInputInfo.InMemory("old.bin", "old"u8)], new FormatCreateOptions());
    modifier.Add(ms, [
      ArchiveInputInfo.InMemory("old.bin", "replacement"u8),
      ArchiveInputInfo.InMemory("new.bin", "new"u8),
    ]);

    Assert.That(ReadFile(ms, "old.bin"), Is.EqualTo("replacement"u8.ToArray()));
    Assert.That(ReadFile(ms, "new.bin"), Is.EqualTo("new"u8.ToArray()));

    modifier.Remove(ms, ["new.bin"]);
    Assert.That(ReadFile(ms, "new.bin"), Is.Null);
    Assert.That(ReadFile(ms, "old.bin"), Is.EqualTo("replacement"u8.ToArray()));

    ((IArchivePurgeable)descriptor).Purge(ms);
    ms.Position = 0;
    using var reader = new FileFormat.Cdi.CdiReader(ms, leaveOpen: true);
    Assert.That(reader.Entries.Where(entry => !entry.IsDirectory), Is.Empty);
  }

  private static byte[]? ReadFile(Stream image, string name) {
    image.Position = 0;
    using var reader = new FileFormat.Cdi.CdiReader(image, leaveOpen: true);
    var entry = reader.Entries.FirstOrDefault(candidate =>
      !candidate.IsDirectory && candidate.FullPath.Equals(name, StringComparison.OrdinalIgnoreCase));
    return entry == null ? null : reader.Extract(entry);
  }
}

using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Mdf;

namespace Compression.Tests.Mdf;

[TestFixture]
public class MdfTests {

  private const int CookedSectorSize = 2048;
  private const int RawSectorSize = 2352;

  // Builds a minimal flat ISO 9660 image (2048-byte sectors) with one file.
  private static byte[] BuildFlatIso(string fileName, byte[] fileData) {
    const int pvdLba = 16;
    const int rootDirLba = 18;
    const int fileLba = 19;

    var totalSectors = fileLba + 2;
    var buf = new byte[totalSectors * CookedSectorSize];

    fileData.AsSpan().CopyTo(buf.AsSpan(fileLba * CookedSectorSize));

    var dirPos = rootDirLba * CookedSectorSize;
    var dot = new byte[34];
    dot[0] = 34; dot[2] = rootDirLba; dot[25] = 0x02; dot[32] = 1; dot[33] = 0x00;
    dot.AsSpan().CopyTo(buf.AsSpan(dirPos)); dirPos += 34;

    var dotdot = new byte[34];
    dotdot[0] = 34; dotdot[2] = rootDirLba; dotdot[25] = 0x02; dotdot[32] = 1; dotdot[33] = 0x01;
    dotdot.AsSpan().CopyTo(buf.AsSpan(dirPos)); dirPos += 34;

    var isoName = fileName.ToUpperInvariant() + ";1";
    var idLen = (byte)isoName.Length;
    var recLen = (byte)(33 + idLen + ((33 + idLen) % 2));
    var rec = new byte[recLen];
    rec[0] = recLen;
    rec[2] = (byte)fileLba;
    rec[6] = (byte)fileLba;
    var sz = (uint)fileData.Length;
    BitConverter.GetBytes(sz).CopyTo(rec, 10);
    rec[32] = idLen;
    System.Text.Encoding.ASCII.GetBytes(isoName).CopyTo(rec, 33);
    rec.AsSpan().CopyTo(buf.AsSpan(dirPos));
    dirPos += recLen;

    var pvd = new byte[CookedSectorSize];
    pvd[0] = 1;
    System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1);
    pvd[6] = 1;
    pvd[156] = 34;
    pvd[158] = rootDirLba;
    pvd[162] = rootDirLba;
    pvd[166] = (byte)(dirPos - rootDirLba * CookedSectorSize);
    pvd[170] = pvd[166];
    pvd[156 + 25] = 0x02;
    pvd[156 + 32] = 1;
    pvd.AsSpan().CopyTo(buf.AsSpan(pvdLba * CookedSectorSize));

    return buf;
  }

  private static byte[] BuildIso(string fileName, byte[] fileData, int headroomSectors = 0) {
    var writer = new FileSystem.Iso.IsoWriter();
    writer.AddFile(fileName, fileData);
    var image = writer.Build();
    if (headroomSectors == 0) return image;
    var withHeadroom = new byte[image.Length + headroomSectors * CookedSectorSize];
    image.CopyTo(withHeadroom, 0);
    return withHeadroom;
  }

  private static byte[] ToRawMode1(byte[] cooked) {
    Assert.That(cooked.Length % CookedSectorSize, Is.Zero);
    using var raw = new MemoryStream();
    var geometry = new MdfInPlaceModifier.SectorGeometry(RawSectorSize, 16);
    for (var lba = 0; lba < cooked.Length / CookedSectorSize; ++lba)
      MdfInPlaceModifier.AppendSector(
        raw,
        lba,
        cooked.AsSpan(lba * CookedSectorSize, CookedSectorSize),
        geometry);
    return raw.ToArray();
  }

  private static byte[] ExtractFile(Stream image, string name) {
    image.Position = 0;
    using var reader = new MdfReader(image, leaveOpen: true);
    var entry = reader.Entries.Single(e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    return reader.Extract(entry);
  }

  [Test, Category("HappyPath")]
  public void Read_FlatIso_ListsFile() {
    var data = "MDF test content"u8.ToArray();
    var iso = BuildFlatIso("test.txt", data);
    using var ms = new MemoryStream(iso);

    using var reader = new MdfReader(ms);
    Assert.That(reader.Entries, Has.Count.GreaterThan(0));
    var file = reader.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);
    Assert.That(file!.Size, Is.EqualTo(data.Length));
  }

  [Test, Category("HappyPath")]
  public void Read_FlatIso_ExtractReturnsData() {
    var data = new byte[256];
    Random.Shared.NextBytes(data);
    var iso = BuildFlatIso("payload.bin", data);
    using var ms = new MemoryStream(iso);

    using var reader = new MdfReader(ms);
    var file = reader.Entries.FirstOrDefault(e => !e.IsDirectory);
    Assert.That(file, Is.Not.Null);

    var extracted = reader.Extract(file!);
    Assert.That(extracted[..data.Length], Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void MdfEntry_Properties() {
    var entry = new MdfEntry {
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
    using var ms = new MemoryStream(new byte[RawSectorSize * 32]);
    using var reader = new MdfReader(ms);
    Assert.That(reader.Entries, Is.Empty);
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
  public void Descriptor_ReportsReadWriteAndMaintenanceCapabilities() {
    IArchiveFormatOperations descriptor = new MdfFormatDescriptor();
    var format = (IFormatDescriptor)descriptor;
    Assert.Multiple(() => {
      Assert.That(format.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(format.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
      Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
      Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
      Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
      Assert.That(descriptor, Is.Not.InstanceOf<IArchiveShrinkable>());
      Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsThroughReader() {
    var payload = "alcohol-mdf-payload"u8.ToArray();
    var descriptor = new MdfFormatDescriptor();
    using var ms = new MemoryStream();
    descriptor.Create(
      ms,
      [ArchiveInputInfo.InMemory("data.bin", payload)],
      new FormatCreateOptions());
    ms.Position = 0;

    using var reader = new MdfReader(ms);
    var fileEntry = reader.Entries.FirstOrDefault(e => !e.IsDirectory && e.Name.StartsWith("DATA"));
    Assert.That(fileEntry, Is.Not.Null);
    Assert.That(reader.Extract(fileEntry!)[..payload.Length], Is.EqualTo(payload));
  }

  [Test, Category("KnownVector")]
  public void Mode1Sector_ZeroPayloadAtLba0_MatchesEcma130Vector() {
    using var image = new MemoryStream();
    var geometry = new MdfInPlaceModifier.SectorGeometry(RawSectorSize, 16);
    MdfInPlaceModifier.AppendSector(image, 0, new byte[CookedSectorSize], geometry);

    var hash = Convert.ToHexString(SHA256.HashData(image.ToArray())).ToLowerInvariant();
    Assert.That(hash, Is.EqualTo("b4f18ab66709c9b3fdef2721cc323e031b6728f3ca6c57b7c435c96189222250"));
  }

  [Test, Category("RoundTrip")]
  public void AddAndReplace_UsesListedIsoFileNamesWithinFixedHeadroom() {
    var original = BuildIso("OLD.TXT", "old"u8.ToArray(), headroomSectors: 8);
    using var image = new MemoryStream(original, writable: true);
    var descriptor = new MdfFormatDescriptor();

    descriptor.Add(image, [ArchiveInputInfo.InMemory("NEW.BIN", "new-data"u8)]);
    Assert.That(ExtractFile(image, "NEW.BIN"), Is.EqualTo("new-data"u8.ToArray()));

    descriptor.Add(image, [ArchiveInputInfo.InMemory("OLD.TXT", "replacement"u8)]);
    Assert.That(ExtractFile(image, "OLD.TXT"), Is.EqualTo("replacement"u8.ToArray()));
    Assert.That(image.Length, Is.EqualTo(original.Length));
  }

  // An add is refused only once it cannot fit even after the track's reclaimable
  // slack has been released, because AddOrReplace trims the logical volume before
  // it checks capacity. So the payload here is larger than the whole track: no
  // amount of trimming can make room for it, and the refusal must leave the image
  // byte-for-byte untouched rather than half-written.
  [Test, Category("Regression")]
  public void Add_LargerThanTheWholeTrack_FailsWithoutMutatingImage() {
    var original = BuildIso("OLD.TXT", "old"u8.ToArray());
    using var image = new MemoryStream((byte[])original.Clone(), writable: true);
    var descriptor = new MdfFormatDescriptor();

    Assert.Throws<IOException>(() =>
      descriptor.Add(image, [ArchiveInputInfo.InMemory("NEW.BIN", new byte[original.Length])]));
    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void Remove_RawMode1_RemovesListedFileAndKeepsPhysicalTrackLength() {
    var raw = ToRawMode1(BuildIso("DELETE.ME", "secret"u8.ToArray(), headroomSectors: 4));
    using var image = new MemoryStream(raw, writable: true);
    var descriptor = new MdfFormatDescriptor();
    var length = image.Length;

    descriptor.Remove(image, ["DELETE.ME"]);

    image.Position = 0;
    using var reader = new MdfReader(image, leaveOpen: true);
    Assert.That(reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name), Does.Not.Contain("DELETE.ME"));
    Assert.That(image.Length, Is.EqualTo(length));
  }

  [Test, Category("RoundTrip")]
  public void Wipe_RawMode1_ZerosUnusedPayloadWithoutChangingTrackLength() {
    var cooked = BuildIso("KEEP.BIN", "keep"u8.ToArray(), headroomSectors: 3);
    var raw = ToRawMode1(cooked);
    using var image = new MemoryStream(raw, writable: true);
    var geometry = MdfInPlaceModifier.DetectGeometry(image);
    var freeLba = checked((int)(cooked.Length / CookedSectorSize - 1));
    MdfInPlaceModifier.WriteSector(image, freeLba, Enumerable.Repeat((byte)0xA5, CookedSectorSize).ToArray(), geometry);
    var beforeLength = image.Length;

    var descriptor = new MdfFormatDescriptor();
    var wiped = descriptor.WipeUnusedSpace(image);

    Span<byte> free = stackalloc byte[CookedSectorSize];
    MdfInPlaceModifier.ReadSector(image, freeLba, free, geometry);
    var freedSector = free.ToArray();
    Assert.Multiple(() => {
      Assert.That(wiped, Is.GreaterThan(0));
      Assert.That(freedSector, Is.All.Zero);
      Assert.That(image.Length, Is.EqualTo(beforeLength));
      Assert.That(ExtractFile(image, "KEEP.BIN"), Is.EqualTo("keep"u8.ToArray()));
    });
  }

  [Test, Category("RoundTrip")]
  public void Purge_RawMode1_LeavesValidEmptyIsoAndStableMdfLength() {
    var raw = ToRawMode1(BuildIso("ERASE.BIN", new byte[6000], headroomSectors: 2));
    using var image = new MemoryStream(raw, writable: true);
    var descriptor = new MdfFormatDescriptor();
    var beforeLength = image.Length;

    descriptor.Purge(image);

    image.Position = 0;
    using var reader = new MdfReader(image, leaveOpen: true);
    Assert.Multiple(() => {
      Assert.That(reader.Entries.Where(e => !e.IsDirectory), Is.Empty);
      Assert.That(image.Length, Is.EqualTo(beforeLength));
    });
  }

  [Test, Category("RoundTrip")]
  public void Defrag_RawMode1_PreservesPayloadAndPhysicalTrackLength() {
    var payload = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
    var raw = ToRawMode1(BuildIso("DATA.BIN", payload, headroomSectors: 4));
    using var image = new MemoryStream(raw, writable: true);
    var descriptor = new MdfFormatDescriptor();
    var beforeLength = image.Length;

    descriptor.Defragment(image);

    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(beforeLength));
      Assert.That(ExtractFile(image, "DATA.BIN"), Is.EqualTo(payload));
    });
  }

  [Test, Category("HappyPath")]
  public void Layout_RawMode1_CoversEveryPhysicalByteConservatively() {
    var raw = ToRawMode1(BuildIso("DATA.BIN", "layout"u8.ToArray(), headroomSectors: 2));
    using var image = new MemoryStream(raw, writable: true);
    var map = MdfLayoutMap.Enumerate(image).OrderBy(e => e.Offset).ToList();

    Assert.That(map, Is.Not.Empty);
    var cursor = 0L;
    foreach (var extent in map) {
      Assert.That(extent.Offset, Is.EqualTo(cursor));
      Assert.That(extent.Length, Is.GreaterThan(0));
      cursor += extent.Length;
    }
    Assert.That(cursor, Is.EqualTo(image.Length));
  }
}

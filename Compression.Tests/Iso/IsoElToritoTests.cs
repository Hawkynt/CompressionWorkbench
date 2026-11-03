using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// El Torito (Bootable CD-ROM Format Specification 1.0): the writer's boot record, catalog and
/// BIOS / UEFI entries, the isolinux boot info table, and <see cref="IsoBoot"/> reading them
/// back; checked against isoinfo and xorriso.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 17d1607 and 952104f.</remarks>
[TestFixture]
public sealed class IsoElToritoTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_eltorito_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static (byte[] Bios, byte[] Efi) BootImages() {
    var bios = new byte[4096];
    new Random(1).NextBytes(bios);
    var efi = new byte[8192];
    new Random(2).NextBytes(efi);
    return (bios, efi);
  }

  private static byte[] BootableImage(byte[] bios, byte[] efi, bool catalogVisible = true) {
    var w = new IsoWriter { EnableRockRidge = true, VolumeIdentifier = "TEST" };
    w.AddFile("boot/isolinux.bin", bios);
    w.AddFile("boot/efi.img", efi);
    if (catalogVisible) {
      w.AddFile("boot/boot.cat", new byte[2048]);
      w.BootCatalogPath = "boot/boot.cat";
    }
    w.BootEntries.Add(new IsoWriter.BootEntry { Platform = 0, SectorCount = 4, ImagePath = "boot/isolinux.bin", PatchBootInfoTable = true });
    w.BootEntries.Add(new IsoWriter.BootEntry { Platform = 0xEF, SectorCount = 16, ImagePath = "boot/efi.img" });
    return w.Build();
  }

  [Test, Category("HappyPath")]
  public void GivenBiosAndUefiEntries_WhenWrittenAndRead_ThenTheEntriesPointAtTheirFiles() {
    var (bios, efi) = BootImages();
    using var s = new MemoryStream(BootableImage(bios, efi));

    var boot = IsoBoot.ReadElTorito(s);

    Assert.That(boot, Is.Not.Null);
    var (entries, catalog) = boot!.Value;
    s.Position = 0;
    using var r = new IsoReader(s, leaveOpen: true);
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => e.Platform), Is.EqualTo(new byte[] { 0, 0xEF }));
      Assert.That(byName["boot/isolinux.bin"].FirstSector, Is.EqualTo(entries[0].LoadRba));
      Assert.That(byName["boot/efi.img"].FirstSector, Is.EqualTo(entries[1].LoadRba));
      Assert.That(byName["boot/boot.cat"].FirstSector, Is.EqualTo(catalog));
      Assert.That(r.Extract(byName["boot/efi.img"]), Is.EqualTo(efi));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenABootInfoTableRequest_WhenWritten_ThenTheTableDescribesTheImageAndTheRestIsUntouched() {
    var (bios, efi) = BootImages();
    using var s = new MemoryStream(BootableImage(bios, efi));
    var (entries, _) = IsoBoot.ReadElTorito(s)!.Value;
    s.Position = 0;
    using var r = new IsoReader(s, leaveOpen: true);

    var patched = r.Extract(r.Entries.Single(e => e.Name == "boot/isolinux.bin"));

    uint checksum = 0;
    for (var i = 64; i + 4 <= patched.Length; i += 4) checksum += BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(i));
    Assert.Multiple(() => {
      Assert.That(IsoBoot.HasBootInfoTable(patched, entries[0].LoadRba), Is.True);
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(16)), Is.EqualTo((uint)bios.Length));
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(20)), Is.EqualTo(checksum));
      Assert.That(patched[..8], Is.EqualTo(bios[..8]));
      Assert.That(patched[64..], Is.EqualTo(bios[64..]));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAHiddenCatalog_WhenWritten_ThenTheCatalogSectorIsValidAndListedNowhere() {
    var (bios, efi) = BootImages();
    var image = BootableImage(bios, efi, catalogVisible: false);
    using var s = new MemoryStream(image);

    var (_, catalog) = IsoBoot.ReadElTorito(s)!.Value;

    s.Position = 0;
    using var r = new IsoReader(s, leaveOpen: true);
    var validation = image.AsSpan((int)catalog * 2048, 32);
    ushort sum = 0;
    for (var i = 0; i < 32; i += 2) sum += BinaryPrimitives.ReadUInt16LittleEndian(validation[i..]);
    Assert.Multiple(() => {
      Assert.That(sum, Is.Zero, "the validation entry's words sum to zero");
      Assert.That(r.Entries.Any(e => e.FirstSector == catalog && !e.IsDirectory), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheBootOptions_WhenCreatedThroughTheDescriptor_ThenTheImageBootsFromTheNamedFiles() {
    var (bios, efi) = BootImages();
    using var output = new MemoryStream();

    new IsoFormatDescriptor().Create(output, [
      ArchiveInputInfo.InMemory("isolinux/isolinux.bin", bios),
      ArchiveInputInfo.InMemory("efi/boot.img", efi),
      ArchiveInputInfo.InMemory("isolinux/boot.cat", new byte[2048]),
    ], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["BootImage"] = "isolinux/isolinux.bin",
        ["EfiBootImage"] = "efi/boot.img",
        ["BootCatalog"] = "isolinux/boot.cat",
      },
    });

    output.Position = 0;
    var (entries, catalog) = IsoBoot.ReadElTorito(output)!.Value;
    output.Position = 0;
    using var r = new IsoReader(output, leaveOpen: true);
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => (e.Platform, e.MediaType, e.SectorCount)), Is.EqualTo(new[] { ((byte)0, (byte)0, (ushort)4), ((byte)0xEF, (byte)0, (ushort)0) }));
      Assert.That(entries[0].LoadRba, Is.EqualTo(byName["isolinux/isolinux.bin"].FirstSector));
      Assert.That(entries[1].LoadRba, Is.EqualTo(byName["efi/boot.img"].FirstSector));
      Assert.That(catalog, Is.EqualTo(byName["isolinux/boot.cat"].FirstSector));
      Assert.That(IsoBoot.HasBootInfoTable(r.Extract(byName["isolinux/isolinux.bin"]), entries[0].LoadRba), Is.True);
    });
  }

  [Test, Category("EdgeCase")]
  public void GivenAnImageWithoutBootRecord_WhenRead_ThenThereAreNoEntriesAndItIsNotHybrid() {
    var w = new IsoWriter();
    w.AddFile("A.TXT", [1]);
    using var s = new MemoryStream(w.Build());

    Assert.Multiple(() => {
      Assert.That(IsoBoot.ReadElTorito(s), Is.Null);
      Assert.That(IsoBoot.IsHybrid(s), Is.False);
      Assert.That(IsoBoot.ReadVolumeId(s), Is.EqualTo("CDROM"));
    });
  }

  [Test, Category("EdgeCase")]
  public void GivenAnMbrPartitionInTheSystemArea_WhenChecked_ThenTheImageIsHybrid() {
    var w = new IsoWriter();
    w.AddFile("A.TXT", [1]);
    var image = w.Build();
    image[510] = 0x55;
    image[511] = 0xAA;
    image[446 + 4] = 0x17;                                                // partition type
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(446 + 12), 64); // sector count

    Assert.That(IsoBoot.IsHybrid(new MemoryStream(image)), Is.True);
  }

  [Test, Category("Exception")]
  public void GivenATruncatedImage_WhenReadForBootEntries_ThenNothingIsReported() {
    Assert.That(IsoBoot.ReadElTorito(new MemoryStream(new byte[2048 * 10])), Is.Null);
  }

  // ── oracles ──────────────────────────────────────────────────────────────────────────────

  [Test, Category("OsIntegration")]
  public void GivenOurBootableImage_WhenIsoinfoReadsIt_ThenItFindsTheElToritoBootEntry() {
    var (bios, efi) = BootImages();
    var path = Path.Combine(this._dir, "boot.iso");
    File.WriteAllBytes(path, BootableImage(bios, efi));

    var d = IsoOracle.Run("isoinfo", $"-d -i {IsoOracle.Path(path)}");

    Assert.Multiple(() => {
      Assert.That(d.Exit, Is.Zero, d.Err);
      Assert.That(d.Out, Does.Contain("El Torito VD version 1 found"));
      Assert.That(d.Out, Does.Contain("Bootid 88 (bootable)"));
      Assert.That(d.Out, Does.Contain("No Emulation Boot"));
      Assert.That(d.Out, Does.Match(@"Nsect\s+4"));
    });
  }

  [Test, Category("OsIntegration")]
  public void GivenOurBootableImage_WhenXorrisoReportsIt_ThenBothEntriesAreThere() {
    var (bios, efi) = BootImages();
    var path = Path.Combine(this._dir, "boot.iso");
    var image = BootableImage(bios, efi);
    File.WriteAllBytes(path, image);
    var (entries, _) = IsoBoot.ReadElTorito(new MemoryStream(image))!.Value;

    var report = IsoOracle.Run("xorriso", $"-indev {IsoOracle.Path(path)} -report_el_torito plain");

    Assert.Multiple(() => {
      Assert.That(report.Exit, Is.Zero, report.Err);
      Assert.That(report.Out, Does.Match($@"El Torito boot img :\s+1\s+BIOS\s+y\s+none\s+0x0000\s+0x00\s+4\s+{entries[0].LoadRba}\b"));
      Assert.That(report.Out, Does.Match($@"El Torito boot img :\s+2\s+UEFI\s+y\s+none\s+0x0000\s+0x00\s+16\s+{entries[1].LoadRba}\b"));
      Assert.That(report.Out, Does.Contain("/boot/boot.cat"));
    });
  }

  [Test, Category("OsIntegration")]
  public void GivenAnImageXorrisoMadeBootable_WhenWeReadIt_ThenWeSeeItsEntries() {
    var tree = Directory.CreateDirectory(Path.Combine(this._dir, "tree", "boot")).FullName;
    var (bios, efi) = BootImages();
    File.WriteAllBytes(Path.Combine(tree, "loader.bin"), bios);
    File.WriteAllBytes(Path.Combine(tree, "efi.img"), efi);
    var path = Path.Combine(this._dir, "theirs.iso");

    var made = IsoOracle.Run("xorriso",
      $"-as mkisofs -R -J -o {IsoOracle.Path(path)} -b boot/loader.bin -c boot/boot.cat -no-emul-boot -boot-load-size 4 -boot-info-table " +
      $"-eltorito-alt-boot -e boot/efi.img -no-emul-boot {IsoOracle.Path(Path.Combine(this._dir, "tree"))}");
    Assert.That(made.Exit, Is.Zero, made.Err);

    using var s = File.OpenRead(path);
    var (entries, catalog) = IsoBoot.ReadElTorito(s)!.Value;
    s.Position = 0;
    using var r = new IsoReader(s, leaveOpen: true);
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.Multiple(() => {
      Assert.That(entries.Select(e => e.Platform), Is.EqualTo(new byte[] { 0, 0xEF }));
      Assert.That(entries[0].LoadRba, Is.EqualTo(byName["boot/loader.bin"].FirstSector));
      Assert.That(entries[1].LoadRba, Is.EqualTo(byName["boot/efi.img"].FirstSector));
      Assert.That(catalog, Is.EqualTo(byName["boot/boot.cat"].FirstSector));
      Assert.That(IsoBoot.HasBootInfoTable(r.Extract(byName["boot/loader.bin"]), entries[0].LoadRba), Is.True);
    });
  }
}

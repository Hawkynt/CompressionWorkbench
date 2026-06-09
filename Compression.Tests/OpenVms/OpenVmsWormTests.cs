using System.Text;
using Compression.Registry;
using FileSystem.OpenVms;

namespace Compression.Tests.OpenVms;

/// <summary>
/// WORM contract tests for <see cref="OpenVmsWriter"/> /
/// <see cref="OpenVmsFormatDescriptor.Create"/>.
/// </summary>
[TestFixture]
public class OpenVmsWormTests {

  // ── Stage 0 — capability advertisement ──────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate_AndOptsIntoIArchiveCreatable() {
    var d = new OpenVmsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "OpenVms descriptor must opt in to IArchiveCreatable to be discoverable as WORM.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  // ── Stage 1 — home block magic at canonical offset ──────────────────

  [Test, Category("HappyPath")]
  public void Build_PlacesDecFileMagic_AtHomeBlockOffsetPlus0x1E8() {
    var image = OpenVmsWriter.Build([("seed.txt", "hi"u8.ToArray())]);
    // Home block at LBN 1 = offset 512; format-string field at +0x1E8 = 488 → 1000
    var fmt = image.AsSpan(1000, 11);
    Assert.That(Encoding.ASCII.GetString(fmt), Is.EqualTo("DECFILE11A "),
      "DECFILE11A magic must land verbatim at byte offset 1000 of the volume.");
  }

  [Test, Category("HappyPath")]
  public void Build_HomeBlockStructureLevel_IsOds2() {
    var image = OpenVmsWriter.Build([("seed.txt", "hi"u8.ToArray())]);
    var hb = OpenVmsHomeBlock.TryParse(image);
    Assert.That(hb.Valid, Is.True);
    Assert.That(hb.StructureLevel, Is.EqualTo(0x0202),
      "Structure level 0x0202 = ODS-2 per Files-11 spec.");
    Assert.That(hb.StructureName, Is.EqualTo("ODS-2"));
    Assert.That(hb.ClusterSize, Is.EqualTo(1));
    Assert.That(hb.IndexBitmapLbn, Is.EqualTo(2u));
    Assert.That(hb.OwnerUic, Is.EqualTo(0x00010001u),
      "Owner UIC [1,1] = system.");
  }

  // ── Stage 2 — round-trip files via the file table ───────────────────

  [Test, Category("HappyPath")]
  public void Build_ThenRead_RoundTripsSingleFile() {
    var payload = "OpenVMS WORM rocks"u8.ToArray();
    var image = OpenVmsWriter.Build([("note.txt", payload)]);
    var ft = OpenVmsFileTable.TryParse(image);
    Assert.That(ft.Entries, Has.Count.EqualTo(1));
    Assert.That(ft.Entries[0].Name, Is.EqualTo("note.txt"));
    Assert.That(ft.Entries[0].Size, Is.EqualTo(payload.LongLength));
    Assert.That(ft.Extract(image, ft.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Build_ThenRead_RoundTripsMultipleFiles() {
    var inputs = new (string, byte[])[] {
      ("alpha.txt", "AAA"u8.ToArray()),
      ("beta.bin",  new byte[] { 0x10, 0x20, 0x30 }),
      ("gamma.dat", Encoding.UTF8.GetBytes(new string('G', 4096))),
    };
    var image = OpenVmsWriter.Build(inputs, volumeLabel: "WORMVOL");
    var ft = OpenVmsFileTable.TryParse(image);
    Assert.That(ft.Entries.Count, Is.EqualTo(inputs.Length));
    for (var i = 0; i < inputs.Length; i++) {
      Assert.That(ft.Entries[i].Name, Is.EqualTo(inputs[i].Item1));
      Assert.That(ft.Extract(image, ft.Entries[i]), Is.EqualTo(inputs[i].Item2));
    }

    // Home block also surfaces the volume label.
    var hb = OpenVmsHomeBlock.TryParse(image);
    Assert.That(hb.VolumeLabel, Does.Contain("WORMVOL"));
  }

  // ── Stage 3 — descriptor.Create end-to-end ──────────────────────────

  [Test, Category("HappyPath")]
  public void DescriptorCreate_ListAndExtract_RoundTrip() {
    var d = new OpenVmsFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("a.txt", "alpha"u8.ToArray()),
      ArchiveInputInfo.InMemory("data/b.bin", new byte[] { 1, 2, 3, 4 }),
    };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.disk"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("home_block.bin"));
    Assert.That(names, Does.Contain("a.txt"));
    Assert.That(names, Does.Contain("data/b.bin"));

    var outDir = Path.Combine(Path.GetTempPath(), "ovms_worm_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);

      Assert.That(File.ReadAllText(Path.Combine(outDir, "a.txt")),
        Is.EqualTo("alpha"));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "data", "b.bin")),
        Is.EqualTo(new byte[] { 1, 2, 3, 4 }));

      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("structure_level=0x0202"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── Stage 4 — determinism ───────────────────────────────────────────

  [Test, Category("Equivalence")]
  public void Build_TwiceWithSameInputs_ProducesByteIdenticalImage() {
    var inputs = new (string, byte[])[] {
      ("a.txt", "AAA"u8.ToArray()),
      ("b.txt", "BBB"u8.ToArray()),
    };
    var first = OpenVmsWriter.Build(inputs, volumeLabel: "DETERM");
    var second = OpenVmsWriter.Build(inputs, volumeLabel: "DETERM");
    Assert.That(second, Is.EqualTo(first),
      "OpenVms WORM emission must be deterministic.");
  }

  // ── Stage 5 — capacity boundaries ───────────────────────────────────

  [Test, Category("Boundary")]
  public void Build_FileTable_OverflowsCleanly() {
    // File table region is 14 blocks × 512 = 7168 bytes. Each entry costs
    // 8+8+2+nameLen. With 200-byte names that's ~218 bytes per entry; 35
    // entries fit, 80 must overflow.
    var manyFiles = new List<(string, byte[])>(80);
    for (var i = 0; i < 80; i++)
      manyFiles.Add(($"file_{i:D3}_{new string('x', 200)}", new byte[] { (byte)i }));
    Assert.That(() => OpenVmsWriter.Build(manyFiles),
      Throws.TypeOf<InvalidOperationException>().With.Message.Contains("file table overflows"));
  }

  [Test, Category("Boundary")]
  public void AddFile_RejectsEmptyName() {
    using var w = new OpenVmsWriter(new MemoryStream());
    Assert.That(() => w.AddFile("", "x"u8.ToArray()),
      Throws.TypeOf<ArgumentException>().With.Message.Contains("empty"));
  }

  [Test, Category("Boundary")]
  public void AddFile_RejectsOverlongName() {
    using var w = new OpenVmsWriter(new MemoryStream());
    Assert.That(() => w.AddFile(new string('q', 300), Array.Empty<byte>()),
      Throws.TypeOf<ArgumentException>().With.Message.Contains("255"));
  }

  // ── Stage 6 — Empty volume still has a valid home block ─────────────

  [Test, Category("HappyPath")]
  public void Build_EmptyVolume_StillHasValidHomeBlock() {
    var image = OpenVmsWriter.Build([]);
    var hb = OpenVmsHomeBlock.TryParse(image);
    Assert.That(hb.Valid, Is.True);
    Assert.That(hb.StructureLevel, Is.EqualTo(0x0202));
    var ft = OpenVmsFileTable.TryParse(image);
    Assert.That(ft.Entries, Is.Empty);
  }
}

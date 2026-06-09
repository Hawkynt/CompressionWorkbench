using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.AdvFs;

namespace Compression.Tests.AdvFs;

/// <summary>
/// WORM contract tests for <see cref="AdvFsWriter"/> / <see cref="AdvFsFormatDescriptor.Create"/>.
/// </summary>
[TestFixture]
public class AdvFsWormTests {

  // ── Stage 0 — capability advertisement ──────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanCreate_AndOptsIntoIArchiveCreatable() {
    var d = new AdvFsFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "AdvFs descriptor must opt in to IArchiveCreatable to be discoverable as WORM.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "AdvFs must advertise CanCreate.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True,
      "AdvFs must advertise SupportsMultipleEntries.");
  }

  // ── Stage 1 — detection cookie survives the emit ────────────────────

  [Test, Category("HappyPath")]
  public void Build_PlacesDetectionCookie_AtRbmtPageOffset() {
    var image = AdvFsWriter.Build([("hello.txt", "world"u8.ToArray())]);
    Assert.That(image.Length, Is.GreaterThan((int)AdvFsWriter.RbmtPageOffset + 16),
      "Emitted image must reach past the RBMT page offset.");
    var cookieAtOffset = image.AsSpan((int)AdvFsWriter.RbmtPageOffset, AdvFsReader.DetectionCookie.Length);
    Assert.That(cookieAtOffset.SequenceEqual(AdvFsReader.DetectionCookie), Is.True,
      "Detection cookie must land verbatim at RBMT page offset 131072.");
  }

  // ── Stage 2 — round-trip file payloads ──────────────────────────────

  [Test, Category("HappyPath")]
  public void Build_ThenRead_RoundTripsSingleFile() {
    var payload = "the quick brown fox"u8.ToArray();
    var image = AdvFsWriter.Build([("note.txt", payload)]);
    using var ms = new MemoryStream(image);
    var r = new AdvFsReader(ms);
    Assert.That(r.Valid, Is.True, "Reader must accept the writer's emission as a valid AdvFS image.");
    Assert.That(r.FileTableEntries, Has.Count.EqualTo(1));
    var entry = r.FileTableEntries[0];
    Assert.That(entry.Name, Is.EqualTo("note.txt"));
    Assert.That(entry.Size, Is.EqualTo(payload.LongLength));
    var got = r.ExtractFile(entry);
    Assert.That(got, Is.EqualTo(payload), "Extracted bytes must exactly match the written payload.");
  }

  [Test, Category("HappyPath")]
  public void Build_ThenRead_RoundTripsMultipleFiles() {
    var inputs = new (string, byte[])[] {
      ("alpha.txt", "ALPHA"u8.ToArray()),
      ("beta.bin",  new byte[] { 1, 2, 3, 4, 5 }),
      ("gamma.dat", Encoding.UTF8.GetBytes(new string('G', 8192))),  // crosses 8 KB to exercise data-area math
      ("dirA/nested.cfg", "k=v\n"u8.ToArray()),
    };
    var image = AdvFsWriter.Build(inputs);
    using var ms = new MemoryStream(image);
    var r = new AdvFsReader(ms);
    Assert.That(r.Valid, Is.True);
    Assert.That(r.FileTableEntries.Count, Is.EqualTo(inputs.Length));

    for (var i = 0; i < inputs.Length; i++) {
      var (name, data) = inputs[i];
      var entry = r.FileTableEntries[i];
      Assert.That(entry.Name, Is.EqualTo(name));
      Assert.That(entry.Size, Is.EqualTo(data.LongLength));
      Assert.That(r.ExtractFile(entry), Is.EqualTo(data),
        $"Payload mismatch for '{name}' — multi-entry round-trip failed.");
    }
  }

  // ── Stage 3 — descriptor.Create end-to-end ──────────────────────────

  [Test, Category("HappyPath")]
  public void DescriptorCreate_ListAndExtract_RoundTripsThroughDescriptor() {
    var d = new AdvFsFormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("readme.md",  "# AdvFS WORM\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("data/q.bin", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
    };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToHashSet();
    Assert.That(names, Does.Contain("FULL.advfs"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("rbmt_page0.bin"));
    Assert.That(names, Does.Contain("readme.md"));
    Assert.That(names, Does.Contain("data/q.bin"));

    // Extract to a temp dir and verify the per-file bytes.
    var outDir = Path.Combine(Path.GetTempPath(), "advfs_worm_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);

      var readmePath = Path.Combine(outDir, "readme.md");
      Assert.That(File.Exists(readmePath), Is.True);
      Assert.That(File.ReadAllText(readmePath), Is.EqualTo("# AdvFS WORM\n"));

      var qPath = Path.Combine(outDir, "data", "q.bin");
      Assert.That(File.Exists(qPath), Is.True);
      Assert.That(File.ReadAllBytes(qPath), Is.EqualTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }

  // ── Stage 4 — determinism (no host clock leak) ──────────────────────

  [Test, Category("Equivalence")]
  public void Build_TwiceWithSameInputs_ProducesByteIdenticalImage() {
    var inputs = new (string, byte[])[] {
      ("a.txt", "AAA"u8.ToArray()),
      ("b.txt", "BBB"u8.ToArray()),
    };
    var first = AdvFsWriter.Build(inputs, volumeTag: "DETERM.VOL1");
    var second = AdvFsWriter.Build(inputs, volumeTag: "DETERM.VOL1");
    Assert.That(second, Is.EqualTo(first),
      "AdvFs WORM emission must be deterministic — same inputs → byte-identical image.");
  }

  // ── Stage 5 — capacity boundaries ───────────────────────────────────

  [Test, Category("Boundary")]
  public void Build_RbmtFileTable_OverflowsCleanly() {
    // RBMT page is 8 KB. After the 132-byte header prefix + 16-byte eyecatcher +
    // 4-byte file count, each entry costs 8+8+2 + nameBytes. A 200-byte name
    // means ~218 B per entry; 36 entries fill the page. 80 entries must overflow.
    var manyFiles = new List<(string, byte[])>(80);
    for (var i = 0; i < 80; i++)
      manyFiles.Add(($"file_{i:D3}_{new string('x', 200)}", new byte[] { (byte)i }));
    Assert.That(() => AdvFsWriter.Build(manyFiles),
      Throws.TypeOf<InvalidOperationException>().With.Message.Contains("file table overflows"));
  }

  [Test, Category("Boundary")]
  public void AddFile_RejectsOverlongName() {
    using var w = new AdvFsWriter(new MemoryStream());
    var huge = new string('q', 300);
    Assert.That(() => w.AddFile(huge, Array.Empty<byte>()),
      Throws.TypeOf<ArgumentException>().With.Message.Contains("255"));
  }

  [Test, Category("Boundary")]
  public void AddFile_RejectsEmptyName() {
    using var w = new AdvFsWriter(new MemoryStream());
    Assert.That(() => w.AddFile("", "x"u8.ToArray()),
      Throws.TypeOf<ArgumentException>().With.Message.Contains("empty"));
  }

  // ── Stage 6 — emitted header still parses cleanly into metadata.ini ─

  [Test, Category("HappyPath")]
  public void Build_MetadataIniSurfacesParsedFields() {
    var d = new AdvFsFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("x.txt", "hi"u8.ToArray())], new FormatCreateOptions());

    var outDir = Path.Combine(Path.GetTempPath(), "advfs_meta_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(outDir);
    try {
      ms.Position = 0;
      d.Extract(ms, outDir, null, null);
      var meta = File.ReadAllText(Path.Combine(outDir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
      Assert.That(meta, Does.Contain("on_disk_version=4"));
      Assert.That(meta, Does.Contain("vd_index=1"));
      Assert.That(meta, Does.Contain("vd_count=1"));
      Assert.That(meta, Does.Contain("vd_meta_blk_cnt=16"));
      Assert.That(meta, Does.Contain("volume_tag=CWB-ADVFS"));
    } finally {
      try { Directory.Delete(outDir, recursive: true); } catch { /* ignore */ }
    }
  }
}

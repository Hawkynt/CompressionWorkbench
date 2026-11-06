#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Cdi;
using FileFormat.Mdf;
using FileFormat.Nrg;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// The CD-image containers (MDF, CDI, NRG) read their ISO 9660 track through the shared ISO
/// reader, so they list the Rock Ridge and Joliet names a plain ISO lists, whatever the
/// sector framing; and their edits keep those names. The ISO tracks come from our writer and
/// from xorriso; <c>isoinfo</c> and <c>xorriso</c> read the tracks the containers carry.
/// </summary>
[TestFixture]
public sealed class CdImageRockRidgeTests {

  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_cdrr_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static Dictionary<string, byte[]> Tree() => new(StringComparer.Ordinal) {
    ["Docs/Quarterly Report 2026 (final).txt"] = "quarterly\n"u8.ToArray(),
    ["Readme.md"] = "# readme\n"u8.ToArray(),
    ["src/deep/Some.Long.File.Name.cs"] = Pattern(9000, 1),
    ["Mixed Case Folder/inner file.bin"] = Pattern(70_000, 2),
  };

  private static byte[] Pattern(int size, int seed) {
    var d = new byte[size];
    new Random(seed).NextBytes(d);
    return d;
  }

  private static byte[] OurIso(bool rockRidge, bool joliet = true) {
    var options = new FormatCreateOptions {
      FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["RockRidge"] = rockRidge.ToString(), ["Joliet"] = joliet.ToString() },
    };
    return IsoFormatDescriptor.BufferedWriterFor([.. Tree().Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))], options).Build();
  }

  // ── Containers around an ISO track ─────────────────────────────────────

  public enum Container { MdfCooked, MdfRaw, Cdi, Nrg, NrgRaw }

  /// <summary>Raw Mode 1 sectors (ECMA-130): sync, header with BCD address and mode 1, user data at 16.</summary>
  private static byte[] RawMode1(byte[] iso) {
    var sectors = (iso.Length + 2047) / 2048;
    var raw = new byte[sectors * 2352];
    for (var s = 0; s < sectors; ++s) {
      var at = s * 2352;
      raw[at] = 0;
      raw.AsSpan(at + 1, 10).Fill(0xFF);
      var address = s + 150;
      static byte Bcd(int v) => (byte)(v / 10 << 4 | v % 10);
      raw[at + 12] = Bcd(address / 4500);
      raw[at + 13] = Bcd(address / 75 % 60);
      raw[at + 14] = Bcd(address % 75);
      raw[at + 15] = 1;
      iso.AsSpan(s * 2048, Math.Min(2048, iso.Length - s * 2048)).CopyTo(raw.AsSpan(at + 16));
    }
    return raw;
  }

  private static byte[] Wrap(byte[] iso, Container container) {
    switch (container) {
      case Container.MdfCooked: return iso;
      case Container.MdfRaw: return RawMode1(iso);
      case Container.Cdi: {
        using var ms = new MemoryStream();
        ms.Write(new byte[CdiDescriptor.StandardPregapSectors * 2048]);
        ms.Write(iso);
        ms.Write(new byte[(2048 - iso.Length % 2048) % 2048]);
        ms.Write(CdiDescriptor.BuildSingleTrackV35(checked((uint)((iso.Length + 2047) / 2048))));
        return ms.ToArray();
      }
      default: {
        using var ms = new MemoryStream();
        var raw = container == Container.NrgRaw;
        NrgWriter.Write(ms, new NrgDiscDefinition([
          new NrgSessionDefinition([new NrgTrackDefinition(raw ? NrgTrackMode.Mode1Raw : NrgTrackMode.Mode1, raw ? RawMode1(iso) : iso)]),
        ]));
        return ms.ToArray();
      }
    }
  }

  /// <summary>The files the container's reader lists, with their bytes.</summary>
  private static Dictionary<string, byte[]> Read(byte[] image, Container container) {
    using var stream = new MemoryStream(image);
    return container switch {
      Container.MdfCooked or Container.MdfRaw => ReadAll(new MdfReader(stream), r => r.Entries, (r, e) => (e.FullPath, e.IsDirectory, () => r.Extract(e))),
      Container.Cdi => ReadAll(new CdiReader(stream), r => r.Entries, (r, e) => (e.FullPath, e.IsDirectory, () => r.Extract(e))),
      _ => ReadAll(new NrgReader(stream), r => r.Entries, (r, e) => (e.FullPath, e.IsDirectory, () => r.Extract(e))),
    };
  }

  private static Dictionary<string, byte[]> ReadAll<TReader, TEntry>(TReader reader, Func<TReader, IEnumerable<TEntry>> entries,
      Func<TReader, TEntry, (string Path, bool IsDirectory, Func<byte[]> Bytes)> view) where TReader : IDisposable {
    using (reader)
      return entries(reader).Select(e => view(reader, e)).Where(v => !v.IsDirectory)
        .ToDictionary(v => v.Path, v => v.Bytes(), StringComparer.Ordinal);
  }

  private static void AssertTree(Dictionary<string, byte[]> got, Dictionary<string, byte[]> expected, string what) {
    Assert.That(got.Keys, Is.EquivalentTo(expected.Keys), $"{what}: names");
    foreach (var (name, data) in expected)
      if (got.TryGetValue(name, out var bytes))
        Assert.That(bytes, Is.EqualTo(data), $"{what}: {name}");
  }

  // ── Reading ─────────────────────────────────────────────────────────────

  [TestCase(Container.MdfCooked, true)]
  [TestCase(Container.MdfRaw, true)]
  [TestCase(Container.Cdi, true)]
  [TestCase(Container.Nrg, true)]
  [TestCase(Container.NrgRaw, true)]
  [TestCase(Container.MdfRaw, false)]
  [TestCase(Container.Cdi, false)]
  [TestCase(Container.Nrg, false)]
  [Category("RoundTrip")]
  public void GivenARockRidgeOrJolietTrack_WhenTheContainerIsRead_ThenItListsTheRealNames(Container container, bool rockRidge) {
    var image = Wrap(OurIso(rockRidge), container);
    AssertTree(Read(image, container), Tree(), $"{container}, {(rockRidge ? "Rock Ridge" : "Joliet")}");
  }

  [TestCase(Container.MdfRaw)]
  [TestCase(Container.Cdi)]
  [TestCase(Container.Nrg)]
  [Category("RoundTrip")]
  public void GivenAPlainIso9660Track_WhenTheContainerIsRead_ThenItListsTheIso9660Identifiers(Container container) {
    var image = Wrap(OurIso(rockRidge: false, joliet: false), container);
    var got = Read(image, container);
    Assert.That(got.Keys, Has.Member("README.MD").And.All.Matches<string>(k => k == k.ToUpperInvariant()));
    Assert.That(got.Values.Select(Convert.ToHexString), Is.EquivalentTo(Tree().Values.Select(Convert.ToHexString)));
  }

  [TestCase(Container.Cdi)]
  [TestCase(Container.Nrg)]
  [Category("RoundTrip")]
  public void GivenATrackShorterThanAFile_WhenTheFileIsExtracted_ThenItFailsInsteadOfReturningZeros(Container container) {
    var iso = OurIso(rockRidge: true);
    using var stream = new MemoryStream(iso);
    var big = new IsoReader(stream).Entries.Single(e => e.Name == "Mixed Case Folder/inner file.bin");
    var cut = iso.AsSpan(0, (int)(big.FirstSector + 2) * 2048).ToArray(); // the file runs past the track's end
    var image = Wrap(cut, container);
    Assert.Throws<InvalidDataException>(() => Read(image, container));
  }

  // ── Editing keeps the names ─────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void GivenAnMdf_WhenFilesAreAddedAndRemovedInFolders_ThenTheRealNamesAreKept() {
    var mdf = new MdfFormatDescriptor();
    using var image = new MemoryStream();
    mdf.Create(image, [.. Tree().Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))], new FormatCreateOptions());

    mdf.Add(image, [ArchiveInputInfo.InMemory("Docs/New Folder/An Added Long Name.txt", "added"u8.ToArray())]);
    mdf.Remove(image, ["src/deep/Some.Long.File.Name.cs"]);

    var expected = Tree();
    expected.Remove("src/deep/Some.Long.File.Name.cs");
    expected["Docs/New Folder/An Added Long Name.txt"] = "added"u8.ToArray();
    AssertTree(Read(image.ToArray(), Container.MdfCooked), expected, "MDF after edits");
    Assert.Throws<FileNotFoundException>(() => mdf.Remove(image, ["not/there.txt"]));
  }

  [TestCase(Container.Cdi)]
  [TestCase(Container.Nrg)]
  [Category("RoundTrip")]
  public void GivenACdiOrNrg_WhenAFileIsAddedInAFolder_ThenTheRealNamesAreKept(Container container) {
    IArchiveFormatOperations ops = container == Container.Cdi ? new CdiFormatDescriptor() : new NrgFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, [.. Tree().Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value))], new FormatCreateOptions());

    ((IArchiveModifiable)ops).Add(image, [ArchiveInputInfo.InMemory("Docs/New Folder/An Added Long Name.txt", "added"u8.ToArray())]);

    var expected = Tree();
    expected["Docs/New Folder/An Added Long Name.txt"] = "added"u8.ToArray();
    AssertTree(Read(image.ToArray(), container), expected, $"{container} after an add");
  }

  // ── Third-party tracks and third-party readers ─────────────────────────

  [TestCase(Container.MdfCooked)]
  [TestCase(Container.MdfRaw)]
  [TestCase(Container.Cdi)]
  [TestCase(Container.Nrg)]
  [TestCase(Container.NrgRaw)]
  [Category("OsIntegration")]
  [Category("ExternalFsInterop")]
  public void GivenATrackXorrisoMade_WhenTheContainerIsRead_ThenItListsXorrisosNames(Container container) {
    RequireTool("xorriso");
    var src = Path.Combine(this._dir, "src");
    foreach (var (name, data) in Tree()) {
      var path = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllBytes(path, data);
    }
    var isoPath = Path.Combine(this._dir, "x.iso");
    var made = FsInteropToolbox.RunWsl($"xorriso -as mkisofs -R -J -o {FsInteropToolbox.WinToWsl(isoPath)} {FsInteropToolbox.WinToWsl(src)} 2>&1");
    Assert.That(made.ExitCode, Is.Zero, made.StdOut + made.StdErr);

    AssertTree(Read(Wrap(File.ReadAllBytes(isoPath), container), container), Tree(), $"{container} around xorriso's track");
  }

  [TestCase(Container.MdfCooked)]
  [TestCase(Container.Cdi)]
  [TestCase(Container.Nrg)]
  [Category("OsIntegration")]
  [Category("ExternalFsInterop")]
  public void GivenAContainerWeCreatedAndEdited_WhenIsoinfoAndXorrisoReadItsTrack_ThenTheyListTheNamesWeList(Container container) {
    RequireTool("isoinfo");
    RequireTool("xorriso");
    var inputs = Tree().Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList();
    var added = ArchiveInputInfo.InMemory("Docs/New Folder/An Added Long Name.txt", "added"u8.ToArray());
    using var image = new MemoryStream();
    byte[] track;
    switch (container) {
      case Container.MdfCooked: {
        var mdf = new MdfFormatDescriptor();
        mdf.Create(image, inputs, new FormatCreateOptions());
        mdf.Add(image, [added]);
        track = image.ToArray();
        break;
      }
      case Container.Cdi: {
        var cdi = new CdiFormatDescriptor();
        cdi.Create(image, inputs, new FormatCreateOptions());
        cdi.Add(image, [added]);
        image.Position = 0;
        using var reader = new CdiReader(image, leaveOpen: true);
        var data = reader.Tracks.Single(t => t.IsData);
        track = [.. Enumerable.Range(0, data.DataSectorCount).SelectMany(i => reader.ReadTrackSector(data, i))];
        break;
      }
      default: {
        var nrg = new NrgFormatDescriptor();
        nrg.Create(image, inputs, new FormatCreateOptions());
        nrg.Add(image, [added]);
        image.Position = 0;
        using var reader = new NrgReader(image, leaveOpen: true);
        track = reader.ExtractTrack(reader.Tracks.Single());
        break;
      }
    }
    var ours = Read(image.ToArray(), container).Keys.ToList();
    var isoPath = Path.Combine(this._dir, "track.iso");
    File.WriteAllBytes(isoPath, track);

    var listing = FsInteropToolbox.RunWsl($"isoinfo -R -l -i {FsInteropToolbox.WinToWsl(isoPath)} 2>&1");
    var xorriso = FsInteropToolbox.RunWsl($"xorriso -indev {FsInteropToolbox.WinToWsl(isoPath)} -find / -type f 2>&1");
    Assert.Multiple(() => {
      Assert.That(ours, Has.Member("Docs/New Folder/An Added Long Name.txt"));
      foreach (var name in ours) {
        Assert.That(listing.StdOut, Does.Contain(name[(name.LastIndexOf('/') + 1)..]), $"isoinfo -R lists {name}");
        Assert.That(xorriso.StdOut, Does.Contain($"'/{name}'"), $"xorriso lists {name}");
      }
    });
  }

  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL not available.");
    if (!FsInteropToolbox.WslHasTool(tool)) Assert.Ignore($"'{tool}' not available in WSL.");
  }
}

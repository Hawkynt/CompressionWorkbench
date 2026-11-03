using System.Diagnostics;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Rock Ridge written by <see cref="IsoWriter"/> (NM names of any length through CE
/// continuation areas, PX modes, TF times, SP/ER in the root) and read back by our reader
/// and by the reference readers. New images keep folders, empty ones included, instead of
/// flattening every name to its leaf.
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 17d1607, 8a63bf8, e2ff9da, 5a41abd.</remarks>
[TestFixture]
public sealed class IsoRockRidgeWriterTests {
  private static readonly DateTime When = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_isorr_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch (IOException) { }
  }

  private static byte[] Build(Action<IsoWriter> fill, bool joliet = true) {
    var w = new IsoWriter { EnableRockRidge = true, EnableJoliet = joliet, VolumeIdentifier = "TEST" };
    fill(w);
    return w.Build();
  }

  private static Dictionary<string, IsoEntry> Read(byte[] iso, out IsoReader reader) {
    reader = new IsoReader(new MemoryStream(iso));
    return reader.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
  }

  // A name past what one directory record holds: it goes into a continuation area.
  private static readonly string VeryLongName = string.Concat(Enumerable.Repeat("very-long-rock-ridge-name-", 9)) + "end.txt";

  [Test, Category("HappyPath")]
  public void GivenRockRidge_WhenWrittenAndRead_ThenNamesModesTimesAndEmptyFoldersRoundTrip() {
    var name = "Ünïcode and spaces — much longer than the sixty-four characters of Joliet.txt";
    var iso = Build(w => {
      w.AddDirectory("empty folder", 0x1C0, When);
      w.AddFile("dir/" + name, [1, 2, 3]);
      w.SetMetadata("dir/" + name, 0x1ED, When);
    });

    var byName = Read(iso, out var r);
    using (r) {
      Assert.Multiple(() => {
        Assert.That(byName["empty folder"].IsDirectory, Is.True);
        Assert.That(byName["empty folder"].UnixMode & 0xFFF, Is.EqualTo(0x1C0));
        Assert.That(byName["dir/" + name].UnixMode & 0xFFF, Is.EqualTo(0x1ED));
        Assert.That(byName["dir/" + name].LastModified, Is.EqualTo(When));
        Assert.That(r.Extract(byName["dir/" + name]), Is.EqualTo(new byte[] { 1, 2, 3 }));
      });
    }
  }

  [Test, Category("HappyPath")]
  public void GivenNamesThatDifferOnlyInCase_WhenWritten_ThenBothSurvive() {
    var iso = Build(w => {
      w.AddFile("dir/Same.txt", [4]);
      w.AddFile("dir/same.txt", [5]);
    });

    var byName = Read(iso, out var r);
    using (r) {
      Assert.Multiple(() => {
        Assert.That(r.Extract(byName["dir/Same.txt"]), Is.EqualTo(new byte[] { 4 }));
        Assert.That(r.Extract(byName["dir/same.txt"]), Is.EqualTo(new byte[] { 5 }));
      });
    }
  }

  [Test, Category("Boundary")]
  public void GivenANameTooLongForItsRecord_WhenWritten_ThenItIsKeptWholeInAContinuationArea() {
    Assert.That(Encoding.UTF8.GetByteCount(VeryLongName), Is.GreaterThan(200), "the name must not fit one record");
    var iso = Build(w => {
      w.AddFile("deep/" + VeryLongName, [7]);
      w.AddDirectory("deep/" + VeryLongName[..^4] + " folder");
    });

    var byName = Read(iso, out var r);
    using (r) {
      Assert.Multiple(() => {
        Assert.That(r.Extract(byName["deep/" + VeryLongName]), Is.EqualTo(new byte[] { 7 }));
        Assert.That(byName["deep/" + VeryLongName[..^4] + " folder"].IsDirectory, Is.True);
      });
    }
  }

  [TestCase(1)]
  [TestCase(100)]
  [TestCase(255)]
  [Category("Boundary")]
  public void GivenNamesAroundTheRecordLimit_WhenWritten_ThenEachReadsBackWhole(int length) {
    var name = new string('n', length);
    var iso = Build(w => w.AddFile(name, [1]));

    var byName = Read(iso, out var r);
    using (r)
      Assert.That(byName.Keys, Is.EquivalentTo(new[] { name }));
  }

  [Test, Category("HappyPath")]
  public void GivenManyLongNames_WhenWritten_ThenContinuationAreasSpanSectorsCorrectly() {
    var names = Enumerable.Range(0, 40).Select(i => $"{i:D2}-{VeryLongName}").ToArray();
    var iso = Build(w => {
      foreach (var n in names) w.AddFile(n, Encoding.ASCII.GetBytes(n[..2]));
    });

    var byName = Read(iso, out var r);
    using (r) {
      Assert.That(byName.Keys, Is.EquivalentTo(names));
      Assert.That(r.Extract(byName[names[^1]]), Is.EqualTo("39"u8.ToArray()));
    }
  }

  [Test, Category("HappyPath")]
  public void GivenNoRockRidge_WhenWritten_ThenTheImageCarriesNoSuspEntries() {
    var w = new IsoWriter();
    w.AddFile("A.TXT", [1]);

    var iso = w.Build();

    Assert.Multiple(() => {
      Assert.That(Encoding.ASCII.GetString(iso).Contains("RRIP_1991A", StringComparison.Ordinal), Is.False);
      using var r = new IsoReader(new MemoryStream(iso));
      Assert.That(r.Entries.Single().Name, Is.EqualTo("A.TXT"));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenRockRidgeAndJoliet_WhenRead_ThenRockRidgeNamesWin() {
    var name = new string('j', 80) + ".txt"; // longer than Joliet's 64 characters
    var iso = Build(w => w.AddFile(name, [1]), joliet: true);

    var byName = Read(iso, out var r);
    using (r)
      Assert.That(byName.Keys, Is.EquivalentTo(new[] { name }));
  }

  [Test, Category("HappyPath")]
  public void GivenAFolderTree_WhenCreatedThroughTheDescriptor_ThenFoldersAndLongNamesAreKept() {
    var name = "A long name with Case, longer than the old 8.3 limit.txt";
    Directory.CreateDirectory(Path.Combine(this._dir, "pack", "sub", "empty"));
    File.WriteAllText(Path.Combine(this._dir, "pack", "sub", name), "deep");
    File.WriteAllText(Path.Combine(this._dir, "top.txt"), "top");
    var iso = Path.Combine(this._dir, "new.iso");

    ArchiveOperations.Create(iso, [
      .. ArchiveInput.Resolve([Path.Combine(this._dir, "pack")]),
      new ArchiveInput(Path.Combine(this._dir, "top.txt"), "top.txt"),
    ], new CompressionOptions(), FormatDetector.Format.Iso);

    using var r = new IsoReader(File.OpenRead(iso));
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.Multiple(() => {
      Assert.That(byName.Keys, Is.SupersetOf(new[] { "top.txt", "pack", "pack/sub", "pack/sub/empty", "pack/sub/" + name }));
      Assert.That(r.Extract(byName["pack/sub/" + name]), Is.EqualTo("deep"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAnImageWithAnEmptyFolder_WhenExtracted_ThenTheFolderExists() {
    var iso = Build(w => {
      w.AddDirectory("keep/empty");
      w.AddFile("a.txt", [1]);
    });
    var outDir = Path.Combine(this._dir, "x");

    new IsoFormatDescriptor().Extract(new MemoryStream(iso), outDir, null, null);

    Assert.That(Directory.Exists(Path.Combine(outDir, "keep", "empty")), Is.True);
  }

  [TestCase("Cdi", ".cdi")]
  [TestCase("Mdf", ".mdf")]
  [TestCase("Nrg", ".nrg")]
  [TestCase("BinCue", ".bin")]
  [Category("HappyPath")]
  public void GivenAFolderTree_WhenACdImageIsCreated_ThenItKeepsTheFolders(string format, string extension) {
    Directory.CreateDirectory(Path.Combine(this._dir, "Pack", "Sub", "empty"));
    File.WriteAllText(Path.Combine(this._dir, "Pack", "Sub", "b.txt"), "deep");
    var image = Path.Combine(this._dir, "disc" + extension);

    ArchiveOperations.Create(image, ArchiveInput.Resolve([Path.Combine(this._dir, "Pack")]), new CompressionOptions(),
      Enum.Parse<FormatDetector.Format>(format));

    // The CD image readers show the ISO 9660 names of the primary tree (upper case).
    var names = ArchiveOperations.List(image, null).Select(e => e.Name.Replace('\\', '/').Trim('/').ToLowerInvariant()).ToList();
    Assert.That(names, Is.SupersetOf(new[] { "pack", "pack/sub", "pack/sub/empty", "pack/sub/b.txt" }), string.Join(", ", names));
  }

  // ── oracles ──────────────────────────────────────────────────────────────────────────────

  [Test, Category("OsIntegration")]
  public void GivenOurRockRidgeImage_WhenIsoinfoListsIt_ThenItSeesTheNamesAndModes() {
    var path = Path.Combine(this._dir, "rr.iso");
    File.WriteAllBytes(path, Build(w => {
      w.AddDirectory("Folder With Spaces", 0x1E8, When); // 0750
      w.AddFile("Folder With Spaces/" + VeryLongName, [1, 2]);
      w.SetMetadata("Folder With Spaces/" + VeryLongName, 0x1A4, When);
    }));

    var descriptors = IsoOracle.Run("isoinfo", $"-d -i {IsoOracle.Path(path)}");
    var listing = IsoOracle.Run("isoinfo", $"-R -l -i {IsoOracle.Path(path)}");

    Assert.Multiple(() => {
      Assert.That(descriptors.Exit, Is.Zero, descriptors.Err);
      Assert.That(descriptors.Out, Does.Contain("Rock Ridge signatures version 1 found"));
      Assert.That(listing.Out, Does.Contain("Folder With Spaces"));
      Assert.That(listing.Out, Does.Contain(VeryLongName));
      Assert.That(listing.Out, Does.Match(@"dr-?w?x?r-x---"));
    });
  }

  [Test, Category("OsIntegration")]
  public void GivenOurRockRidgeImage_WhenXorrisoExtractsIt_ThenNamesModesAndContentMatch() {
    var path = Path.Combine(this._dir, "rr.iso");
    var content = Encoding.UTF8.GetBytes("Rock Ridge content\n");
    File.WriteAllBytes(path, Build(w => {
      w.AddDirectory("empty", 0x1C0, When);
      w.AddFile("sub/" + VeryLongName, content);
      w.SetMetadata("sub/" + VeryLongName, 0x1ED, When);
      w.AddFile("Mixed Case.TXT", content);
    }));
    var outDir = Path.Combine(this._dir, "xorriso-out");

    var listing = IsoOracle.Run("xorriso", $"-indev {IsoOracle.Path(path)} -find / -exec lsdl --");
    var extract = IsoOracle.Run("osirrox", $"-indev {IsoOracle.Path(path)} -extract / {IsoOracle.Path(outDir)}");

    Assert.Multiple(() => {
      Assert.That(listing.Exit, Is.Zero, listing.Err);
      Assert.That(listing.Out, Does.Contain(VeryLongName));
      Assert.That(listing.Out, Does.Match(@"-rwxr-xr-x .*" + System.Text.RegularExpressions.Regex.Escape(VeryLongName)));
      Assert.That(listing.Out, Does.Match(@"drwx------ .*'?/empty"));
      Assert.That(extract.Exit, Is.Zero, extract.Err);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "sub", VeryLongName)), Is.EqualTo(content));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "Mixed Case.TXT")), Is.EqualTo(content));
      Assert.That(Directory.Exists(Path.Combine(outDir, "empty")), Is.True);
    });
  }

  [Test, Category("OsIntegration")]
  public void GivenAnImageXorrisoWroteWithRockRidgeAndJoliet_WhenWeReadIt_ThenRockRidgeNamesModesAndTimesWin() {
    var tree = Directory.CreateDirectory(Path.Combine(this._dir, "tree")).FullName;
    var longName = new string('x', 90) + ".txt"; // past Joliet's 64 characters
    var file = Path.Combine(tree, longName);
    File.WriteAllText(file, "theirs");
    File.SetLastWriteTimeUtc(file, When);
    var path = Path.Combine(this._dir, "theirs.iso");

    var made = IsoOracle.Run("xorriso",
      $"-outdev {IsoOracle.Path(path)} -joliet on -map {IsoOracle.Path(tree)} / -chmod 0640 /{longName} -- -commit");
    Assert.That(made.Exit, Is.Zero, made.Err);

    using var r = new IsoReader(File.OpenRead(path));
    var entry = r.Entries.Single(e => !e.IsDirectory);
    Assert.Multiple(() => {
      Assert.That(entry.Name, Is.EqualTo(longName));
      Assert.That(entry.UnixMode & 0xFFF, Is.EqualTo(0x1A0));
      Assert.That(entry.LastModified, Is.EqualTo(When));
      Assert.That(r.Extract(entry), Is.EqualTo("theirs"u8.ToArray()));
    });
  }
}

/// <summary>Runs an ISO reference tool natively on Linux or through WSL elsewhere; ignores the test when it is missing.</summary>
internal static class IsoOracle {
  public static string Path(string hostPath) => OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(hostPath) : hostPath;

  public static (string Out, string Err, int Exit) Run(string tool, string args) {
    if (OperatingSystem.IsWindows()) {
      if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"'{tool}' is needed in WSL for this oracle (apt install xorriso genisoimage).");
      var (o, e, x) = FsInteropToolbox.RunWsl($"{tool} {args}");
      return (o, e, x);
    }
    var psi = new ProcessStartInfo("/bin/sh", ["-c", $"command -v {tool} >/dev/null || exit 127; exec {tool} {args}"]) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode == 127) Assert.Ignore($"'{tool}' is not installed.");
    return (stdout.Result, stderr, p.ExitCode);
  }
}

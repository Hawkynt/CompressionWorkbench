using System.Reflection;
using System.Security.Cryptography;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// Archives made by registered StuffIt installs (ssokolow/stuffit-test-files, CC0; see
/// StuffIt/ReferenceVectors/README.md): every fork must decode to the source file it was made from,
/// and The Unarchiver's unar (the reference open-source reader) must agree with us.
/// </summary>
[TestFixture]
public class StuffItCorpusTests {

  /// <summary>SHA-256 and size of each source file's data and resource fork (from the corpus' sources/ and their AppleDouble files).</summary>
  private static readonly Dictionary<string, (string Data, int DataSize, string Rsrc, int RsrcSize)> Sources = new() {
    ["Test Image"] = ("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", 0, "4b8175653903645616d9e07627957ae0dba4c7ac3b3e9aa6afc8e07144dcfbb0", 9134),
    ["Test Text"] = ("9734aef6d3788ba985e78f7b3785dc4817e770be92a4e5e57e64a92cc9c2fc25", 11, "5f0c7e77ac2430be40532730665ea27f0cf1088ac049e0c06851d62085b87315", 332),
    ["testfile.PICT"] = ("318d71cd4d027c6bec6917af3ddc3b7df0ec8b07031045a9cdd9052b94c7782e", 2694, "011604ad448ef4451081d04bd395c2a974cab637877fb64b45e62ebe39bc452e", 44549),
    ["testfile.txt"] = ("b645efee0ed710034959eae942277a750d08687c30bcf0e9ec6ea7641527462f", 12, "f788dcd5313a531a27fc62a9b4c951a6653ef11b49f2262ee0796f72c5564b0a", 332),
    ["testfile.jpg"] = ("e514232511df1a4f4221a75c27523518c3c62a2fe6470fa56e430364428eecd1", 220, "", 0),
    ["testfile.png"] = ("fdda20984cc1591419ec4583e24e72e4dba39d0b96608253f853a2dfb238ad1a", 87, "", 0),
  };

  private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

  // classic SIT! 4.5 = method 13; StuffIt 5 format from 6.5.1/7.0 Mac = method 15 (+ stored), 7.0 Windows = folder + method 15.
  [TestCase("testfile.stuffit45_dlx.mac9.sit", false, 13)]
  [TestCase("testfile.stuffit651_dlx.mac9.sit", true, 15)]
  [TestCase("testfile.stuffit7_dlx.mac9.sit", true, 15)]
  [TestCase("testfile.stuffit7.win.sit", true, 15)]
  [Category("ThemVsUs")]
  public void GivenArchiveMadeByStuffIt_WhenEveryForkIsExtracted_ThenItMatchesTheSourceFile(string vector, bool sit5, int method) {
    using var reader = new StuffItReader(new MemoryStream(LoadVector(vector)));
    Assert.That(reader.IsStuffIt5, Is.EqualTo(sit5));

    var files = reader.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Is.Not.Empty);
    Assert.That(files.Any(e => e.DataMethod == method || e.ResourceMethod == method), $"no fork uses method {method}");
    foreach (var e in files) {
      var leaf = e.FileName[(e.FileName.LastIndexOf('/') + 1)..];
      Assert.That(Sources.ContainsKey(leaf), $"unexpected entry {e.FileName}");
      var (data, dataSize, rsrc, rsrcSize) = Sources[leaf];
      // StuffIt for Windows converts the Mac text file's CR line ending to LF (unar agrees).
      if (vector.Contains(".win.", StringComparison.Ordinal) && leaf == "testfile.txt")
        data = "b2f51cd17b3cbe77f091f887d91110164a2cb5a5a9ebe828c44d655c83dca8eb";

      var d = reader.Extract(e);
      Assert.That(d.Length, Is.EqualTo(dataSize), e.FileName);
      Assert.That(Sha(d), Is.EqualTo(data), e.FileName + " data fork");

      // The Windows build stores no resource forks.
      if (e.HasResourceFork) {
        var r = reader.ExtractResourceFork(e);
        Assert.That(r.Length, Is.EqualTo(rsrcSize), e.FileName);
        Assert.That(Sha(r), Is.EqualTo(rsrc), e.FileName + " resource fork");
      }
    }
  }

  [Test, Category("ThemVsUs")]
  public void GivenWindowsStuffIt7Archive_WhenListed_ThenTheFolderAndItsFilesKeepTheirPaths() {
    var d = new StuffItFormatDescriptor();
    var names = d.List(new MemoryStream(LoadVector("testfile.stuffit7.win.sit")), null).Select(e => (e.Name, e.IsDirectory)).ToList();

    Assert.That(names, Does.Contain(("sources", true)));
    Assert.That(names, Does.Contain(("sources/testfile.txt", false)));
    Assert.That(names, Does.Contain(("sources/testfile.jpg", false)));
    Assert.That(names, Does.Contain(("sources/testfile.png", false)));
  }

  [Test, Category("ThemVsUs")]
  public void GivenMacArchive_WhenListedThroughTheDescriptor_ThenResourceForksAppearAsRsrcEntries() {
    var d = new StuffItFormatDescriptor();
    var entries = d.List(new MemoryStream(LoadVector("testfile.stuffit45_dlx.mac9.sit")), null);

    Assert.That(entries.Select(e => e.Name), Does.Contain("testfile.PICT.rsrc"));
    Assert.That(entries.Single(e => e.Name == "Test Image.rsrc").OriginalSize, Is.EqualTo(9134));
    Assert.That(entries.Select(e => e.Name), Does.Not.Contain("Test Image")); // empty data fork, resource only
    Assert.That(entries.Single(e => e.Name == "testfile.PICT").Method, Is.EqualTo("LZ+Huffman"));
  }

  [Test, Category("Exception")]
  public void GivenDamagedMethod13Fork_WhenExtracted_ThenTheCrcRefusesIt() {
    var bytes = LoadVector("testfile.stuffit45_dlx.mac9.sit");
    using (var probe = new StuffItReader(new MemoryStream(bytes))) {
      var pict = probe.Entries.Single(e => e.FileName == "testfile.PICT");
      bytes[pict.DataForkOffset + pict.CompressedDataSize / 2] ^= 0x20;
    }

    using var reader = new StuffItReader(new MemoryStream(bytes));
    var e = reader.Entries.Single(x => x.FileName == "testfile.PICT");
    Assert.That(() => reader.Extract(e), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenDamagedArsenicFork_WhenExtracted_ThenItIsRefused() {
    var bytes = LoadVector("testfile.stuffit7_dlx.mac9.sit");
    using (var probe = new StuffItReader(new MemoryStream(bytes))) {
      var pict = probe.Entries.Single(e => e.FileName == "testfile.PICT");
      bytes[pict.DataForkOffset + pict.CompressedDataSize / 2] ^= 0x20;
    }

    using var reader = new StuffItReader(new MemoryStream(bytes));
    var e = reader.Entries.Single(x => x.FileName == "testfile.PICT");
    Assert.That(() => reader.Extract(e), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("Exception")]
  public void GivenDamagedClassicEntryHeader_WhenOpened_ThenTheHeaderCrcRefusesIt() {
    var bytes = LoadVector("testfile.stuffit45_dlx.mac9.sit");
    bytes[22 + 5] ^= 0x01; // inside the first entry's name

    Assert.That(() => new StuffItReader(new MemoryStream(bytes)), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("ExternalInterop")]
  public void GivenEveryReferenceArchive_WhenUnarExtractsIt_ThenItsDataForksMatchOurs() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sit_unar_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      foreach (var vector in new[] { "testfile.stuffit45_dlx.mac9.sit", "testfile.stuffit651_dlx.mac9.sit", "testfile.stuffit7.win.sit" }) {
        var path = Path.Combine(dir, vector);
        File.WriteAllBytes(path, LoadVector(vector));
        var outDir = Path.Combine(dir, vector + ".out");
        var r = FsInteropToolbox.RunWsl($"unar -q -f -k hidden -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
        Assert.That(r.ExitCode, Is.EqualTo(0), $"unar failed on {vector}:\n{r.StdOut}\n{r.StdErr}");

        using var reader = new StuffItReader(File.OpenRead(path));
        foreach (var e in reader.Entries.Where(x => !x.IsDirectory && x.DataForkSize > 0)) {
          var theirs = Path.Combine(outDir, e.FileName.Replace('/', Path.DirectorySeparatorChar));
          Assert.That(File.Exists(theirs), $"unar produced no {e.FileName} from {vector}");
          Assert.That(reader.Extract(e), Is.EqualTo(File.ReadAllBytes(theirs)), $"{vector}: {e.FileName}");
        }
      }
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Any further real archives (raw or MacBinary-wrapped .sit) in the directory named by
  /// <c>CWB_STUFFIT_SAMPLES</c> are decoded and compared with unar, fork by fork. Used for samples
  /// whose licence keeps them out of the repository (e.g. the dexvert StuffIt set, which carries
  /// methods 2 and 3).
  /// </summary>
  [Test, Category("ExternalInterop")]
  public void GivenLocalSampleArchives_WhenDecoded_ThenEveryDataForkMatchesUnar() {
    var samples = Environment.GetEnvironmentVariable("CWB_STUFFIT_SAMPLES");
    if (string.IsNullOrEmpty(samples) || !Directory.Exists(samples)) Assert.Ignore("CWB_STUFFIT_SAMPLES is not set.");
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");

    var methods = new SortedSet<int>();
    var compared = 0;
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_sit_local_{Guid.NewGuid():N}");
    try {
      foreach (var file in Directory.EnumerateFiles(samples)) {
        var bytes = File.ReadAllBytes(file);
        var offset = bytes.Length > 132 && bytes.AsSpan(128, 4).SequenceEqual("SIT!"u8) ? 128 : 0;
        if (!bytes.AsSpan(offset).StartsWith("SIT!"u8) && !bytes.AsSpan(offset).StartsWith("StuffIt (c)1997-"u8)) continue;
        var raw = Path.Combine(tmp, Path.GetFileName(file) + ".raw.sit");
        Directory.CreateDirectory(tmp);
        File.WriteAllBytes(raw, bytes[offset..]);
        var outDir = Path.Combine(tmp, Path.GetFileName(file) + ".out");
        var r = FsInteropToolbox.RunWsl($"unar -q -f -k hidden -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(raw)}'");
        Assert.That(r.ExitCode, Is.EqualTo(0), $"unar failed on {file}:\n{r.StdErr}");

        using var reader = new StuffItReader(new MemoryStream(bytes, offset, bytes.Length - offset));
        foreach (var e in reader.Entries.Where(x => !x.IsDirectory && x.DataForkSize > 0 && !x.IsEncrypted)) {
          var theirs = Path.Combine(outDir, e.FileName.Replace('/', Path.DirectorySeparatorChar));
          if (!File.Exists(theirs)) continue; // unar renames names the host cannot hold
          Assert.That(reader.Extract(e), Is.EqualTo(File.ReadAllBytes(theirs)), $"{file}: {e.FileName} (method {e.DataMethod})");
          methods.Add(e.DataMethod);
          ++compared;
        }
      }
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
    Assert.That(compared, Is.GreaterThan(0), "no StuffIt archive found in CWB_STUFFIT_SAMPLES");
    TestContext.Out.WriteLine($"compared {compared} data forks; methods {string.Join(',', methods)}");
  }

  [Test, Category("ExternalInterop")]
  public void GivenArchiveFromOurWriter_WhenUnarExtractsIt_ThenStoredAndRleForksMatch() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar (The Unarchiver) is not installed.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sit_w_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var runs = Enumerable.Range(0, 5000).Select(i => (byte)(i / 300 % 3 == 0 ? 0x90 : i / 100)).ToArray(); // long runs incl. 0x90 -> RLE
      var noise = Enumerable.Range(0, 3000).Select(i => (byte)(i * 131 + (i >> 3))).ToArray();          // incompressible -> stored
      var path = Path.Combine(dir, "ours.sit");
      using (var f = File.Create(path))
      using (var w = new StuffItWriter(f, leaveOpen: true)) {
        w.AddFile("runs.bin", runs);
        w.AddFile("noise.bin", noise);
        w.AddFile("empty.txt", []);
      }
      using (var r = new StuffItReader(File.OpenRead(path)))
        Assert.That(r.Entries.Single(e => e.FileName == "runs.bin").DataMethod, Is.EqualTo(1));

      var outDir = Path.Combine(dir, "out");
      var x = FsInteropToolbox.RunWsl($"unar -q -f -k hidden -D -o '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(path)}'");
      Assert.That(x.ExitCode, Is.EqualTo(0), $"unar rejected our archive:\n{x.StdOut}\n{x.StdErr}");
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "runs.bin")), Is.EqualTo(runs));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "noise.bin")), Is.EqualTo(noise));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "empty.txt")), Is.Empty);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  private static byte[] LoadVector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"StuffItVectors.{name}")
      ?? throw new InvalidOperationException($"missing embedded vector {name}");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using Compression.Registry;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// Third-party proof for the Media Based Catalog the editor writes: ntbkup (William T. Kranz's
/// NTBackup reader, GPL, run only as an external program) reads a backup's catalog with
/// <c>-c</c> — the Set Map's set name and user, and from the File/Directory Detail every folder
/// and every file with its Format Logical Address — and extracts the files with <c>-x</c>.
/// After an edit, both must show the edited backup: the catalog the new tree with every file at
/// the block where its FILE DBLK now is, the extraction the right bytes.
/// </summary>
/// <remarks>
/// ntbkup runs from <c>CWB_NTBKUP</c> or the PATH. Its source is <c>msbksrc.lzh</c> from Kranz's
/// msbackup page (fpns.net/willy, archived); MinGW builds it with
/// <c>gcc -x c -std=gnu89 NTBKUP.C NTTREE.C</c>. The NTBackup sample is the one
/// <see cref="BkfMtftarOracleTests"/> describes (<c>CWB_BKF_SAMPLE</c>).
/// </remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
public sealed class BkfNtbkupOracleTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_ntbkup_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  [Test]
  public void GivenTheNtbackupSampleWeEdited_WhenNtbkupReadsItsCatalogAndExtracts_ThenBothShowTheEdit() {
    var ntbkup = RequireNtbkup();
    using var image = new MemoryStream();
    image.Write(File.ReadAllBytes(BkfCatalogTests.RequireSample()));
    BkfEditor.AddFile(image, "os/winnt50C/added.txt", "added by an edit"u8.ToArray());
    BkfEditor.Remove(image, "os/winnt50C/hal.dll");

    this.AssertNtbkupAgrees(ntbkup, image.ToArray(), "edited.bkf");
  }

  [Test]
  public void GivenACataloguedTwoSetBackupWeEdited_WhenNtbkupReadsIt_ThenEverySetShowsItsFiles() {
    var ntbkup = RequireNtbkup();
    var m = new MtfFixture(mediaBasedCatalog: 1);
    m.Set(1, s => s.Volb("C:").Dirb("\0", security: true).File("one.txt", "first set"u8.ToArray(), security: true));
    m.Set(2, s => s.Volb("C:").Dirb("\0").File("two.txt", "second set"u8.ToArray()).Dirb("docs\0").File("doc.txt", "doc"u8.ToArray()));
    using var image = new MemoryStream();
    image.Write(m.Build());
    BkfEditor.AddFile(image, "set-02/docs/new.txt", "new in set two"u8.ToArray());
    BkfEditor.Remove(image, "set-02/two.txt");

    this.AssertNtbkupAgrees(ntbkup, image.ToArray(), "two-sets.bkf");
  }

  [Test]
  public void GivenABackupWeCreated_WhenNtbkupExtractsIt_ThenTheBytesMatch() {
    var ntbkup = RequireNtbkup();
    using var image = new MemoryStream();
    new BkfFormatDescriptor().Create(image, [
      ArchiveInputInfo.InMemory("root.txt", "root"u8.ToArray()),
      ArchiveInputInfo.InMemory("docs/inner.bin", Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray()),
    ], new FormatCreateOptions());
    var path = this.Save("created.bkf", image.ToArray());
    var outDir = Directory.CreateDirectory(Path.Combine(this._dir, "x-created")).FullName;
    var (exit, output) = ntbkup(path, "-x", outDir);
    Assert.That(exit, Is.Zero, output);
    Assert.That(File.ReadAllBytes(Path.Combine(outDir, "inner.bin")), Is.EqualTo(Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray()), output);
    Assert.That(File.ReadAllText(Path.Combine(outDir, "root.txt")), Is.EqualTo("root"), output);
  }

  /// <summary>
  /// ntbkup's catalog listing names exactly the folders and files our reader lists, each file at
  /// the Format Logical Address of its FILE DBLK; and what it extracts matches our extraction.
  /// </summary>
  private void AssertNtbkupAgrees(Func<string, string, string, (int, string)> ntbkup, byte[] bytes, string name) {
    var path = this.Save(name, bytes);
    var (exit, catalog) = ntbkup(path, "-c", this._dir);
    Assert.That(exit, Is.Zero, catalog);

    using var stream = new MemoryStream(bytes);
    var media = MtfMedia.Read(stream, keepData: false);
    using var reader = new BkfReader(new MemoryStream(bytes));
    var expectedFiles = new List<(string Name, long Size, ulong Fla)>();
    var expectedFolders = new List<string>();
    foreach (var set in media.Sets) {
      var ssetAt = set.Sset.Offset;
      foreach (var b in set.Blocks) {
        if (b.Type == "FILE")
          expectedFiles.Add((b.String(84), (long)BitConverter.ToUInt64(b.Dblk, 12), (ulong)((b.Offset - ssetAt) / 1024)));
        else if (b.Type == "DIRB")
          expectedFolders.Add("\\" + string.Concat(b.String(80).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => p + "\\")));
      }
    }

    var files = Regex.Matches(catalog, @"^FILE:\s+(\d+) @ blk =\s+(\d+)\s+\S+\s+\S+\s+\S+\s+(.+?)\s*$", RegexOptions.Multiline)
      .Select(m => (m.Groups[3].Value, long.Parse(m.Groups[1].Value), ulong.Parse(m.Groups[2].Value))).ToList();
    var folders = Regex.Matches(catalog, @"^DIRB: (.+?)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
    Assert.Multiple(() => {
      Assert.That(files, Is.EqualTo(expectedFiles), $"ntbkup -c files (name, size, block):\n{catalog}");
      Assert.That(folders, Is.EqualTo(expectedFolders), $"ntbkup -c folders:\n{catalog}");
    });

    var outDir = Directory.CreateDirectory(Path.Combine(this._dir, "x-" + Path.GetFileNameWithoutExtension(name))).FullName;
    var (xExit, xOut) = ntbkup(path, "-x", outDir);
    Assert.That(xExit, Is.Zero, xOut);
    foreach (var entry in reader.Entries.Where(e => !e.IsDirectory)) {
      var flat = Path.Combine(outDir, entry.Name[(entry.Name.LastIndexOf('/') + 1)..]);
      Assert.That(File.Exists(flat), Is.True, $"ntbkup -x wrote {entry.Name}:\n{xOut}");
      Assert.That(File.ReadAllBytes(flat), Is.EqualTo(reader.Extract(entry)), entry.Name);
    }
  }

  private string Save(string name, byte[] bytes) {
    var path = Path.Combine(this._dir, name);
    File.WriteAllBytes(path, bytes);
    return path;
  }

  /// <summary>Runs ntbkup on a backup with one option, in the given working folder (where -x writes).</summary>
  private static Func<string, string, string, (int, string)> RequireNtbkup() {
    var tool = Environment.GetEnvironmentVariable("CWB_NTBKUP");
    if (string.IsNullOrEmpty(tool) || !File.Exists(tool)) Assert.Ignore("CWB_NTBKUP does not name an ntbkup executable.");
    return (backup, option, workDir) => {
      var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir };
      psi.ArgumentList.Add(backup);
      psi.ArgumentList.Add(option);
      using var p = Process.Start(psi)!;
      var stdout = p.StandardOutput.ReadToEndAsync();
      var stderr = p.StandardError.ReadToEndAsync();
      if (!p.WaitForExit(120_000)) {
        p.Kill(true);
        return (-1, "ntbkup timed out");
      }
      return (p.ExitCode, stdout.Result + stderr.Result);
    };
  }
}

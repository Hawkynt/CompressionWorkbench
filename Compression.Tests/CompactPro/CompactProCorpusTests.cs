using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FileFormat.CompactPro;

namespace Compression.Tests.CompactPro;

/// <summary>
/// Large real Compact Pro archives against The Unarchiver. The archives come from Info-Mac
/// mirrors and are not committed (redistribution terms vary per package): drop the BinHex-decoded
/// <c>.cpt</c> files into <c>test-corpus/compactpro/</c>. Their LZH forks run past one 0x1FFF0-unit
/// block, which no committed fixture does; every data and resource fork must match unar's.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class CompactProCorpusTests {

  [Test]
  public void RealArchives_InTestCorpus_MatchUnarAcrossLzhBlocks() {
    var corpus = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "test-corpus", "compactpro"));
    if (!Directory.Exists(corpus) || !Directory.EnumerateFiles(corpus, "*.cpt").Any())
      Assert.Ignore($"No real .cpt files under {corpus}.");
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL not available.");
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar is not installed in WSL.");

    var script = FsInteropToolbox.WinToWsl(Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "CompactPro", "unar-manifest.py"));
    var forks = 0;
    var multiBlock = 0;
    var mismatches = new List<string>();
    foreach (var file in Directory.EnumerateFiles(corpus, "*.cpt").Order()) {
      // unar extracts inside Linux, so Mac names with ':' or trailing dots survive intact
      var run = FsInteropToolbox.RunWsl($"python3 -I {script} {FsInteropToolbox.WinToWsl(file)}");
      Assert.That(run.ExitCode, Is.Zero, $"{file}: unar failed\n{run.StdOut}{run.StdErr}");
      var unar = run.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').Split('\t'))
        .ToDictionary(p => Encoding.UTF8.GetString(Convert.FromHexString(p[0])), p => p[1], StringComparer.Ordinal);

      var reader = new CompactProReader(File.OpenRead(file));
      foreach (var e in reader.Entries.Where(e => !e.IsDirectory && !e.IsEncrypted)) {
        CompactProCodec.LzhBlocksRead = 0;
        var (data, resource) = reader.ExtractForks(e);
        var blocks = CompactProCodec.LzhBlocksRead;
        void Compare(string name, byte[] fork) {
          ++forks;
          // a '/' inside a Mac name: we list it as ':' (the Mac convention), unar writes '_'
          if (!unar.TryGetValue(name, out var sha) && !unar.TryGetValue(name.Replace(':', '_'), out sha)) mismatches.Add($"{Path.GetFileName(file)}: unar wrote no '{name}'");
          else if (sha != Convert.ToHexStringLower(SHA256.HashData(fork))) mismatches.Add($"{Path.GetFileName(file)}: '{name}' differs");
        }
        if (data.Length > 0) Compare(e.FileName, data);
        if (resource.Length > 0) Compare(e.FileName + ".rsrc", resource);
        // every LZH fork reads one code-table set per block it spans
        if (blocks > (e.DataForkLzh ? 1 : 0) + (e.ResourceForkLzh ? 1 : 0)) ++multiBlock;
      }
    }
    TestContext.Out.WriteLine($"{forks} forks compared, {multiBlock} entries with LZH streams longer than one block");
    Assert.That(mismatches, Is.Empty);
    Assert.That(forks, Is.GreaterThan(0));
    Assert.That(multiBlock, Is.GreaterThan(0), "no archive in the corpus has an LZH stream that crosses a block boundary");
  }

  /// <summary>The resource-fork entry (id 2) of an AppleDouble file.</summary>
  private static byte[] AppleDoubleResourceFork(byte[] file) {
    if (file.Length < 26 || BinaryPrimitives.ReadUInt32BigEndian(file) != 0x00051607) return file;
    var count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(24));
    for (var i = 0; i < count; ++i) {
      var at = 26 + i * 12;
      if (BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at)) != 2) continue;
      var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 4));
      var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8));
      return file.AsSpan(offset, length).ToArray();
    }
    return [];
  }
}

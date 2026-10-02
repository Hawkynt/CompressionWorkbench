using Compression.Registry;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

/// <summary>
/// External witness for bucket-generation reuse. A self-roundtrip can miss a
/// generation mismatch when alloc, bucket_gens, pointers and backpointers all
/// share the same bug; the real bcachefs checker is the authority here.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public sealed class BcacheFsBucketGenerationExternalTests {

  [Test]
  public void ReusedNonzeroGeneration_PassesBcachefsFsck() {
    RequireCapableChecker();

    var path = Path.Combine(Path.GetTempPath(), $"cwb_bcachefs_gen_{Guid.NewGuid():N}.img");
    try {
      var descriptor = new BcacheFsFormatDescriptor();
      using (var image = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
        descriptor.Create(image,
          [ArchiveInputInfo.InMemory("first.bin", Payload(12_345, 17))],
          new FormatCreateOptions());

        image.Position = 0;
        descriptor.Remove(image, ["first.bin"]);

        image.Position = 0;
        descriptor.Add(image,
          [ArchiveInputInfo.InMemory("replacement.bin", Payload(23_456, 29))]);
        image.Flush(flushToDisk: true);
      }

      var result = FsInteropToolbox.RunWsl($"bcachefs fsck -n {FsInteropToolbox.WinToWsl(path)}");
      TestContext.Out.WriteLine($"bcachefs fsck exit={result.ExitCode}\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
      Assert.That(result.ExitCode, Is.EqualTo(0),
        $"bcachefs fsck rejected the reused-generation image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }

  [Test]
  public void DefragmentingOverReusedBuckets_PassesBcachefsFsck() {
    RequireCapableChecker();

    var path = Path.Combine(Path.GetTempPath(), $"cwb_bcachefs_gendefrag_{Guid.NewGuid():N}.img");
    try {
      var descriptor = new BcacheFsFormatDescriptor();
      var kept = new Dictionary<string, byte[]>(StringComparer.Ordinal);
      using (var image = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) {
        var inputs = new List<ArchiveInputInfo>();
        for (var i = 0; i < 8; ++i) {
          var payload = Payload(30_000 + i * 2_900, i + 3);
          inputs.Add(ArchiveInputInfo.InMemory($"G{i:D2}.BIN", payload));
          kept[$"G{i:D2}.BIN"] = payload;
        }
        descriptor.Create(image, inputs, new FormatCreateOptions());

        // Emptying every other bucket advances its generation, so the pass that
        // follows has to move runs both into and out of reused buckets.
        var dropped = kept.Keys.Where((_, i) => i % 2 == 1).ToArray();
        foreach (var name in dropped) kept.Remove(name);
        image.Position = 0;
        descriptor.Remove(image, dropped);

        image.Position = 0;
        descriptor.Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
        image.Flush(flushToDisk: true);
      }

      using (var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
        using var reader = new BcacheFsReader(image, leaveOpen: true);
        Assert.That(reader.Valid, Is.True, reader.Status);
        foreach (var (name, want) in kept) {
          var entry = reader.Entries.Single(e => e.Name == name);
          Assert.That(reader.Read(entry), Is.EqualTo(want).AsCollection,
            $"'{name}' did not survive the move");
        }
      }

      var result = FsInteropToolbox.RunWsl($"bcachefs fsck -n {FsInteropToolbox.WinToWsl(path)}");
      TestContext.Out.WriteLine($"bcachefs fsck exit={result.ExitCode}\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
      Assert.That(result.ExitCode, Is.EqualTo(0),
        $"bcachefs fsck rejected the defragmented reused-generation image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }

  /// <summary>
  /// Stops the oracle before it judges an image it cannot read.
  /// </summary>
  /// <remarks>
  /// <para>This package writes metadata version 1.3, the version Debian's and
  /// Ubuntu's bcachefs-tools write and check, so any checker from 1.3 on can open
  /// it. A checker older than that refuses the superblock outright, which is a
  /// statement about the checker and not about the volume; that, and an absent
  /// tool, are the only skips.</para>
  ///
  /// <para>A checker that opens the image and then complains is answering the
  /// question that was asked, and its answer is the verdict — an oracle that ran
  /// and said no is never downgraded to a skip.</para>
  /// </remarks>
  private static void RequireCapableChecker() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("No Linux environment (WSL) to run bcachefs-tools in.");
    if (!FsInteropToolbox.WslHasTool("bcachefs"))
      Assert.Ignore("bcachefs-tools is not installed: `sudo apt install -y bcachefs-tools`.");

    var required = (Major: BcacheFsFormat.Version >> 10, Minor: BcacheFsFormat.Version & 0x3FF);
    var reported = FsInteropToolbox.RunWsl("bcachefs version").StdOut.Trim();
    var parts = reported.TrimStart('v').Split('.');
    if (parts.Length < 2
      || !int.TryParse(parts[0], out var major)
      || !int.TryParse(parts[1], out var minor))
      return;   // unreadable version: run the checker rather than silently skipping it

    if (major < required.Major || (major == required.Major && minor < required.Minor))
      Assert.Ignore(
        $"bcachefs-tools {reported} predates the metadata version this package writes "
        + $"({required.Major}.{required.Minor}) and refuses the superblock before reading any bucket.");
  }

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 37 + seed * 19 + i / 257);
    return result;
  }
}

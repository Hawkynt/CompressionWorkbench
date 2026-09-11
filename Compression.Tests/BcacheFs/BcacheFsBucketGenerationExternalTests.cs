using System.Diagnostics;
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

      var result = Run("bcachefs", "fsck", "-n", path);
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

      var result = Run("bcachefs", "fsck", "-n", path);
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
  /// <para>This package stamps the metadata version in
  /// <see cref="BcacheFsFormat.Version"/> and the <c>incompat_version_field</c>
  /// feature with it. A checker older than that version refuses the superblock
  /// outright — <c>error validating superblock: Filesystem has incompatible
  /// features</c>, <c>invalid_sb_features</c> — without looking at a single
  /// bucket, which is a statement about the checker and not about the volume.
  /// Ubuntu's current archive ships 1.3.4 against the 1.38 written here, so the
  /// oracle is skipped there and runs wherever the tool is new enough.</para>
  ///
  /// <para>This is the only skip. A checker that opens the image and then
  /// complains is answering the question that was asked, and its answer is the
  /// verdict — an oracle that ran and said no is never downgraded to a skip.</para>
  /// </remarks>
  private static void RequireCapableChecker() {
    if (!OperatingSystem.IsLinux())
      Assert.Ignore("The mandatory bcachefs generation oracle runs on the Ubuntu CI leg.");

    var required = (Major: BcacheFsFormat.Version >> 10, Minor: BcacheFsFormat.Version & 0x3FF);
    var reported = Run("bcachefs", "version").StdOut.Trim();

    var digits = reported.AsSpan().TrimStart('v');
    var parts = digits.ToString().Split('.');
    if (parts.Length < 2
      || !int.TryParse(parts[0], out var major)
      || !int.TryParse(parts[1], out var minor))
      return;   // unreadable version: run the checker rather than silently skipping it

    if (major < required.Major || (major == required.Major && minor < required.Minor))
      Assert.Ignore(
        $"bcachefs-tools {reported} predates the metadata version this package writes "
        + $"({required.Major}.{required.Minor}) and refuses the superblock before reading any "
        + "bucket, so it cannot witness generations here.");
  }

  private static (string StdOut, string StdErr, int ExitCode) Run(
      string executable, params string[] arguments) {
    Process? process;
    try {
      var start = new ProcessStartInfo(executable) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      foreach (var argument in arguments) start.ArgumentList.Add(argument);
      process = Process.Start(start);
    } catch (System.ComponentModel.Win32Exception e) {
      Assert.Fail($"'{executable}' is required by this Linux oracle but is not installed: {e.Message}");
      return default;
    }

    Assert.That(process, Is.Not.Null);
    using (process) {
      var stdout = process!.StandardOutput.ReadToEnd();
      var stderr = process.StandardError.ReadToEnd();
      process.WaitForExit();
      return (stdout, stderr, process.ExitCode);
    }
  }

  private static byte[] Payload(int length, int seed) {
    var result = new byte[length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = (byte)(i * 37 + seed * 19 + i / 257);
    return result;
  }
}

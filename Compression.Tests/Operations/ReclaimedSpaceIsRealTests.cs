#pragma warning disable CS1591
using Compression.Registry;
using Compression.Tests.Support;

namespace Compression.Tests.Operations;

/// <summary>
/// Every format that says it can reclaim space has to actually reclaim it — and
/// the volume it hands back has to still be one its own tools will open.
/// </summary>
/// <remarks>
/// <para>Two claims are being checked, and they fail in opposite directions. A
/// format that answers <see cref="LayoutReclaim.Sparse" /> or
/// <see cref="LayoutReclaim.HardLinks" /> and then hands back a volume the same
/// size has promised a saving nobody got. One that produces a smaller volume by
/// writing something its own filesystem would not recognise has done worse than
/// nothing.</para>
///
/// <para>Neither is visible from inside. A hole and a zone full of zeros read
/// back identically through our own reader, and so do one inode with two names
/// and two inodes with one each; the only difference is what the volume looks
/// like to the system that owns the format. So the size is measured here and the
/// verdict is left to <c>fsck</c> and the kernel's driver.</para>
///
/// <para>The list walks the registry rather than naming formats, so a format
/// that learns to reclaim is checked from the moment it says so.</para>
/// </remarks>
[TestFixture]
public class ReclaimedSpaceIsRealTests {

  /// <summary>Every descriptor that claims it can reclaim anything.</summary>
  private static IEnumerable<string> Claimants() {
    foreach (var descriptor in FormatRegistry.All.OrderBy(d => d.Id, StringComparer.Ordinal)) {
      if (descriptor is not ILayoutOptimizable optimizable) continue;
      if (optimizable.ReclaimSupport == LayoutReclaim.None) continue;
      if (FormatRegistry.GetArchiveOps(descriptor.Id) is not IArchiveCreatable) continue;
      yield return descriptor.Id;
    }
  }

  /// <summary>
  /// How much payload to ask for. A format with a floor its volumes never go
  /// below shows no saving at all until the payload is past it, so the probe
  /// grows until it is — rather than declaring a format broken for being roomy.
  /// </summary>
  private static readonly int[] Scales = [1, 8, 64, 512];

  /// <summary>
  /// Files that are mostly zeros and files that are copies of each other, kept
  /// individually small so the tightest of the formats being asked can hold
  /// them, and multiplied in number when more payload is needed.
  /// </summary>
  private static Dictionary<string, byte[]> Probe(int scale) {
    byte[] Solid(int length, int seed) {
      var data = new byte[length];
      for (var i = 0; i < length; ++i) data[i] = (byte)(i * seed + 7);
      return data;
    }

    byte[] MostlyHole(int length) {
      var data = new byte[length];
      for (var i = 0; i < 400 && i < length; ++i) data[i] = (byte)(i * 31 + 7);
      return data;
    }

    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    var shared = Solid(4_000, 17);
    for (var group = 0; group < scale; ++group) {
      files[$"HOLEY{group}A.BIN"] = MostlyHole(6_000);
      files[$"HOLEY{group}B.BIN"] = MostlyHole(5_000);
      files[$"ALLHOLE{group}.BIN"] = new byte[6_000];
      for (var i = 0; i < 4; ++i) files[$"COPY{group}_{i}.BIN"] = (byte[])shared.Clone();
      files[$"OTHER{group}.BIN"] = Solid(4_000, 29 + group);
    }
    return files;
  }

  /// <summary>
  /// Builds a volume of the probe set, or null when the format cannot hold it.
  /// </summary>
  private static byte[]? TryCreate(string formatId, Dictionary<string, byte[]> files) {
    var creatable = (IArchiveCreatable)FormatRegistry.GetArchiveOps(formatId)!;
    using var built = new MemoryStream();
    try {
      creatable.Create(built,
        files.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList(),
        new FormatCreateOptions());
    } catch {
      return null;
    }
    return built.ToArray();
  }

  private static byte[] Rebuild(ILayoutOptimizable optimizable, byte[] source, LayoutReclaim asking) {
    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    optimizable.RebuildStreaming(input, output, new LayoutRebuildOptions {
      MakeSparse = asking.HasFlag(LayoutReclaim.Sparse),
      DeduplicateWithLinks = asking.HasFlag(LayoutReclaim.HardLinks),
    });
    return output.ToArray();
  }

  [TestCaseSource(nameof(Claimants)), Category("Regression")]
  public void WhatItSaysItCanReclaim_ItReclaims(string formatId) {
    var optimizable = (ILayoutOptimizable)FormatRegistry.All.First(d => d.Id == formatId);

    Dictionary<string, byte[]>? files = null;
    byte[]? plain = null, reclaimed = null;
    foreach (var scale in Scales) {
      var candidate = Probe(scale);
      var source = TryCreate(formatId, candidate);
      if (source == null) break;               // as much as this format will hold

      files = candidate;
      plain = Rebuild(optimizable, source, LayoutReclaim.None);
      reclaimed = Rebuild(optimizable, source, optimizable.ReclaimSupport);
      if (reclaimed.Length < plain.Length) break;
    }

    if (files == null || plain == null || reclaimed == null) {
      Assert.Ignore($"{formatId}: cannot hold even the smallest probe set.");
      return;
    }

    Assert.That(reclaimed.Length, Is.LessThan(plain.Length),
      $"{formatId} says it can reclaim {optimizable.ReclaimSupport}, but a volume of files that "
      + $"are mostly zeros and mostly copies of each other came back at {reclaimed.Length:N0} "
      + $"bytes against {plain.Length:N0} — no saving at all");

    // Every byte still has to be there. A hole that comes back as data, or data
    // that comes back as a hole, leaves the file exactly the right length and
    // reads perfectly well; only the bytes are wrong.
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    using var rebuilt = new MemoryStream(reclaimed);
    var seen = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in ops.List(rebuilt, null)) {
      if (entry.IsDirectory) continue;
      using var stream = ops.OpenEntry(rebuilt, entry.Name, null);
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      seen[entry.Name.Replace('\\', '/').TrimStart('/')] = buffer.ToArray();
    }

    foreach (var (name, want) in files) {
      Assert.That(seen.ContainsKey(name), Is.True,
        $"{formatId}: '{name}' is missing from the reclaimed volume");
      Assert.That(seen[name], Is.EqualTo(want).AsCollection,
        $"{formatId}: '{name}' came back with different bytes from the reclaimed volume");
    }
  }

  [TestCaseSource(nameof(Claimants)), Category("Interop")]
  public void AReclaimedVolume_IsStillOneItsOwnToolsWillOpen(string formatId) {
    if (!ThirdPartyFsCheck.IsSupported(formatId))
      Assert.Ignore($"{formatId}: nothing here reads this format but us.");

    var optimizable = (ILayoutOptimizable)FormatRegistry.All.First(d => d.Id == formatId);

    var files = Probe(1);
    var source = TryCreate(formatId, files);
    if (source == null) {
      Assert.Ignore($"{formatId}: cannot hold the probe set.");
      return;
    }

    var reclaimed = Rebuild(optimizable, source, optimizable.ReclaimSupport);
    var path = Path.Combine(Path.GetTempPath(), "cwb_reclaim_" + Guid.NewGuid().ToString("N")[..8] + ".img");
    File.WriteAllBytes(path, reclaimed);
    try {
      var checker = ThirdPartyFsCheck.Fsck(formatId, path);
      if (checker.Ran)
        Assert.That(checker.Ok, Is.True,
          $"{checker.Tool} rejected a volume {formatId} reclaimed space in: {checker.Detail}");

      var read = ThirdPartyFsCheck.ReadBack(formatId, path, [.. files.Values]);
      if (read.Ran)
        Assert.That(read.Ok, Is.True,
          $"{read.Tool} read a volume {formatId} reclaimed space in and did not get the files "
          + $"back: {read.Detail}");

      if (!checker.Ran && !read.Ran)
        Assert.Ignore($"{formatId}: no third-party reader ran here ({read.Detail}).");
    } finally {
      try { File.Delete(path); } catch { /* the scratch image is already gone */ }
    }
  }
}

using Compression.Lib;
using Compression.Registry;
using NUnit.Framework;

namespace Compression.Tests.FileSystems;

/// <summary>
/// Stores a file at every power-of-two boundary a filesystem will accept and
/// reads all of them back byte for byte.
/// </summary>
/// <remarks>
/// <para>
/// Block-allocation arithmetic goes wrong at boundaries, not in the middle of a
/// range: the size that exactly fills a block, the one that overflows it by a
/// byte, the one that falls a byte short, and the widths where a length field
/// runs out of bits. So for every <c>2^i</c> the sweep stores <c>2^i - 1</c>,
/// <c>2^i</c> and <c>2^i + 1</c>. Those runs overlap at the small end — 2 is
/// both <c>2^1</c> and <c>2^0 + 1</c> — and each distinct size is stored once.
/// </para>
/// <para>
/// Every size goes into one volume, so a writer that lays files out in sequence
/// has to get a long chain of differently-sized allocations right rather than a
/// single tidy one. Content is not uniform either: a bug can hide behind
/// compression or sparse-region handling just as easily as behind a length
/// field, so the sizes cycle through three shapes — incompressible, highly
/// compressible, and mostly zero with islands of data. Reading the bytes back
/// unchanged is what proves the storage, whichever path the writer took.
/// </para>
/// </remarks>
[TestFixture]
public sealed class FileSizeBoundaryTests {

  /// <summary>Highest exponent the sweep considers.</summary>
  private const int MaxExponent = 32;

  /// <summary>
  /// Largest file the routine run will actually store. The sweep is defined up
  /// to 2^32; sizes past this are left to <see cref="LargeFileSizesRoundTrip" />,
  /// which is opt-in because a single 4 GiB file costs more than a test run
  /// should.
  /// </summary>
  private const long RoutineCeiling = 1L << 20;

  /// <summary>
  /// Filesystems whose writer and reader are both exercised here. Each has been
  /// verified against its own native checker elsewhere in the suite.
  /// </summary>
  private static readonly string[] Filesystems = [
    "Fat", "ExFat", "Jfs", "HfsPlus", "Btrfs", "Ext",
    "CramFs", "Erofs", "MinixFs", "SquashFs", "Iso", "ReiserFs",
  ];

  /// <summary>
  /// The deduplicated boundary sizes at or below <paramref name="ceiling" />,
  /// ascending.
  /// </summary>
  private static IReadOnlyList<long> BoundarySizes(long ceiling) {
    var sizes = new SortedSet<long>();
    for (var i = 0; i <= MaxExponent; ++i) {
      var power = 1L << i;
      foreach (var size in new[] { power - 1, power, power + 1 })
        if (size >= 0 && size <= ceiling)
          sizes.Add(size);
    }

    return [.. sizes];
  }

  /// <summary>
  /// Content for a file of <paramref name="size" /> bytes. The shape cycles so
  /// that consecutive sizes take different paths through a writer that
  /// compresses or elides runs of zeros, and every byte is a function of its own
  /// offset so a misplaced block is visible rather than merely a wrong length.
  /// </summary>
  private static byte[] Pattern(long size) {
    var body = new byte[size];
    switch (size % 3) {
      case 0:
        // Incompressible: a writer that stores this verbatim has nowhere to hide.
        for (var i = 0L; i < size; ++i)
          body[i] = (byte)((i * 2_654_435_761L) >> 13);
        break;
      case 1:
        // Highly compressible: long runs, so compressing writers take that path.
        for (var i = 0L; i < size; ++i)
          body[i] = (byte)(i >> 8);
        break;
      default:
        // Mostly zero with islands, which is what invites sparse handling.
        for (var i = 0L; i < size; ++i)
          body[i] = (i & 0x3FF) < 16 ? (byte)(i | 1) : (byte)0;
        break;
    }

    return body;
  }

  private static string NameFor(long size) => $"size_{size:D10}.bin";

  /// <summary>
  /// True when <paramref name="id" /> will store a file of <paramref name="size" />
  /// bytes: the descriptor accepts the input and, for a fixed-size image, the
  /// file still leaves room for the filesystem's own structures.
  /// </summary>
  private static bool SupportsFileSize(IArchiveFormatOperations ops, long size, byte[] content) {
    if (ops is not IArchiveWriteConstraints constraints) return true;

    // A fixed-size image has to hold its metadata too; leave a quarter free
    // rather than guess each format's exact overhead.
    if (constraints.MaxTotalArchiveSize is { } max && size > max - max / 4) return false;

    return constraints.CanAccept(ArchiveInputInfo.InMemory(NameFor(size), content), out _);
  }

  private static void AssertEveryBoundarySizeRoundTrips(string id, long ceiling) {
    FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(id);
    Assert.That(ops, Is.InstanceOf<IArchiveCreatable>(), $"{id} cannot create volumes");

    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<ArchiveInputInfo>();
    foreach (var size in BoundarySizes(ceiling)) {
      var content = Pattern(size);
      if (!SupportsFileSize(ops!, size, content)) continue;
      var name = NameFor(size);
      expected[name] = content;
      inputs.Add(ArchiveInputInfo.InMemory(name, content));
    }

    Assert.That(expected, Is.Not.Empty, $"{id} accepted none of the boundary sizes");

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops!).Create(image, inputs, new FormatCreateOptions());

    // Extract once rather than per entry: the default per-entry reader unpacks
    // the whole volume each time, which would make this quadratic.
    var target = Path.Combine(Path.GetTempPath(), "fs-size-sweep-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(target);
    try {
      image.Position = 0;
      ops!.Extract(image, target, password: null, files: null);

      var extracted = Directory.EnumerateFiles(target, "size_*.bin", SearchOption.AllDirectories)
        .ToDictionary(static path => Path.GetFileName(path), static path => path, StringComparer.OrdinalIgnoreCase);

      var missing = expected.Keys.Where(name => !extracted.ContainsKey(name)).ToList();
      // A descriptor that declined a size up front never had it written; the
      // sweep only asks about what it accepted.
      Assert.That(missing, Is.Empty, $"{id} lost {missing.Count} of {expected.Count} files");

      foreach (var (name, want) in expected.OrderBy(static e => e.Key, StringComparer.Ordinal)) {
        var got = File.ReadAllBytes(extracted[name]);
        Assert.That(got.Length, Is.EqualTo(want.Length), $"{id}: {name} came back the wrong length");
        Assert.That(got, Is.EqualTo(want), $"{id}: {name} came back with different bytes");
      }
    } finally {
      try { Directory.Delete(target, recursive: true); } catch { /* best effort */ }
    }
  }

  [TestCaseSource(nameof(Filesystems))]
  [Category("Boundary")]
  [Category("Slow")]
  public void EveryBoundarySizeRoundTrips(string id)
    => AssertEveryBoundarySizeRoundTrips(id, RoutineCeiling);

  /// <summary>
  /// One large file per volume, straddling 8 MiB.
  /// </summary>
  /// <remarks>
  /// The sweep above puts every size in one volume, which keeps it quick but
  /// caps how large any single file gets. Some limits only show up past that:
  /// Minix sized both of its bitmaps at one block, so a volume needing more
  /// than 8192 zones wrote the zone bitmap over the inode table and read back
  /// as empty. One file per volume is the cheap way to reach those sizes.
  /// </remarks>
  [Test]
  [Category("Boundary")]
  [Category("Slow")]
  [Pairwise]
  public void ALargeFileRoundTrips(
      [ValueSource(nameof(Filesystems))] string id,
      [Values(1L << 23, 1L << 24)] long size) {
    FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(id);
    Assert.That(ops, Is.InstanceOf<IArchiveCreatable>(), $"{id} cannot create volumes");

    var content = Pattern(size);
    if (!SupportsFileSize(ops!, size, content))
      Assert.Ignore($"{id} declines a {size:N0}-byte file");

    var name = NameFor(size);
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops!).Create(image, [ArchiveInputInfo.InMemory(name, content)], new FormatCreateOptions());

    image.Position = 0;
    var entry = ops!.List(image, password: null)
      .FirstOrDefault(e => !e.IsDirectory && e.Name.Replace('\\', '/').EndsWith(name, StringComparison.OrdinalIgnoreCase));
    Assert.That(entry, Is.Not.Null, $"{id} lost a {size:N0}-byte file");
    Assert.That(entry!.OriginalSize, Is.EqualTo(size));

    // Extract by the name the listing gave: descriptors differ on whether they
    // root their paths at "/".
    image.Position = 0;
    var read = ops.ExtractEntryToMemory(image, entry.Name, password: null);
    Assert.That(read, Is.EqualTo(content), $"{id}: a {size:N0}-byte file came back with different bytes");
  }

  /// <summary>
  /// The rest of the sweep, up to 2^32. Opt-in: a single file at the top of that
  /// range is four gigabytes, which is more than a routine run should spend.
  /// </summary>
  [TestCaseSource(nameof(Filesystems))]
  [Category("Boundary")]
  [Explicit("Stores files up to 4 GiB; run with --filter Category=Boundary when you mean it.")]
  public void LargeFileSizesRoundTrip(string id)
    => AssertEveryBoundarySizeRoundTrips(id, 1L << MaxExponent);

  /// <summary>The sweep must cover each size once, and must not skip any.</summary>
  [Test]
  [Category("Boundary")]
  public void TheSweepCoversEachBoundarySizeExactlyOnce() {
    var sizes = BoundarySizes(RoutineCeiling);

    Assert.Multiple(() => {
      Assert.That(sizes, Is.Unique);
      Assert.That(sizes, Is.Ordered);
      Assert.That(sizes, Does.Contain(0L), "an empty file is the first boundary");
      Assert.That(sizes.Max(), Is.EqualTo(RoutineCeiling), "the ceiling itself is a boundary and belongs in the sweep");

      // Every triple that fits under the ceiling must be present in full. The
      // topmost power is the exception: its "+ 1" is over the line by a byte.
      foreach (var exponent in new[] { 0, 1, 9, 12, 16 }) {
        var power = 1L << exponent;
        Assert.That(sizes, Does.Contain(power - 1), $"2^{exponent} - 1");
        Assert.That(sizes, Does.Contain(power), $"2^{exponent}");
        Assert.That(sizes, Does.Contain(power + 1), $"2^{exponent} + 1");
      }
    });
  }
}

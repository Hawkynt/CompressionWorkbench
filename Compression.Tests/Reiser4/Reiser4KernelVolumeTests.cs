using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

/// <summary>
/// Volumes a Reiser4 kernel wrote (see <c>ReferenceVectors/README.md</c>): each was formatted by
/// mkfs.reiser4 with a plugin profile and filled through the kernel's own write path, and the
/// manifest beside it is what the kernel read back. The reader must agree with it entry for entry,
/// byte for byte, and the editor must carry every object across an edit unchanged.
/// </summary>
[TestFixture]
public sealed class Reiser4KernelVolumeTests {

  internal static readonly string[] Volumes = [
    "default", "gzip1-force-8k", "zstd1-latt-16k", "lzo1-none-4k", "reg40-smart", "reg40-tails", "reg40-node41",
    "node41", "tea-lexic", "fnv1-dot-o", "deg-ext-3", "rupasov", "journal-replay",
  ];

  /// <summary>Volumes populate.sh filled whose names the writer may key: every hash but Rupasov.</summary>
  internal static readonly string[] EditableVolumes = [.. Volumes.Where(static v => v is not ("rupasov" or "journal-replay"))];

  internal static byte[] Image(string name) {
    using var resource = typeof(Reiser4KernelVolumeTests).Assembly.GetManifestResourceStream($"Reiser4Vectors.{name}.img.gz")
      ?? throw new InvalidOperationException($"reference volume {name} is not embedded");
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    using var bytes = new MemoryStream();
    gzip.CopyTo(bytes);
    return bytes.ToArray();
  }

  /// <summary>One line of a kernel manifest.</summary>
  internal sealed record ManifestEntry(string Path, string Type, int Mode, uint Uid, uint Gid, long Size, uint Links,
      long ModifiedTime, ulong Device, string Value);

  internal static Dictionary<string, ManifestEntry> Manifest(string name) {
    using var resource = typeof(Reiser4KernelVolumeTests).Assembly.GetManifestResourceStream($"Reiser4Vectors.{name}.manifest")
      ?? throw new InvalidOperationException($"manifest {name} is not embedded");
    using var reader = new StreamReader(resource);
    return ParseManifest(reader.ReadToEnd());
  }

  internal static Dictionary<string, ManifestEntry> ParseManifest(string text) {
    var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
    foreach (var raw in text.Split('\n')) {
      var line = raw.TrimEnd('\r');
      if (!line.StartsWith("./", StringComparison.Ordinal)) continue;
      var f = line.Split('|');
      var device = f[8].Split(':');
      var major = Convert.ToUInt64(device[0], 16);
      var minor = Convert.ToUInt64(device[1], 16);
      result[f[0][2..]] = new ManifestEntry(f[0][2..], f[1], Convert.ToInt32(f[2], 8), uint.Parse(f[3], CultureInfo.InvariantCulture),
        uint.Parse(f[4], CultureInfo.InvariantCulture), long.Parse(f[5], CultureInfo.InvariantCulture), uint.Parse(f[6], CultureInfo.InvariantCulture),
        long.Parse(f[7], CultureInfo.InvariantCulture), major << 20 | minor, string.Join('|', f[9..]));
    }
    return result;
  }

  private static string Md5(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

  /// <summary>Asserts the reader sees exactly what the kernel manifest lists.</summary>
  internal static void AssertMatches(Reiser4Reader reader, IReadOnlyDictionary<string, ManifestEntry> manifest, string label,
      IReadOnlySet<string>? skip = null) {
    Assert.That(reader.NativeTreeValid, Is.True, $"{label}: {reader.NativeProblem}");
    var entries = reader.Entries.ToDictionary(static e => e.Name, StringComparer.Ordinal);
    var expected = manifest.Where(p => p.Key != "." && skip?.Contains(p.Key) != true).ToDictionary(StringComparer.Ordinal);
    Assert.That(entries.Keys.Where(k => skip?.Contains(k) != true).Order(StringComparer.Ordinal),
      Is.EqualTo(expected.Keys.Order(StringComparer.Ordinal)), $"{label}: the namespace");
    foreach (var (path, want) in expected) {
      var entry = entries[path];
      var metadata = entry.Metadata!;
      var where = $"{label}: {path}";
      Assert.That(metadata.Mode & 0xFFF, Is.EqualTo(want.Mode), where + " mode");
      Assert.That(metadata.UserId, Is.EqualTo(want.Uid), where + " uid");
      Assert.That(metadata.GroupId, Is.EqualTo(want.Gid), where + " gid");
      Assert.That(metadata.ModifiedTime, Is.EqualTo((uint)want.ModifiedTime), where + " mtime");
      Assert.That(metadata.LinkCount, Is.EqualTo(want.Links), where + " links");
      switch (want.Type) {
        case "directory":
          Assert.That(entry.IsDirectory, Is.True, where);
          Assert.That(metadata.Size, Is.EqualTo(want.Size), where + " size");
          break;
        case "regular file" or "regular empty file":
          Assert.That(entry.Kind, Is.EqualTo(Reiser4Reader.EntryKind.File), where);
          Assert.That(entry.Size, Is.EqualTo(want.Size), where + " size");
          Assert.That(Md5(reader.Extract(entry)), Is.EqualTo(want.Value), where + " bytes");
          break;
        case "symbolic link":
          Assert.That(entry.Kind, Is.EqualTo(Reiser4Reader.EntryKind.Symlink), where);
          Assert.That(entry.LinkTarget, Is.EqualTo(want.Value), where + " target");
          break;
        case "fifo":
          Assert.That(entry.Kind, Is.EqualTo(Reiser4Reader.EntryKind.Fifo), where);
          break;
        case "character special file":
          Assert.That(entry.Kind, Is.EqualTo(Reiser4Reader.EntryKind.CharacterDevice), where);
          Assert.That(entry.Device, Is.EqualTo(want.Device), where + " device");
          break;
        case "block special file":
          Assert.That(entry.Kind, Is.EqualTo(Reiser4Reader.EntryKind.BlockDevice), where);
          Assert.That(entry.Device, Is.EqualTo(want.Device), where + " device");
          break;
        default:
          Assert.Fail($"{where}: unexpected manifest type {want.Type}");
          break;
      }
    }
  }

  [TestCaseSource(nameof(Volumes)), Category("HappyPath")]
  public void GivenAVolumeTheKernelWrote_WhenReading_ThenEveryEntryMatchesWhatTheKernelReadBack(string volume) {
    using var reader = new Reiser4Reader(new MemoryStream(Image(volume)));
    Assert.Multiple(() => AssertMatches(reader, Manifest(volume), volume));
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeWithACommittedButUnflushedTransaction_WhenReading_ThenTheReplayedViewIsWhatTheKernelMounted() {
    var image = Image("journal-replay");
    using var reader = new Reiser4Reader(new MemoryStream(image));
    Assert.That(reader.ReplayedTransactions, Is.GreaterThan(0), "the reference volume must need a replay to be meaningful");
    var unreplayed = image.ToArray();
    Array.Clear(unreplayed, 19 * 4096, 2 * 4096);
    using var stale = new Reiser4Reader(new MemoryStream(unreplayed));
    var manifest = Manifest("journal-replay");
    Assert.That(stale.Entries.Select(static e => e.Name).Order(), Is.Not.EqualTo(manifest.Keys.Where(static k => k != ".").Order()),
      "without the replay the namespace is the one before the last commit");
  }

  [TestCaseSource(nameof(EditableVolumes)), Category("HappyPath")]
  public void GivenAVolumeTheKernelWrote_WhenAddingAndRemoving_ThenEveryOtherObjectIsCarriedUnchanged(string volume) {
    var image = new MemoryStream();
    image.Write(Image(volume));
    var manifest = Manifest(volume);
    var payload = new byte[9_000];
    new Random(7).NextBytes(payload);
    var removed = manifest.Keys.First(static k => k.StartsWith("many/", StringComparison.Ordinal));
    var descriptor = new Reiser4FormatDescriptor();
    descriptor.Add(image, [ArchiveInputInfo.InMemory("added/a-new-name-longer-than-twenty-three.bin", payload)]);
    descriptor.Remove(image, [removed]);

    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      AssertMatches(reader, manifest, volume, new HashSet<string>(StringComparer.Ordinal) { removed, "many", "added", "added/a-new-name-longer-than-twenty-three.bin" });
      Assert.That(reader.ReplayedTransactions, Is.Zero, "a rebuilt volume has its journal folded in");
      Assert.That(reader.Entries.Any(e => e.Name == removed), Is.False);
      Assert.That(reader.Extract(reader.Entries.Single(static e => e.Name == "added/a-new-name-longer-than-twenty-three.bin")), Is.EqualTo(payload));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAVolumeWithAPendingTransaction_WhenEditing_ThenTheReplayedStateIsWhatIsCarried() {
    var image = new MemoryStream();
    image.Write(Image("journal-replay"));
    var manifest = Manifest("journal-replay");
    new Reiser4FormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("added.bin", [1, 2, 3])]);
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.ReplayedTransactions, Is.Zero, "the rebuilt journal is blank");
      Assert.That(image.ToArray().AsSpan(19 * 4096, 2 * 4096).ToArray(), Is.All.Zero);
      AssertMatches(reader, manifest, "journal-replay", new HashSet<string>(StringComparer.Ordinal) { "added.bin" });
    });
  }

  [TestCaseSource(nameof(EditableVolumes)), TestCase("rupasov"), Category("Spec")]
  public void GivenTheEntriesTheKernelKeyed_WhenKeyingTheirNamesAgain_ThenTheKeysAreIdentical(string volume) {
    using var reader = new Reiser4Reader(new MemoryStream(Image(volume)));
    var vol = reader.Volume!;
    var checkedNames = 0;
    foreach (var directory in vol.Objects.Values.Where(static o => o.IsDirectory)) {
      var hash = (Reiser4HashId)vol.Plugin(directory, 4);
      var fibration = (Reiser4FibrationId)vol.Plugin(directory, 5);
      foreach (var entry in directory.Entries.Where(static e => !e.IsDot)) {
        if (entry.Hashed && !Reiser4NameKeys.CanHash(hash)) continue;
        var (ordering, objectId, offset, _) = Reiser4NameKeys.Make(entry.Name, hash, fibration);
        Assert.That((ordering, objectId, offset), Is.EqualTo((entry.Ordering, entry.ObjectId, entry.Offset)),
          $"{volume}: {Reiser4Volume.DecodeName(entry.Name)} under {hash}/{fibration}");
        ++checkedNames;
      }
    }
    Assert.That(checkedNames, Is.GreaterThan(300));
  }
}

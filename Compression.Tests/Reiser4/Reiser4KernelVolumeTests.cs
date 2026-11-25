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
    "node41", "tea-lexic", "fnv1-dot-o", "deg-ext-3", "rupasov", "journal-replay", "short-keys", "short-keys-reg40",
  ];

  /// <summary>
  /// Volumes of three-word keys, formatted by a patched mkfs.reiser4 and filled by a kernel built
  /// with <c>REISER4_LARGE_KEY=0</c>: neither the packaged kernel nor the packaged fsck.reiser4
  /// reads them, so they are judged by <see cref="Reiser4ShortKeyOracle" /> instead.
  /// </summary>
  internal static readonly string[] ShortKeyVolumes = ["short-keys", "short-keys-reg40"];

  /// <summary>
  /// Volumes populate.sh filled that fsck.reiser4 calls consistent and the writer edits in the shared
  /// edit tests: every hash but Rupasov, whose long names fsck.reiser4 keys differently from the
  /// kernel (the Rupasov volume has its own kernel-judged edit test).
  /// </summary>
  internal static readonly string[] EditableVolumes =
    [.. Volumes.Where(static v => v is not ("rupasov" or "journal-replay") && !ShortKeyVolumes.Contains(v))];

  /// <summary>Every volume the offline edit test carries across an edit: the editable ones and the short-key ones.</summary>
  internal static readonly string[] OfflineEditableVolumes = [.. EditableVolumes, .. ShortKeyVolumes];

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

  [TestCaseSource(nameof(OfflineEditableVolumes)), Category("HappyPath")]
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

  [TestCaseSource(nameof(EditableVolumes)), TestCase("rupasov"), TestCaseSource(nameof(ShortKeyVolumes)), Category("Spec")]
  public void GivenTheEntriesTheKernelKeyed_WhenKeyingTheirNamesAgain_ThenTheKeysAreIdentical(string volume) {
    using var reader = new Reiser4Reader(new MemoryStream(Image(volume)));
    var vol = reader.Volume!;
    var checkedNames = 0;
    var hashedNames = 0;
    foreach (var directory in vol.Objects.Values.Where(static o => o.IsDirectory)) {
      var hash = (Reiser4HashId)vol.Plugin(directory, 4);
      var fibration = (Reiser4FibrationId)vol.Plugin(directory, 5);
      foreach (var entry in directory.Entries.Where(static e => !e.IsDot)) {
        if (entry.Hashed && !Reiser4NameKeys.CanHash(hash)) continue;
        var (ordering, objectId, offset, _) = vol.Keys.NameKey(entry.Name, hash, fibration);
        Assert.That((ordering, objectId, offset), Is.EqualTo((entry.Ordering, entry.ObjectId, entry.Offset)),
          $"{volume}: {Reiser4Volume.DecodeName(entry.Name)} under {hash}/{fibration}");
        ++checkedNames;
        if (entry.Hashed) ++hashedNames;
      }
    }
    Assert.That(checkedNames, Is.GreaterThan(300));
    Assert.That(hashedNames, Is.GreaterThanOrEqualTo(3), "the 35-, 46- and 255-byte names are hashed");
  }

  [TestCaseSource(nameof(ShortKeyVolumes)), Category("Spec")]
  public void GivenAShortKeyVolume_WhenReading_ThenItsKeysAreThreeWordsAndNamesPastFifteenBytesAreHashed(string volume) {
    using var reader = new Reiser4Reader(new MemoryStream(Image(volume)));
    var vol = reader.Volume!;
    var lengths = vol.Objects.Values.Single(o => vol.Paths.Any(p => p.Path == "lengths" && p.Object == o));
    var byName = lengths.Entries.Where(static e => !e.IsDot && !e.IsDotDot).ToDictionary(static e => Reiser4Volume.DecodeName(e.Name));
    Assert.Multiple(() => {
      Assert.That(vol.Keys, Is.SameAs(Reiser4KeyShape.Short));
      Assert.That(vol.Objects.Keys.All(static k => k.Ordering == 0), Is.True, "a short key has no ordering word");
      // Held whole up to fifteen bytes, hashed from sixteen on - where a large key holds 23.
      foreach (var name in new[] { "seven-7", "eight-08", "fifteen-bytes-x", "fifteen-bytes-y" })
        Assert.That(byName[name].Hashed, Is.False, name);
      foreach (var name in new[] { "sixteen-bytes-xy", "sixteen-bytes-yz", "twenty-three-bytes-xyz1", "twenty-four-bytes-xyz123" })
        Assert.That(byName[name].Hashed, Is.True, name);
    });
  }

  [Test, Category("Spec")]
  public void GivenTheShortAndLargeKeyShapes_ThenTheirOnDiskSizesAreTheMeasuredOnes() {
    Assert.Multiple(() => {
      Assert.That((Reiser4KeyShape.Large.ItemHeaderBytes, Reiser4KeyShape.Large.UnitHeaderBytes, Reiser4KeyShape.Large.TargetBytes,
        Reiser4KeyShape.Large.MaxInlineLength), Is.EqualTo((38, 26, 24, 23)));
      Assert.That((Reiser4KeyShape.Short.ItemHeaderBytes, Reiser4KeyShape.Short.UnitHeaderBytes, Reiser4KeyShape.Short.TargetBytes,
        Reiser4KeyShape.Short.MaxInlineLength), Is.EqualTo((30, 18, 16, 15)));
      Assert.That(Reiser4KeyShape.FromFormatFlags(1), Is.SameAs(Reiser4KeyShape.Large));
      Assert.That(Reiser4KeyShape.FromFormatFlags(0), Is.SameAs(Reiser4KeyShape.Short));
      Assert.That(Reiser4KeyShape.FromFormatFlags(0xFFFF_FFFF_FFFF_FFFE), Is.SameAs(Reiser4KeyShape.Short), "only bit 0 decides");
    });
  }

  [Test, Category("ErrorHandling")]
  public void GivenAKeyWithAnOrderingWord_WhenWrittenAsAShortKey_ThenItIsRefused() {
    var buffer = new byte[24];
    Assert.Throws<InvalidDataException>(() => Reiser4KeyShape.Short.Write(Reiser4Key.Of(0x2a, Reiser4Key.MinorStatData, 1, 0x10000, 0), buffer));
  }

  [TestCase(1), TestCase(7), TestCase(8), TestCase(15), TestCase(16), TestCase(23), TestCase(24), TestCase(255), Category("Spec")]
  public void GivenANameOfAnyLength_WhenKeyedShort_ThenItIsHashedExactlyWhenLongerThanFifteenAndUnpacksWhenNot(int length) {
    var name = System.Text.Encoding.ASCII.GetBytes(new string('q', length));
    var (ordering, objectId, offset, hashed) = Reiser4KeyShape.Short.NameKey(name, Reiser4HashId.R5, Reiser4FibrationId.Extension1);
    Assert.Multiple(() => {
      Assert.That(ordering, Is.Zero);
      Assert.That(hashed, Is.EqualTo(length > 15));
      Assert.That(Reiser4NameKeys.IsHashed(ordering, objectId), Is.EqualTo(hashed));
      Assert.That(Reiser4NameKeys.Unpack(ordering, objectId, offset), hashed ? Is.Null : Is.EqualTo(name));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAShortKeyVolume_WhenAddingAFile_ThenEveryObjectIsCarriedAndTheKeysStayShort() {
    var image = new MemoryStream();
    image.Write(Image("short-keys"));
    new Reiser4FormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("lengths/new-name-of-24-bytes.txt", [1, 2, 3])]);
    using var reader = new Reiser4Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.Volume!.Keys, Is.SameAs(Reiser4KeyShape.Short));
      AssertMatches(reader, Manifest("short-keys"), "short-keys", new HashSet<string>(StringComparer.Ordinal) { "lengths", "lengths/new-name-of-24-bytes.txt" });
      Assert.That(reader.Extract(reader.Entries.Single(static e => e.Name == "lengths/new-name-of-24-bytes.txt")), Is.EqualTo(new byte[] { 1, 2, 3 }));
    });
  }

  [Test, Category("HappyPath")]
  public void GivenTheWriterAskedForShortKeys_WhenCreating_ThenTheVolumeHasShortKeysAndReadsBack() {
    var writer = new Reiser4Writer { ShortKeys = true };
    writer.AddFile("a/fifteen-bytes-x", [1]);
    writer.AddFile("a/sixteen-bytes-xy", [2]);
    writer.AddFile("a/" + new string('z', 255), [3]);
    writer.AddSymlink("a/link", "fifteen-bytes-x");
    var image = writer.Build();
    using var reader = new Reiser4Reader(new MemoryStream(image));
    Assert.Multiple(() => {
      Assert.That(reader.NativeTreeValid, Is.True, reader.NativeProblem);
      Assert.That(reader.Volume!.Keys, Is.SameAs(Reiser4KeyShape.Short));
      Assert.That(BitConverter.ToUInt64(image, 17 * 4096 + 72) & 1, Is.Zero, "the format40 flags say short keys");
      Assert.That(reader.Extract(reader.Entries.Single(static e => e.Name == "a/sixteen-bytes-xy")), Is.EqualTo(new byte[] { 2 }));
      Assert.That(reader.Extract(reader.Entries.Single(static e => e.Name == "a/" + new string('z', 255))), Is.EqualTo(new byte[] { 3 }));
      Assert.That(reader.Entries.Single(static e => e.Name == "a/link").LinkTarget, Is.EqualTo("fifteen-bytes-x"));
    });
  }
}

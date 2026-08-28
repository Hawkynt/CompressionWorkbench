#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CbmNibble;

/// <summary>
/// Shared pseudo-archive helpers for G64/NIB. The public entries are raw
/// half-tracks, not files from the Commodore filesystem contained inside them.
/// Keeping that boundary explicit lets a future block/sector driver sit below a
/// C64 filesystem driver without archive-level rebuilds silently changing layers.
/// </summary>
internal static class CbmNibbleEntries {
  public static CbmNibbleReader.NibbleImage ReadImage(Stream stream, string fileName) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return CbmNibbleReader.Read(ms.ToArray(), fileName);
  }

  public static List<(string Name, byte[] Data)> Build(Stream stream, string fileName) {
    var image = ReadImage(stream, fileName);
    var result = new List<(string, byte[])> {
      ("metadata.ini", CbmNibbleReader.BuildMetadata(image)),
    };
    foreach (var track in image.Tracks)
      if (track.Data.Length > 0)
        result.Add(($"track_{track.Index:D2}.bin", track.Data));
    return result;
  }

  public static List<ArchiveEntryInfo> List(Stream stream, string fileName)
    => Build(stream, fileName).Select((entry, index) => new ArchiveEntryInfo(
      index, entry.Name, entry.Data.LongLength, entry.Data.LongLength,
      "stored", false, false, null)).ToList();

  public static void Extract(Stream stream, string outputDir, string[]? files, string fileName) {
    foreach (var entry in Build(stream, fileName)) {
      if (files != null && files.Length > 0 && !MatchesFilter(entry.Name, files)) continue;
      WriteFile(outputDir, entry.Name, entry.Data);
    }
  }

  public static bool TryParseTrackName(string name, out int index) {
    index = -1;
    var leaf = Path.GetFileName(name.Replace('\\', '/'));
    if (!leaf.StartsWith("track_", StringComparison.OrdinalIgnoreCase) ||
        !leaf.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return false;
    var digits = leaf.AsSpan(6, leaf.Length - 10);
    return int.TryParse(digits, System.Globalization.NumberStyles.None,
      System.Globalization.CultureInfo.InvariantCulture, out index);
  }

  public static bool IsMetadata(string name)
    => string.Equals(Path.GetFileName(name), "metadata.ini", StringComparison.OrdinalIgnoreCase);

  public static uint DefaultSpeedZone(int halfTrackIndex) {
    var track = halfTrackIndex / 2 + 1;
    return track switch {
      >= 1 and <= 17 => 3,
      >= 18 and <= 24 => 2,
      >= 25 and <= 30 => 1,
      _ => 0,
    };
  }

  public static void Commit(Stream archive, byte[] image) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("Nibble-image mutation requires a readable, writable, seekable stream.", nameof(archive));
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(image);
    archive.Flush();
  }

  public static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}

/// <summary>
/// Commodore G64 GCR track container. R/W operates directly on opaque
/// <c>track_NN.bin</c> half-tracks; ordinary-file Create remains available as a
/// convenience path that first builds a Commodore filesystem and GCR-encodes it.
/// Variable-speed-map images are readable but direct mutation fails closed until
/// their auxiliary speed blocks are modeled.
/// </summary>
public sealed class G64FormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveShrinkable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveCreatable,
  IArchiveLayoutMap {

  public string Id => "G64";
  public string DisplayName => "G64 (Commodore GCR)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".g64";
  public IReadOnlyList<string> Extensions => [".g64"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("GCR-1541"u8.ToArray(), Offset: 0, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored GCR tracks")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VICE G64 Commodore GCR track image with direct half-track R/W and canonical re-layout";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => CbmNibbleEntries.List(stream, "image.g64");

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => CbmNibbleEntries.Extract(stream, outputDir, files, "image.g64");

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var image = CbmNibbleEntries.ReadImage(archive, "image.g64");
    byte[] data;
    if (CbmNibbleEntries.IsMetadata(entryName))
      data = CbmNibbleReader.BuildMetadata(image);
    else if (CbmNibbleEntries.TryParseTrackName(entryName, out var index))
      data = image.Tracks.FirstOrDefault(t => t.Index == index)?.Data ?? [];
    else
      data = [];
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    entry.CopyTo(ms);
    return ms.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory && !CbmNibbleEntries.IsMetadata(i.ArchiveName)).ToArray();
    var direct = files.Where(i => CbmNibbleEntries.TryParseTrackName(i.ArchiveName, out _)).ToArray();
    if (direct.Length > 0) {
      if (direct.Length != files.Length)
        throw new NotSupportedException("G64 Create cannot mix track_NN.bin inputs with Commodore filesystem files.");
      var tracks = direct.Select(input => {
        CbmNibbleEntries.TryParseTrackName(input.ArchiveName, out var index);
        return new CbmNibbleReader.Track(index, input.ReadContent(), CbmNibbleEntries.DefaultSpeedZone(index));
      }).ToArray();
      output.Write(CbmNibbleWriter.BuildG64FromTracks(tracks));
      return;
    }

    var writer = new CbmNibbleWriter();
    foreach (var input in files)
      writer.AddFile(Path.GetFileName(input.ArchiveName), input.ReadContent());
    writer.WriteTo(output);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var image = RequireDirectWritableProfile(archive);
    var tracks = image.Tracks.ToDictionary(t => t.Index);
    var highest = image.TrackCount - 1;
    foreach (var input in inputs.Where(i => !i.IsDirectory)) {
      if (CbmNibbleEntries.IsMetadata(input.ArchiveName)) continue;
      if (!CbmNibbleEntries.TryParseTrackName(input.ArchiveName, out var index) || index is < 0 or >= 84)
        throw new NotSupportedException("G64 mutation accepts track_00.bin through track_83.bin entries only.");
      var data = input.ReadContent();
      if (data.Length > ushort.MaxValue)
        throw new NotSupportedException($"G64 track_{index:D2}.bin exceeds the 16-bit track length field.");
      var speed = tracks.TryGetValue(index, out var old) ? old.SpeedZone : CbmNibbleEntries.DefaultSpeedZone(index);
      tracks[index] = new CbmNibbleReader.Track(index, data, speed);
      highest = Math.Max(highest, index);
    }
    var rebuilt = CbmNibbleWriter.BuildG64FromTracks(
      tracks.Values.OrderBy(t => t.Index).ToArray(), image.Version, Math.Max(1, highest + 1));
    VerifyTrackPayloads(rebuilt, tracks.Values.Where(t => t.Data.Length > 0));
    CbmNibbleEntries.Commit(archive, rebuilt);
  }

  public void Remove(Stream archive, string[] entryNames) {
    var image = RequireDirectWritableProfile(archive);
    var remove = new HashSet<int>();
    foreach (var name in entryNames) {
      if (CbmNibbleEntries.IsMetadata(name))
        throw new NotSupportedException("metadata.ini is a generated G64 view and cannot be removed.");
      if (!CbmNibbleEntries.TryParseTrackName(name, out var index))
        throw new NotSupportedException("G64 removal accepts track_NN.bin entries only.");
      remove.Add(index);
    }
    var tracks = image.Tracks.Select(t => remove.Contains(t.Index) ? t with { Data = [] } : t).ToArray();
    var rebuilt = CbmNibbleWriter.BuildG64FromTracks(tracks, image.Version, image.TrackCount);
    VerifyTrackPayloads(rebuilt, tracks.Where(t => t.Data.Length > 0));
    CbmNibbleEntries.Commit(archive, rebuilt);
  }

  public void Purge(Stream archive) {
    var image = RequireDirectWritableProfile(archive);
    var empty = image.Tracks.Select(t => t with { Data = [] }).ToArray();
    CbmNibbleEntries.Commit(archive,
      CbmNibbleWriter.BuildG64FromTracks(empty, image.Version, image.TrackCount));
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions());

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var image = RequireDirectWritableProfile(archive);
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, this.EnumerateLayout(archive).ToList(), "Reading G64 track map"));
    options.CancellationToken.ThrowIfCancellationRequested();
    var rebuilt = CbmNibbleWriter.BuildG64FromTracks(image.Tracks, image.Version, image.TrackCount);
    VerifyTrackPayloads(rebuilt, image.Tracks.Where(t => t.Data.Length > 0));
    options.CancellationToken.ThrowIfCancellationRequested();
    CbmNibbleEntries.Commit(archive, rebuilt);
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, this.EnumerateLayout(archive).ToList(), "G64 track blocks compacted"));
  }

  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var original = CbmNibbleEntries.ReadAll(input);
    var image = CbmNibbleReader.Read(original, "image.g64");
    EnsureConstantSpeedZones(image);
    var compact = CbmNibbleWriter.BuildG64FromTracks(image.Tracks, image.Version, image.TrackCount);
    VerifyTrackPayloads(compact, image.Tracks.Where(t => t.Data.Length > 0));
    var selected = compact.Length < original.Length ? compact : original;
    output.Position = 0;
    output.SetLength(0);
    output.Write(selected);
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var image = CbmNibbleEntries.ReadImage(archive, "image.g64");
    if (image.Tracks.Any(t => t.SpeedZone > 3)) {
      // Pointer-based speed maps may occupy otherwise unreferenced byte ranges.
      // Until those blocks are parsed, the only safe wipe map is "all reserved".
      return [new DefragBlockInfo(0, image.TotalFileSize, DefragBlockKind.MetadataReserved, "$G64/variable-speed-profile")];
    }

    var result = new List<DefragBlockInfo>();
    var tableBytes = 12L + image.TrackCount * 8L;
    result.Add(new DefragBlockInfo(0, Math.Min(tableBytes, image.TotalFileSize),
      DefragBlockKind.MetadataReserved, "$G64/header-and-tables"));
    foreach (var track in image.Tracks)
      if (track.Data.Length > 0 && track.PhysicalOffset >= 0 && track.PhysicalLength > 0)
        result.Add(new DefragBlockInfo(track.PhysicalOffset, track.PhysicalLength,
          DefragBlockKind.Used, $"track_{track.Index:D2}.bin"));
    return result;
  }

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    var extents = this.EnumerateLayout(image).ToList();
    if (extents.Count == 0) return 0;
    image.Position = 0;
    // G64 physical track extents include the u16 length prefix, so archive-entry
    // logical sizes cannot safely drive generic cluster-tip wiping.
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, false, null);
  }

  private static CbmNibbleReader.NibbleImage RequireDirectWritableProfile(Stream archive) {
    var image = CbmNibbleEntries.ReadImage(archive, "image.g64");
    EnsureConstantSpeedZones(image);
    return image;
  }

  private static void EnsureConstantSpeedZones(CbmNibbleReader.NibbleImage image) {
    var variable = image.Tracks.FirstOrDefault(t => t.SpeedZone > 3);
    if (variable != null)
      throw new NotSupportedException(
        $"G64 track {variable.Index} uses variable-speed map pointer 0x{variable.SpeedZone:X8}; " +
        "the image remains readable, but mutation is refused until speed-map blocks are modeled.");
  }

  private static void VerifyTrackPayloads(byte[] rebuilt, IEnumerable<CbmNibbleReader.Track> expected) {
    var parsed = CbmNibbleReader.Read(rebuilt, "image.g64");
    var actual = parsed.Tracks.ToDictionary(t => t.Index);
    foreach (var track in expected) {
      if (!actual.TryGetValue(track.Index, out var found) || !found.Data.AsSpan().SequenceEqual(track.Data))
        throw new InvalidOperationException($"G64 rebuild changed track_{track.Index:D2}.bin; refusing to commit it.");
    }
  }
}

/// <summary>
/// Commodore NIB fixed-slot raw nibble dump. Each of the 84 half-tracks occupies
/// exactly 8192 bytes, so direct mutation is naturally random-access friendly:
/// replace one slot, clear one slot, or rewrite the same fixed geometry. An
/// all-zero slot is the workbench's explicit empty-track representation.
/// </summary>
public sealed class NibFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveLayoutMap {

  public string Id => "Nib";
  public string DisplayName => "NIB (Commodore nibble dump)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nib";
  public IReadOnlyList<string> Extensions => [".nib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Fixed 8192-byte GCR slots")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore raw nibble dump with fixed-slot half-track R/W";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => CbmNibbleEntries.List(stream, "image.nib");

  public void Extract(Stream stream, string outputDir, string? password, string[]? files)
    => CbmNibbleEntries.Extract(stream, outputDir, files, "image.nib");

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var image = CbmNibbleEntries.ReadImage(archive, "image.nib");
    byte[] data;
    if (CbmNibbleEntries.IsMetadata(entryName))
      data = CbmNibbleReader.BuildMetadata(image);
    else if (CbmNibbleEntries.TryParseTrackName(entryName, out var index))
      data = image.Tracks.FirstOrDefault(t => t.Index == index)?.Data ?? [];
    else
      data = [];
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    entry.CopyTo(ms);
    return ms.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory && !CbmNibbleEntries.IsMetadata(i.ArchiveName)).ToArray();
    var direct = files.Where(i => CbmNibbleEntries.TryParseTrackName(i.ArchiveName, out _)).ToArray();
    if (direct.Length > 0) {
      if (direct.Length != files.Length)
        throw new NotSupportedException("NIB Create cannot mix track_NN.bin inputs with Commodore filesystem files.");
      var tracks = direct.Select(input => {
        CbmNibbleEntries.TryParseTrackName(input.ArchiveName, out var index);
        return new CbmNibbleReader.Track(index, input.ReadContent(), 0);
      }).ToArray();
      output.Write(CbmNibbleWriter.BuildNibFromTracks(tracks));
      return;
    }

    var writer = new CbmNibbleWriter();
    foreach (var input in files)
      writer.AddFile(Path.GetFileName(input.ArchiveName), input.ReadContent());
    output.Write(writer.BuildNib());
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var image = CbmNibbleEntries.ReadImage(archive, "image.nib");
    var tracks = image.Tracks.ToDictionary(t => t.Index);
    foreach (var input in inputs.Where(i => !i.IsDirectory)) {
      if (CbmNibbleEntries.IsMetadata(input.ArchiveName)) continue;
      if (!CbmNibbleEntries.TryParseTrackName(input.ArchiveName, out var index) || index is < 0 or >= CbmNibbleReader.NibTrackCount)
        throw new NotSupportedException("NIB mutation accepts track_00.bin through track_83.bin entries only.");
      var data = input.ReadContent();
      if (data.Length != CbmNibbleReader.NibTrackSize)
        throw new NotSupportedException($"NIB track_{index:D2}.bin must be exactly {CbmNibbleReader.NibTrackSize} bytes.");
      tracks[index] = new CbmNibbleReader.Track(index, data, 0);
    }
    var rebuilt = CbmNibbleWriter.BuildNibFromTracks(tracks.Values.OrderBy(t => t.Index).ToArray());
    VerifyNibTracks(rebuilt, tracks.Values.Where(t => t.Data.Length > 0));
    CbmNibbleEntries.Commit(archive, rebuilt);
  }

  public void Remove(Stream archive, string[] entryNames) {
    var image = CbmNibbleEntries.ReadImage(archive, "image.nib");
    var remove = new HashSet<int>();
    foreach (var name in entryNames) {
      if (CbmNibbleEntries.IsMetadata(name))
        throw new NotSupportedException("metadata.ini is a generated NIB view and cannot be removed.");
      if (!CbmNibbleEntries.TryParseTrackName(name, out var index))
        throw new NotSupportedException("NIB removal accepts track_NN.bin entries only.");
      remove.Add(index);
    }
    var tracks = image.Tracks.Select(t => remove.Contains(t.Index) ? t with { Data = [] } : t).ToArray();
    var rebuilt = CbmNibbleWriter.BuildNibFromTracks(tracks);
    VerifyNibTracks(rebuilt, tracks.Where(t => t.Data.Length > 0));
    CbmNibbleEntries.Commit(archive, rebuilt);
  }

  public void Purge(Stream archive) {
    _ = CbmNibbleEntries.ReadImage(archive, "image.nib");
    CbmNibbleEntries.Commit(archive, new byte[CbmNibbleReader.NibExpectedFileSize]);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions());

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var image = CbmNibbleEntries.ReadImage(archive, "image.nib");
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, this.EnumerateLayout(archive).ToList(),
      "NIB uses fixed half-track slots; no relocation is necessary"));
    options.CancellationToken.ThrowIfCancellationRequested();
    // Fixed slot offsets are the canonical layout. Do not rewrite merely to make
    // a defragment verb look busy; a real block driver would make the same call a no-op.
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, image.TotalFileSize, this.EnumerateLayout(archive).ToList(),
      "NIB is already physically canonical"));
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var image = CbmNibbleEntries.ReadImage(archive, "image.nib");
    var result = new List<DefragBlockInfo>(image.Tracks.Count + 1);
    foreach (var track in image.Tracks) {
      var offset = track.Index * (long)CbmNibbleReader.NibTrackSize;
      result.Add(new DefragBlockInfo(offset, CbmNibbleReader.NibTrackSize,
        track.Data.Length == 0 ? DefragBlockKind.Free : DefragBlockKind.Used,
        track.Data.Length == 0 ? null : $"track_{track.Index:D2}.bin"));
    }
    var covered = image.Tracks.Count * (long)CbmNibbleReader.NibTrackSize;
    if (covered < image.TotalFileSize)
      result.Add(new DefragBlockInfo(covered, image.TotalFileSize - covered,
        DefragBlockKind.MetadataReserved, "$NIB/trailing-partial-slot"));
    return result;
  }

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    var extents = this.EnumerateLayout(image).ToList();
    if (extents.Count == 0) return 0;
    image.Position = 0;
    return UnusedSpaceWiper.Wipe(image, extents, image.Length, false, null);
  }

  private static void VerifyNibTracks(byte[] rebuilt, IEnumerable<CbmNibbleReader.Track> expected) {
    var parsed = CbmNibbleReader.Read(rebuilt, "image.nib");
    var actual = parsed.Tracks.ToDictionary(t => t.Index);
    foreach (var track in expected) {
      if (!actual.TryGetValue(track.Index, out var found) || !found.Data.AsSpan().SequenceEqual(track.Data))
        throw new InvalidOperationException($"NIB rebuild changed track_{track.Index:D2}.bin; refusing to commit it.");
    }
  }
}

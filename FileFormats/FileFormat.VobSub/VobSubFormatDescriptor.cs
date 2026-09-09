#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.VobSub;

/// <summary>
/// Pseudo-archive descriptor for VobSub DVD subtitles. The primary file is the textual
/// <c>.idx</c>; the binary <c>.sub</c> sibling is resolved by replacing the extension.
/// Each subtitle frame from the <c>.sub</c> is exposed as <c>subtitle_NNN.bin</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://sam.zoy.org/writings/dvd/subtitles/</c> — Sam Hocevar's classic DVD subtitle (SPU/RLE) format description</description></item>
///   <item><description>ISO/IEC 13818-1 — MPEG-2 program-stream / PES framing used by the <c>.sub</c> side</description></item>
///   <item><description>VobSub / DirectVobSub (Gabest) — the defining tool producing .idx/.sub pairs</description></item>
/// </list>
/// </summary>
/// <remarks>
/// When invoked without filesystem context (pure stream input), only the parsed index
/// metadata is returned — the .sub sibling cannot be discovered. The <see cref="ListPair"/>
/// / <see cref="ExtractPair"/> overloads accept both files explicitly for callers that
/// have filesystem access. Creation and mutation require a file-backed <c>.idx</c> stream,
/// because one archive operation must update both members of the VobSub pair.
/// </remarks>
public sealed class VobSubFormatDescriptor :
    IFormatDescriptor,
    IArchiveFormatOperations,
    IArchiveInMemoryExtract,
    IArchiveCreatable,
    IArchiveModifiable,
    IArchiveWriteConstraints {

  /// <summary>Gets the id.</summary>
  public string Id => "VobSub";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "VobSub DVD Subtitles";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".idx";
  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".idx"];
  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("# VobSub index file"u8.ToArray(), Confidence: 0.95),
  ];
  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;
  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>Gets the description.</summary>
  public string Description => "VobSub DVD subtitle index (.idx) plus sibling MPEG-PS subtitle stream (.sub).";

  /// <inheritdoc />
  public long? MaxTotalArchiveSize => null;

  /// <inheritdoc />
  public string AcceptedInputsDescription =>
    "accepts: index.idx, metadata.ini, subtitle_NNN.bin (packet-preserving remux), subtitle_NNN.spu (raw DVD SPU mux)";

  /// <inheritdoc />
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    if (input.IsDirectory) {
      reason = "VobSub is a flat subtitle pair and does not contain directories.";
      return false;
    }

    var name = LeafName(input.ArchiveName);
    if (name.Equals("index.idx", StringComparison.OrdinalIgnoreCase)
        || name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
        || TryParseFrameName(name, out _, out _)) {
      reason = null;
      return true;
    }

    reason = "Expected index.idx, metadata.ini, subtitle_NNN.bin, or subtitle_NNN.spu.";
    return false;
  }

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(stream);
    return BuildEntries(idxBytes, subBytes).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();
  }

  /// <summary>Decodes the supplied input.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(stream);
    foreach (var e in BuildEntries(idxBytes, subBytes)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Opens a single VobSub entry as a bounded read-only stream. The
  /// <c>metadata.ini</c> + <c>index.idx</c> + per-frame entries each
  /// produce a decoded byte buffer; the matched buffer is wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var (idxBytes, subBytes) = ReadIndexAndSibling(archive);
    foreach (var e in BuildEntries(idxBytes, subBytes)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>Performs the extract entry operation.</summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    var (idxBytes, subBytes) = ReadIndexAndSibling(input);
    foreach (var e in BuildEntries(idxBytes, subBytes))
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  /// <summary>
  /// Creates a VobSub pair from <c>index.idx</c> plus one frame per timestamp.
  /// <c>subtitle_NNN.bin</c> is copied byte-for-byte as already-framed MPEG-PS data;
  /// <c>subtitle_NNN.spu</c> is packetized as DVD Private Stream 1.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var files = ValidateInputs(inputs);
    var indexInputs = files.Where(static input => LeafName(input.ArchiveName).Equals("index.idx", StringComparison.OrdinalIgnoreCase)).ToArray();
    if (indexInputs.Length != 1)
      throw new ArgumentException("VobSub creation requires exactly one index.idx input.", nameof(inputs));

    var indexText = Encoding.UTF8.GetString(indexInputs[0].ReadContent());
    var index = VobSubReader.ReadIndex(indexText);
    var frames = CollectFrames(files, index.Entries.Count, nameof(inputs));
    var pair = VobSubWriter.Build(indexText, frames);
    WritePair(output, pair);
  }

  /// <summary>
  /// Adds or replaces frames in an existing pair. A replacement <c>index.idx</c>
  /// may be supplied to append timestamps; all pre-existing <c>.bin</c> frame chunks
  /// that are not replaced remain byte-identical.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var file = RequireFileBackedIndex(archive, "edit");
    var additions = ValidateInputs(inputs);
    var current = ReadEditablePair(file);

    var indexReplacement = additions
      .Where(static input => LeafName(input.ArchiveName).Equals("index.idx", StringComparison.OrdinalIgnoreCase))
      .ToArray();
    if (indexReplacement.Length > 1)
      throw new ArgumentException("VobSub edit accepts at most one replacement index.idx.", nameof(inputs));

    var indexText = indexReplacement.Length == 1
      ? Encoding.UTF8.GetString(indexReplacement[0].ReadContent())
      : Encoding.UTF8.GetString(current.IdxBytes);
    var targetIndex = VobSubReader.ReadIndex(indexText);
    if (targetIndex.Entries.Count < current.Pair.Frames.Count)
      throw new ArgumentException("Use Remove to delete VobSub frames; Add cannot shrink the timestamp set.", nameof(inputs));

    var frames = new VobSubWriter.Frame?[targetIndex.Entries.Count];
    for (var i = 0; i < current.Pair.Frames.Count; ++i)
      frames[i] = new VobSubWriter.Frame(current.Pair.Frames[i], VobSubWriter.FrameKind.ProgramStream);

    foreach (var input in additions) {
      var name = LeafName(input.ArchiveName);
      if (!TryParseFrameName(name, out var frameIndex, out var kind)) continue;
      if ((uint)frameIndex >= (uint)frames.Length)
        throw new ArgumentException($"VobSub frame '{name}' has no matching timestamp in index.idx.", nameof(inputs));
      frames[frameIndex] = new VobSubWriter.Frame(input.ReadContent(), kind);
    }

    for (var i = 0; i < frames.Length; ++i)
      if (frames[i] is null)
        throw new ArgumentException($"VobSub timestamp {i} has no subtitle_NNN.bin/.spu frame.", nameof(inputs));

    var pair = VobSubWriter.Build(indexText, frames.Select(static frame => frame!).ToArray());
    WritePair(file, pair);
  }

  /// <summary>
  /// Removes named subtitle frames and their timestamp directives, then concatenates
  /// the surviving already-framed chunks without re-encoding them.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var file = RequireFileBackedIndex(archive, "edit");
    var current = ReadEditablePair(file);
    var remove = new HashSet<int>();

    foreach (var rawName in entryNames) {
      var name = LeafName(rawName);
      if (TryParseFrameName(name, out var frameIndex, out _)) {
        if ((uint)frameIndex >= (uint)current.Pair.Frames.Count)
          throw new FileNotFoundException($"VobSub frame not found: {rawName}");
        remove.Add(frameIndex);
        continue;
      }

      if (name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase)
          || name.Equals("index.idx", StringComparison.OrdinalIgnoreCase))
        throw new NotSupportedException($"'{name}' is a mandatory/rendered VobSub entry and cannot be removed individually.");
      throw new FileNotFoundException($"VobSub entry not found: {rawName}");
    }

    var survivingFrames = new List<VobSubWriter.Frame>();
    var survivingTimestamps = new List<TimeSpan>();
    for (var i = 0; i < current.Pair.Frames.Count; ++i) {
      if (remove.Contains(i)) continue;
      survivingFrames.Add(new VobSubWriter.Frame(current.Pair.Frames[i], VobSubWriter.FrameKind.ProgramStream));
      survivingTimestamps.Add(current.Pair.Index.Entries[i].Timestamp);
    }

    var indexText = Encoding.UTF8.GetString(current.IdxBytes);
    var pair = VobSubWriter.Build(indexText, survivingFrames, survivingTimestamps);
    WritePair(file, pair);
  }

  /// <summary>Removes every subtitle frame while retaining a valid empty index/sub pair.</summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var file = RequireFileBackedIndex(archive, "purge");
    var current = ReadEditablePair(file);
    var pair = VobSubWriter.Build(
      Encoding.UTF8.GetString(current.IdxBytes),
      Array.Empty<VobSubWriter.Frame>(),
      Array.Empty<TimeSpan>());
    WritePair(file, pair);
  }

  /// <summary>
  /// Lists entries given both files explicitly (preferred when the caller has filesystem
  /// access and can locate the sibling .sub).
  /// </summary>
  public List<ArchiveEntryInfo> ListPair(byte[] idxBytes, byte[] subBytes) =>
    BuildEntries(idxBytes, subBytes).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: null, Kind: e.Kind)).ToList();

  /// <summary>
  /// Extracts entries given both files explicitly (preferred when the caller has filesystem
  /// access and can locate the sibling .sub).
  /// </summary>
  public void ExtractPair(byte[] idxBytes, byte[] subBytes, string outputDir, string[]? files) {
    foreach (var e in BuildEntries(idxBytes, subBytes)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<ArchiveInputInfo> ValidateInputs(IReadOnlyList<ArchiveInputInfo> inputs) {
    var descriptor = new VobSubFormatDescriptor();
    var result = new List<ArchiveInputInfo>(inputs.Count);
    foreach (var input in inputs) {
      if (!descriptor.CanAccept(input, out var reason))
        throw new ArgumentException($"VobSub cannot accept '{input.ArchiveName}': {reason}", nameof(inputs));
      if (!input.IsDirectory) result.Add(input);
    }
    return result;
  }

  private static VobSubWriter.Frame[] CollectFrames(
      IReadOnlyList<ArchiveInputInfo> files,
      int expectedCount,
      string argumentName) {
    var frames = new VobSubWriter.Frame?[expectedCount];
    foreach (var input in files) {
      var name = LeafName(input.ArchiveName);
      if (!TryParseFrameName(name, out var index, out var kind)) continue;
      if ((uint)index >= (uint)expectedCount)
        throw new ArgumentException($"VobSub frame '{name}' has no matching timestamp in index.idx.", argumentName);
      if (frames[index] is not null)
        throw new ArgumentException($"VobSub frame {index} was supplied more than once.", argumentName);
      frames[index] = new VobSubWriter.Frame(input.ReadContent(), kind);
    }

    for (var i = 0; i < frames.Length; ++i)
      if (frames[i] is null)
        throw new ArgumentException($"VobSub timestamp {i} has no subtitle_NNN.bin/.spu frame.", argumentName);
    return frames.Select(static frame => frame!).ToArray();
  }

  private static bool TryParseFrameName(string name, out int index, out VobSubWriter.FrameKind kind) {
    index = -1;
    kind = default;
    if (!name.StartsWith("subtitle_", StringComparison.OrdinalIgnoreCase)) return false;

    var extension = Path.GetExtension(name);
    kind = extension.ToLowerInvariant() switch {
      ".bin" => VobSubWriter.FrameKind.ProgramStream,
      ".spu" => VobSubWriter.FrameKind.RawSpu,
      _ => default,
    };
    if (extension is not (".bin" or ".BIN" or ".spu" or ".SPU")
        && !extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
        && !extension.Equals(".spu", StringComparison.OrdinalIgnoreCase))
      return false;

    var digits = name.AsSpan("subtitle_".Length, name.Length - "subtitle_".Length - extension.Length);
    return !digits.IsEmpty
      && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index)
      && index >= 0;
  }

  private static string LeafName(string name)
    => Path.GetFileName(name.Replace('\\', '/'));

  private static FileStream RequireFileBackedIndex(Stream stream, string operation) {
    if (stream is not FileStream file)
      throw new NotSupportedException($"VobSub {operation} requires a file-backed .idx stream so its sibling .sub can be updated.");
    if (!file.CanRead || !file.CanWrite || !file.CanSeek)
      throw new ArgumentException("VobSub edit stream must be readable, writable, and seekable.", nameof(stream));
    return file;
  }

  private static (byte[] IdxBytes, byte[] SubBytes, VobSubReader.Pair Pair) ReadEditablePair(FileStream stream) {
    stream.Position = 0;
    var (idxBytes, subBytes) = ReadIndexAndSibling(stream);
    if (!File.Exists(SiblingSubPath(stream)))
      throw new FileNotFoundException("VobSub sibling .sub file is required for editing.", SiblingSubPath(stream));
    return (idxBytes, subBytes, VobSubReader.Read(idxBytes, subBytes));
  }

  /// <summary>
  /// Reads the .idx bytes from <paramref name="stream"/>; if the stream is a
  /// <see cref="FileStream"/>, also resolves the sibling .sub by extension swap.
  /// Returns an empty .sub byte array when no sibling is reachable.
  /// </summary>
  private static (byte[] Idx, byte[] Sub) ReadIndexAndSibling(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var idxBytes = ms.ToArray();

    var subBytes = Array.Empty<byte>();
    if (stream is FileStream fs) {
      var subPath = SiblingSubPath(fs);
      if (File.Exists(subPath)) subBytes = File.ReadAllBytes(subPath);
    }
    return (idxBytes, subBytes);
  }

  private static string SiblingSubPath(FileStream indexStream)
    => Path.ChangeExtension(indexStream.Name, ".sub");

  private static void WritePair(Stream output, VobSubWriter.Pair pair) {
    var file = output as FileStream
      ?? throw new NotSupportedException("VobSub creation requires a file-backed .idx output so the sibling .sub file can be created.");
    if (!file.CanWrite || !file.CanSeek)
      throw new ArgumentException("VobSub output stream must be writable and seekable.", nameof(output));

    File.WriteAllBytes(SiblingSubPath(file), pair.SubBytes);
    file.Position = 0;
    file.SetLength(0);
    file.Write(pair.IndexBytes);
    file.Flush();
  }

  private static List<(string Name, string Kind, byte[] Data)> BuildEntries(byte[] idxBytes, byte[] subBytes) {
    var pair = VobSubReader.Read(idxBytes, subBytes);

    var result = new List<(string, string, byte[])> {
      ("metadata.ini", "Tag", BuildMetadata(pair, subBytes.Length)),
      ("index.idx", "Tag", idxBytes),
    };
    for (var i = 0; i < pair.Frames.Count; i++)
      result.Add(($"subtitle_{i:D3}.bin", "Payload", pair.Frames[i]));
    return result;
  }

  private static byte[] BuildMetadata(VobSubReader.Pair pair, int subBytesLength) {
    var sb = new StringBuilder();
    sb.AppendLine("[vobsub]");
    sb.Append(CultureInfo.InvariantCulture, $"size = {pair.Index.Width}x{pair.Index.Height}\n");
    sb.Append("language = ").AppendLine(pair.Index.Language ?? "(unset)");
    sb.Append("palette_entries = ").Append(pair.Index.Palette.Count).Append('\n');
    sb.Append("frame_count = ").Append(pair.Frames.Count).Append('\n');
    sb.Append("sub_bytes_available = ").Append(subBytesLength).Append('\n');
    if (pair.Index.Entries.Count > 0) {
      sb.Append(CultureInfo.InvariantCulture, $"first_timestamp = {pair.Index.Entries[0].Timestamp:c}\n");
      sb.Append(CultureInfo.InvariantCulture, $"last_timestamp = {pair.Index.Entries[^1].Timestamp:c}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

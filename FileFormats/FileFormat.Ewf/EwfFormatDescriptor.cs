#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Zlib;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ewf;

/// <summary>
/// EnCase Expert Witness Format (EWF/E01) descriptor. The mutable archive
/// surface is the forensic image's logical <c>media.raw</c> payload; parsed
/// section payloads remain available as read-only diagnostic entries. Existing
/// physical EVF images can therefore be replaced, purged, canonicalized,
/// compressed and shrunk without pretending their internal sections are user
/// files.
/// </summary>
public sealed class EwfFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveShrinkable,
  IArchiveLayoutMap,
  ILayoutOptimizable,
  IFormatOptionsSchema {

  public string Id => "Ewf";
  public string DisplayName => "EnCase EWF (E01)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsOptimize | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".e01";
  public IReadOnlyList<string> Extensions => [".e01", ".ewf", ".l01"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x45, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00], Offset: 0, Confidence: 0.95),
    new([0x4C, 0x56, 0x46, 0x09, 0x0D, 0x0A, 0xFF, 0x00], Offset: 0, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored chunks", SupportsOptimize: true),
    new("zlib", "Zlib-compressed chunks", SupportsOptimize: true),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "EnCase EWF forensic media image with logical-media R/W, canonical repack and chunk compression optimization.";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "CompressChunks",
      DisplayName: "Compress media chunks",
      Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Zlib-compress each 32 KiB EWF media chunk when compression makes that chunk smaller."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var image = ReadImage(stream);
    var result = new List<ArchiveEntryInfo>();
    var index = 0;
    if (!image.IsLogical) {
      try {
        var media = EwfReader.ExtractMedia(image);
        result.Add(new ArchiveEntryInfo(index++, "media.raw", media.LongLength, MediaStoredBytes(image),
          HasCompressedChunks(image) ? "mixed/zlib" : "stored", false, false, null, "media"));
      } catch (NotSupportedException) {
        // Keep diagnostics available even when this EWF profile cannot expose media.raw.
      } catch (InvalidDataException) { }
    }

    var metadata = BuildMetadata(image);
    result.Add(new ArchiveEntryInfo(index++, "metadata.ini", metadata.LongLength, metadata.LongLength,
      "generated", false, false, null, "metadata"));
    foreach (var (section, i) in image.Sections.Select((s, i) => (s, i))) {
      var name = $"section_{i:D2}_{SafeNameSegment(section.Type)}.bin";
      result.Add(new ArchiveEntryInfo(index++, name, section.Payload.LongLength, section.Payload.LongLength,
        "stored", false, false, null, "section"));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = ReadImage(stream);
    if (!image.IsLogical && MatchesRequested("media.raw", files)) {
      try { WriteFile(outputDir, "media.raw", EwfReader.ExtractMedia(image)); }
      catch (NotSupportedException) { }
    }
    if (MatchesRequested("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadata(image));
    for (var i = 0; i < image.Sections.Count; ++i) {
      var section = image.Sections[i];
      var name = $"section_{i:D2}_{SafeNameSegment(section.Type)}.bin";
      if (MatchesRequested(name, files)) WriteFile(outputDir, name, section.Payload);
    }
  }

  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    var image = ReadImage(archive);
    byte[] data;
    if (string.Equals(entryName, "media.raw", StringComparison.OrdinalIgnoreCase))
      data = EwfReader.ExtractMedia(image);
    else if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase))
      data = BuildMetadata(image);
    else {
      data = [];
      for (var i = 0; i < image.Sections.Count; ++i) {
        var section = image.Sections[i];
        var name = $"section_{i:D2}_{SafeNameSegment(section.Type)}.bin";
        if (!string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
        data = section.Payload;
        break;
      }
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }

  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var result = new MemoryStream();
    entry.CopyTo(result);
    return result.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var media = ReadCreateMedia(inputs);
    var compress = ParseBool(options?.FormatSpecific?.GetValueOrDefault("CompressChunks"), false);
    var writer = new EwfWriter { CompressChunks = compress };
    output.Write(writer.Build(media));
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory).ToArray();
    if (files.Length == 0) return;
    if (files.Length != 1)
      throw new NotSupportedException("EWF existing-image mutation accepts one logical media payload at a time.");

    var existing = ReadImage(archive);
    if (existing.IsLogical)
      throw new NotSupportedException("LVF logical-evidence mutation is not implemented; physical EVF/E01 is R/W.");
    RewriteMedia(archive, files[0].ReadContent(), existing, HasCompressedChunks(existing));
  }

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (!entryNames.Any(n => string.Equals(n, "media.raw", StringComparison.OrdinalIgnoreCase))) {
      if (entryNames.Length > 0)
        throw new NotSupportedException("EWF section_*/metadata.ini entries are diagnostic views; remove 'media.raw' to empty the forensic image.");
      return;
    }
    var existing = ReadImage(archive);
    if (existing.IsLogical)
      throw new NotSupportedException("LVF logical-evidence mutation is not implemented; physical EVF/E01 is R/W.");
    RewriteMedia(archive, [], existing, HasCompressedChunks(existing));
  }

  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var existing = ReadImage(archive);
    if (existing.IsLogical)
      throw new NotSupportedException("LVF logical-evidence purge is not implemented; physical EVF/E01 is purgeable.");
    RewriteMedia(archive, [], existing, HasCompressedChunks(existing));
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions());

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    var existing = ReadImage(archive);
    var media = EwfReader.ExtractMedia(existing);
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, this.EnumerateLayout(archive).ToList(),
      "Reading EWF media/chunk table"));
    options.CancellationToken.ThrowIfCancellationRequested();

    var writer = WriterFrom(existing, HasCompressedChunks(existing));
    var rebuilt = writer.Build(media);
    VerifyMedia(rebuilt, media);
    options.CancellationToken.ThrowIfCancellationRequested();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "writing", 0.9, Math.Max(0, archive.Length - 1), Math.Max(0, rebuilt.LongLength - 1),
      Math.Max(archive.Length, rebuilt.LongLength), null, "Staged canonical EVF complete"));
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "committing", 0.99, -1, -1, rebuilt.LongLength, null,
      "Committing verified EVF rebuild"));
    CommitBytes(archive, rebuilt);
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, this.EnumerateLayout(archive).ToList(),
      "EWF canonicalization complete"));
  }

  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var original = ReadAll(input);
    var image = EwfReader.Read(original);
    var media = EwfReader.ExtractMedia(image);
    var stored = WriterFrom(image, false).Build(media);
    var compressed = WriterFrom(image, true).Build(media);
    VerifyMedia(stored, media);
    VerifyMedia(compressed, media);
    var best = new[] { original, stored, compressed }.MinBy(a => a.LongLength)!;
    output.Position = 0;
    output.SetLength(0);
    output.Write(best);
  }

  public LayoutAnalysis AnalyzeLayout(Stream image) {
    var parsed = ReadImage(image);
    long mediaBytes = 0;
    try { mediaBytes = EwfReader.ExtractMedia(parsed).LongLength; } catch { }
    return new LayoutAnalysis {
      ImageSize = image.CanSeek ? image.Length : parsed.TotalFileSize,
      CurrentUnitSize = EwfWriter.ChunkSize,
      CurrentSlackBytes = Math.Max(0, parsed.TotalFileSize - mediaBytes),
      OptimalUnitSize = EwfWriter.ChunkSize,
      OptimalSlackBytes = 0,
      RequiresRebuild = ["Changing chunk compression rewrites sectors/table/hash sections."],
      Notes = [
        $"{parsed.Sections.Count} section(s); chunk size {EwfWriter.ChunkSize:N0} bytes.",
        HasCompressedChunks(parsed) ? "At least one media chunk is zlib-compressed." : "Media chunks are stored; compression optimization is available.",
        "Re-layout is a staged forensic-image rebuild; the source remains unchanged until commit.",
      ],
    };
  }

  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.MakeSparse || options.DeduplicateWithLinks)
      throw new NotSupportedException("EWF cannot express filesystem sparse files or hard-link deduplication.");
    var image = ReadImage(source);
    var media = EwfReader.ExtractMedia(image);
    var compress = options.Parameters != null && options.Parameters.TryGetValue("CompressChunks", out var raw)
      ? ParseBool(raw, HasCompressedChunks(image))
      : HasCompressedChunks(image);
    var rebuilt = WriterFrom(image, compress).Build(media);
    VerifyMedia(rebuilt, media);
    target.Position = 0;
    target.SetLength(0);
    target.Write(rebuilt);
    options.OnProgress?.Invoke(media.LongLength, media.LongLength);
  }

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var image = ReadImage(archive);
    yield return new DefragBlockInfo(0, Math.Min(EwfReader.FileHeaderSize, image.TotalFileSize),
      DefragBlockKind.MetadataReserved, "$EWF/header");
    foreach (var section in image.Sections) {
      var length = section.SectionSize == 0
        ? EwfReader.SectionDescriptorSize
        : checked((long)section.SectionSize);
      if (section.DescriptorOffset >= image.TotalFileSize) continue;
      length = Math.Min(length, image.TotalFileSize - section.DescriptorOffset);
      if (length <= 0) continue;
      yield return new DefragBlockInfo(
        section.DescriptorOffset,
        length,
        section.Type == "sectors" ? DefragBlockKind.Used : DefragBlockKind.MetadataReserved,
        section.Type == "sectors" ? "media.raw" : "$EWF/" + section.Type);
    }
  }

  private static bool MatchesRequested(string name, string[]? files)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static EwfReader.EwfImage ReadImage(Stream stream)
    => EwfReader.Read(ReadAll(stream));

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static byte[] ReadCreateMedia(IReadOnlyList<ArchiveInputInfo> inputs) {
    var files = inputs.Where(i => !i.IsDirectory).ToArray();
    var named = files.LastOrDefault(i => string.Equals(i.ArchiveName, "media.raw", StringComparison.OrdinalIgnoreCase));
    if (named != null) return named.ReadContent();
    if (files.Length == 0) return [];
    if (files.Length == 1) return files[0].ReadContent();
    using var ms = new MemoryStream();
    foreach (var file in files) ms.Write(file.ReadContent());
    return ms.ToArray();
  }

  private static void RewriteMedia(Stream archive, byte[] media, EwfReader.EwfImage existing, bool compress) {
    var rebuilt = WriterFrom(existing, compress).Build(media);
    VerifyMedia(rebuilt, media);
    CommitBytes(archive, rebuilt);
  }

  private static void CommitBytes(Stream archive, byte[] rebuilt) {
    if (!archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("EWF mutation requires a writable, seekable stream.", nameof(archive));
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(rebuilt);
    archive.Flush();
  }

  private static void VerifyMedia(byte[] rebuilt, ReadOnlySpan<byte> media) {
    var decoded = EwfReader.ExtractMedia(EwfReader.Read(rebuilt));
    if (decoded.Length < media.Length || !decoded.AsSpan(0, media.Length).SequenceEqual(media))
      throw new InvalidOperationException("EWF rebuild did not reproduce the logical media; refusing to commit it.");
  }

  private static EwfWriter WriterFrom(EwfReader.EwfImage image, bool compress) {
    var fields = ReadAcquisitionFields(image);
    return new EwfWriter {
      CompressChunks = compress,
      Description = fields.GetValueOrDefault("a", ""),
      CaseNumber = fields.GetValueOrDefault("c", ""),
      EvidenceNumber = fields.GetValueOrDefault("n", ""),
      ExaminerName = fields.GetValueOrDefault("e", ""),
      Notes = fields.GetValueOrDefault("t", ""),
    };
  }

  private static bool HasCompressedChunks(EwfReader.EwfImage image) {
    var table = image.Sections.FirstOrDefault(s => s.Type is "table" or "table2");
    if (table == null || table.Payload.Length < 28) return false;
    var count = Math.Min(
      BinaryPrimitives.ReadUInt32LittleEndian(table.Payload.AsSpan(0)),
      (uint)Math.Max(0, (table.Payload.Length - 28) / 4));
    for (var i = 0; i < count; ++i)
      if ((BinaryPrimitives.ReadUInt32LittleEndian(table.Payload.AsSpan(24 + i * 4)) & 0x80000000U) != 0)
        return true;
    return false;
  }

  private static long MediaStoredBytes(EwfReader.EwfImage image)
    => image.Sections.FirstOrDefault(s => s.Type == "sectors")?.Payload.LongLength ?? -1;

  private static bool ParseBool(string? value, bool fallback)
    => bool.TryParse(value, out var parsed) ? parsed : fallback;

  private static string SafeNameSegment(string raw) {
    var sb = new StringBuilder(raw.Length);
    foreach (var c in raw)
      sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
    return sb.Length == 0 ? "unknown" : sb.ToString();
  }

  private static byte[] BuildMetadata(EwfReader.EwfImage image) {
    var sb = new StringBuilder();
    sb.AppendLine("[ewf]");
    sb.Append("signature = ").AppendLine(image.IsLogical ? "LVF (logical)" : "EVF (physical)");
    sb.Append(CultureInfo.InvariantCulture, $"segment_number = {image.SegmentNumber}\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size = {image.TotalFileSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"section_count = {image.Sections.Count}\n");
    if (!image.IsLogical) {
      try { sb.Append(CultureInfo.InvariantCulture, $"media_size = {EwfReader.ExtractMedia(image).LongLength}\n"); }
      catch { sb.AppendLine("media_size = unavailable"); }
    }

    var fields = ReadAcquisitionFields(image);
    if (fields.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("[acquisition]");
      foreach (var kv in fields) sb.Append(CultureInfo.InvariantCulture, $"{kv.Key} = {kv.Value}\n");
    }

    var hash = image.Sections.FirstOrDefault(s => s.Type == "hash");
    if (hash is { Payload.Length: >= 16 }) {
      sb.AppendLine();
      sb.AppendLine("[hash]");
      sb.Append("md5 = ").AppendLine(Convert.ToHexString(hash.Payload.AsSpan(0, 16)));
    }

    sb.AppendLine();
    sb.AppendLine("[sections]");
    for (var i = 0; i < image.Sections.Count; ++i) {
      var section = image.Sections[i];
      sb.Append(CultureInfo.InvariantCulture,
        $"section_{i:D2} = type={section.Type} offset={section.DescriptorOffset} size={section.SectionSize} next=0x{section.NextSectionOffset:X} checksum=0x{section.Checksum:X8}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static Dictionary<string, string> ReadAcquisitionFields(EwfReader.EwfImage image) {
    foreach (var section in image.Sections.Where(s => s.Type is "header2" or "header")) {
      foreach (var payload in CandidateHeaderPayloads(section.Payload)) {
        try {
          var text = payload.Length >= 2 && payload[0] == 0xFF && payload[1] == 0xFE
            ? Encoding.Unicode.GetString(payload, 2, payload.Length - 2)
            : Encoding.UTF8.GetString(payload);
          var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
          for (var line = 0; line + 1 < lines.Length; ++line) {
            if (!lines[line].Contains('\t')) continue;
            var keys = lines[line].Split('\t');
            var values = lines[line + 1].Split('\t');
            if (keys.Length < 2 || values.Length == 0) continue;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < Math.Min(keys.Length, values.Length); ++i) {
              var key = keys[i].Trim();
              if (key.Length > 0) result[key] = values[i].Trim();
            }
            if (result.Count > 0) return result;
          }
        } catch { }
      }
    }
    return [];
  }

  private static IReadOnlyList<byte[]> CandidateHeaderPayloads(byte[] payload) {
    var result = new List<byte[]>(2);
    try { result.Add(ZlibStream.Decompress(payload)); }
    catch { }
    result.Add(payload);
    return result;
  }
}

#pragma warning disable CS1591
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Numpy;

/// <summary>
/// Descriptor for NumPy's NPZ format — a ZIP archive whose entries are all
/// <c>.npy</c> array serializations. Detection is extension-based (NPZ has
/// no dedicated magic; its raw magic is the plain ZIP signature) and the
/// contents are surfaced as-is: one entry per enclosed <c>.npy</c>, plus a
/// <c>metadata.ini</c> summary of array names and byte sizes.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html</c> — numpy.lib.format — defines NPZ as a ZIP of .npy members</description></item>
///   <item><description><c>https://github.com/numpy/numpy</c> — canonical implementation (numpy.savez / numpy.load)</description></item>
///   <item><description><c>https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT</c> — PKWARE ZIP APPNOTE — the underlying container format</description></item>
/// </list>
/// </summary>
/// <remarks>
/// We read the ZIP central directory via <see cref="ZipArchive"/> rather than
/// via <c>FileFormat.Zip</c> so this project has no inter-format dependency;
/// the only container semantics needed are DEFLATE + stored entries, both of
/// which are handled by the BCL implementation.
/// </remarks>
public sealed class NpzFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => FileFormat.Zip.ZipLayoutMap.Enumerate(archive);


  public string Id => "Npz";
  public string DisplayName => "NumPy NPZ";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".npz";
  public IReadOnlyList<string> Extensions => [".npz"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // NPZ has no distinct magic; a PK signature with a .npz extension wins on extension-first
  // detection. We keep the MagicSignatures empty so the plain ZIP descriptor beats us by
  // default for generic .zip files.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NumPy NPZ — a ZIP archive containing one or more .npy arrays (typically arr_0.npy, arr_1.npy, …).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var result = new List<ArchiveEntryInfo>();
    var metaBytes = this.CollectEntries(stream, out var zipEntries);
    result.Add(new ArchiveEntryInfo(0, "metadata.ini", metaBytes.LongLength, metaBytes.LongLength,
      "stored", false, false, null, "Metadata"));
    for (var i = 0; i < zipEntries.Count; i++) {
      var e = zipEntries[i];
      result.Add(new ArchiveEntryInfo(
        i + 1, e.Name, e.Length, e.CompressedLength,
        "deflate", false, false, e.LastWriteTime.UtcDateTime, "NpyArray"));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var metaBytes = this.CollectEntries(stream, out var zipEntries);
    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", metaBytes);

    // Re-open the zip to extract — CollectEntries closed its ZipArchive.
    stream.Seek(0, SeekOrigin.Begin);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    foreach (var entry in archive.Entries) {
      if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(entry.FullName, files)) continue;

      using var src = entry.Open();
      using var buf = new MemoryStream();
      src.CopyTo(buf);
      WriteFile(outputDir, entry.FullName, buf.ToArray());
    }
  }

  /// <summary>
  /// WORM create — emits an NPZ (ZIP archive) where every non-directory input
  /// becomes one entry. Inputs whose name ends in <c>.npy</c> and whose bytes
  /// already carry the NPY magic are stored as-is; other inputs are wrapped
  /// in a minimal uint8 NPY frame on the fly and their archive name gets a
  /// <c>.npy</c> suffix appended when not already present.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var raw = i.ReadContent();
      var name = i.ArchiveName.Replace('\\', '/');
      var alreadyNpy = name.EndsWith(".npy", StringComparison.OrdinalIgnoreCase) && IsNpyPayload(raw);

      var entry = archive.CreateEntry(alreadyNpy ? name : EnsureNpySuffix(name), CompressionLevel.NoCompression);
      using var s = entry.Open();
      if (alreadyNpy) {
        s.Write(raw, 0, raw.Length);
      } else {
        NpyWriter.Write(s, raw);
      }
    }
  }

  private static bool IsNpyPayload(ReadOnlySpan<byte> data)
    => data.Length >= 6 &&
       data[0] == 0x93 && data[1] == (byte)'N' && data[2] == (byte)'U' &&
       data[3] == (byte)'M' && data[4] == (byte)'P' && data[5] == (byte)'Y';

  private static string EnsureNpySuffix(string name) =>
    name.EndsWith(".npy", StringComparison.OrdinalIgnoreCase) ? name : name + ".npy";

  private sealed record ZipEntrySummary(string Name, long Length, long CompressedLength, DateTimeOffset LastWriteTime);

  // Walks the zip CD, collects entry summaries, and returns the serialised metadata.ini bytes.
  private byte[] CollectEntries(Stream stream, out List<ZipEntrySummary> entries) {
    entries = [];
    var sb = new StringBuilder();
    sb.Append("[npz]\r\n");

    stream.Seek(0, SeekOrigin.Begin);
    try {
      using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
      var npyCount = 0;
      var otherCount = 0;
      foreach (var entry in archive.Entries) {
        if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
        entries.Add(new ZipEntrySummary(entry.FullName, entry.Length, entry.CompressedLength, entry.LastWriteTime));
        if (entry.FullName.EndsWith(".npy", StringComparison.OrdinalIgnoreCase)) npyCount++;
        else otherCount++;
      }
      sb.Append("parse_status=ok\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"entry_count={entries.Count}\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"npy_count={npyCount}\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"other_count={otherCount}\r\n");
      for (var i = 0; i < entries.Count; i++) {
        var e = entries[i];
        sb.Append(CultureInfo.InvariantCulture, $"[entry_{i:D3}]\r\n");
        sb.Append("name=").Append(e.Name).Append("\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"length={e.Length}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"compressed_length={e.CompressedLength}\r\n");
      }
    } catch (Exception ex) {
      sb.Append("parse_status=error\r\n");
      sb.Append("error=").Append(ex.Message).Append("\r\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

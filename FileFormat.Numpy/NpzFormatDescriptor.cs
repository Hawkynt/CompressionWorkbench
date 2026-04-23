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
/// </summary>
/// <remarks>
/// We read the ZIP central directory via <see cref="ZipArchive"/> rather than
/// via <c>FileFormat.Zip</c> so this project has no inter-format dependency;
/// the only container semantics needed are DEFLATE + stored entries, both of
/// which are handled by the BCL implementation.
/// </remarks>
public sealed class NpzFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Npz";
  public string DisplayName => "NumPy NPZ";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
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

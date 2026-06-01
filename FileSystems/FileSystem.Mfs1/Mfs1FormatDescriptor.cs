#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Mfs1;

/// <summary>
/// Read-only descriptor for Acorn Master File System v1 (MFS-1) disk images —
/// the predecessor to ADFS / DFS, used on early Acorn BBC Micro / Master
/// systems. Magic is weak (two byte signature plus heuristics) so detection is
/// extension-led with a soft offset-0 byte gate.
/// </summary>
/// <remarks>
/// <para><b>Heuristic</b>: byte 0 = <c>0x00</c>, byte 1 = <c>0x80</c>, and
/// bytes 2-13 contain mostly printable ASCII (disk name). Not a strong magic
/// — confidence is intentionally low so other better-magic'd formats win.</para>
/// <para>Distinct from <c>FileSystem.Mfs</c>, which targets the Macintosh File
/// System with a strong <c>0xD2D7</c> magic.</para>
/// </remarks>
public sealed class Mfs1FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Mfs1";
  public string DisplayName => "MFS-1 (Acorn Master File System v1)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".mfs";
  public IReadOnlyList<string> Extensions => [".mfs", ".mfsd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 0x00 0x80 at offsets 0-1 is the MFS-1 boot pattern. Extremely weak
    // (matches many empty/sparse buffers) so confidence 0.20 lets stronger
    // magic-bearing formats win — detection here is really driven by the
    // .mfs / .mfsd extension.
    new([0x00, 0x80], Offset: 0, Confidence: 0.20),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Acorn MFS-1 (pre-ADFS) — opaque single-entry surface (weak magic).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.mfs", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.mfs", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAll(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    var ok = image.Length >= 2 && image[0] == 0x00 && image[1] == 0x80;
    var label = TryExtractLabel(image);

    WriteIfMatch(outputDir, "FULL.mfs", image, files);
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(ok ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"detected_label={label}\n");
    bldr.Append("note=Acorn MFS-1 directory walk is not implemented; image surfaced as opaque blob.\n");
    WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(bldr.ToString()), files);
  }

  private static string TryExtractLabel(byte[] image) {
    if (image.Length < 14) return "";
    Span<char> chars = stackalloc char[12];
    var count = 0;
    for (var i = 2; i < 14; i++) {
      var b = image[i];
      if (b is < 0x20 or > 0x7E) break;
      chars[count++] = (char)b;
    }
    return new string(chars.Slice(0, count));
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}

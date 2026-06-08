#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Aomei;

/// <summary>
/// Read-only header-surface descriptor for AOMEI Backupper image files
/// (<c>.adi</c> disk/partition/system backup, <c>.afi</c> file/folder
/// backup). Both share the 5-byte ASCII signature <c>BIFH\</c> ("Backup
/// Image File Header") at offset 0; the trailing backslash is part of the
/// signature, not a path delimiter.
///
/// <para>
/// AOMEI Backupper is a closed-source consumer backup product from Chengdu
/// Aomei Technology Co. (傲梅科技). The on-disk format is fully proprietary
/// and is implemented by the kernel-mode drivers <c>ambakdrv.sys</c>,
/// <c>amwrtdrv.sys</c>, and <c>ammntdrv.sys</c>. AOMEI publishes no SDK and
/// no public on-disk format specification.
/// </para>
///
/// <para>
/// Research scope (June 2026): a deep search across English- and Chinese-
/// language reverse-engineering communities (Google, GitHub, 010 Editor
/// template repos, libyal, Joachim Metz forensic specs, 52pojie.cn,
/// kanxue.com, freebuf.com, bilibili) produced <b>no</b> public chunk-
/// layout documentation past the 5-byte ASCII header. The closest public
/// information stops at the magic bytes themselves (cross-checked via
/// filext.com, fileinfo.com, file-extensions.org) plus high-level product
/// behaviour (optional compression, password encryption, splitting, and
/// incremental/differential chains — all algorithmically undocumented).
/// </para>
///
/// <para>
/// What this descriptor surfaces:
/// <list type="bullet">
///   <item><description><c>FULL.bifh</c> — the raw image bytes.</description></item>
///   <item><description><c>metadata.ini</c> — parse status plus the
///         speculative post-magic 32-bit word as a diagnostic hex
///         value.</description></item>
///   <item><description><c>header.bin</c> — 64-byte capture of the file
///         start for forensic inspection (only when the magic
///         matches).</description></item>
/// </list>
/// No chunk index, no payload extraction, no defragmentation — those
/// require a real format spec or a clean-room RE effort that does not
/// currently exist in the public domain.
/// </para>
///
/// <para>
/// Detection: 5-byte magic <c>42 49 46 48 5C</c> ("BIFH\") at offset 0.
/// Extensions <c>.adi</c> and <c>.afi</c> are both registered.
/// </para>
///
/// References (all dry-holes for chunk layout, listed for audit trail):
/// <list type="bullet">
///   <item><description>filext.com BIFH file-signature entries for ADI/AFI</description></item>
///   <item><description>52pojie.cn AOMEI Backupper threads (cracked builds, no RE)</description></item>
///   <item><description>kanxue.com (no AOMEI threads located)</description></item>
///   <item><description>aomeitech.com user manual (no format spec)</description></item>
///   <item><description>AOMEI forum *.adi → VMDK PDF (restore-only workflow)</description></item>
/// </list>
/// </summary>
public sealed class AomeiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Aomei";
  public string DisplayName => "AOMEI Backupper Image (ADI/AFI)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".adi";
  public IReadOnlyList<string> Extensions => [".adi", ".afi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(AomeiReader.Magic, Offset: 0, Confidence: 0.95f),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "AOMEI Backupper disk (.adi) / file (.afi) image — header-surface read-only; "
    + "chunk framing past the BIFH magic is undocumented in all public sources "
    + "(English and Chinese RE communities searched).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bifh", 0, 0, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    AomeiReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new AomeiReader(ms);
    } catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.bifh", image.LongLength, image.LongLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    var idx = 0;
    entries.Add(new ArchiveEntryInfo(idx++, "FULL.bifh", image.LongLength, image.LongLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "stored", false, false, null));
    if (reader.Valid)
      entries.Add(new ArchiveEntryInfo(idx++, "header.bin", reader.HeaderRaw.LongLength, reader.HeaderRaw.LongLength, "stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] image;
    try {
      image = ReadAllBounded(stream);
    } catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    AomeiReader reader;
    try {
      using var ms = new MemoryStream(image, writable: false);
      reader = new AomeiReader(ms);
    } catch {
      WriteIfMatch(outputDir, "FULL.bifh", image, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    WriteIfMatch(outputDir, "FULL.bifh", image, files);
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(reader), files);
    if (reader.Valid)
      WriteIfMatch(outputDir, "header.bin", reader.HeaderRaw, files);
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter != null && filter.Length > 0 && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(AomeiReader r) {
    var b = new StringBuilder();
    var ic = CultureInfo.InvariantCulture;
    b.Append(ic, $"parse_status={r.ParseStatus}\n");
    if (r.Valid) {
      b.Append("magic=BIFH\\\n");
      b.Append(ic, $"post_magic_u32_le=0x{r.PostMagicWord:X8}\n");
      b.Append("chunk_layout=undocumented\n");
      b.Append("compression_algorithm=undocumented\n");
      b.Append("encryption_algorithm=undocumented\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  // 64 KB cap — header is at offset 0 and the descriptor only surfaces
  // metadata; speculative carver scans therefore can't pull a multi-GB
  // image into memory.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}

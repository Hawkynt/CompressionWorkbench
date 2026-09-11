#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nrg;

/// <summary>
/// Nero Burning ROM NRG disc image — a sector stream followed by a chunked
/// session/track descriptor and a trailing NERO/NER5 footer.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://cdemu.sourceforge.io</c> — CDEmu / libMirage NRG parser, used as a behavioural oracle for the reverse-engineered chunk layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/NRG_(file_format)</c> — format overview</description></item>
///   <item><description>No official public Nero specification is available; the format is proprietary and reverse-engineered</description></item>
/// </list>
/// </summary>
public sealed class NrgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
  IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable {

  private const uint CdRomMediumType = 0x00000400;

  /// <summary>Gets the id.</summary>
  public string Id => "Nrg";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "NRG";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".nrg";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".nrg"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  // NRG magic is a footer signature ("NER5" or "NERO" at a variable offset from EOF),
  // which cannot be represented as a fixed-offset MagicSignature.
  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description =>
    "Nero Burning ROM disc image (NRG v1/v2 reader; interoperable v2 TAO ISO writer; R/W and maintenance through verified rebuild)";

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new NrgReader(stream, leaveOpen: true);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(index, entry.FullPath, entry.Size,
      entry.Size, "iso9660", entry.IsDirectory, false, null)).ToList();
  }

  /// <summary>Decodes the supplied input.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new NrgReader(stream, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory)
        continue;
      if (files != null && !MatchesFilter(entry.FullPath, files))
        continue;
      WriteFile(outputDir, entry.FullPath, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Creates a single-session, single-track NRG v2 image containing a cooked
  /// 2,048-byte/sector ISO 9660 track. The trailer is a real NRG descriptor
  /// chain rather than merely a NER5 signature: ETN2 describes the TAO track,
  /// SINF records the session track count, MTYP marks CD-ROM media, and END!
  /// terminates the chunk list.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var input in inputs.Where(static input => !input.IsDirectory))
      iso.AddFile(input.ArchiveName.Replace('\\', '/'), input.ReadContent());

    var image = iso.Build();
    output.Write(image);

    var trailerOffset = checked((ulong)output.Position);

    Span<byte> etn2 = stackalloc byte[32];
    BinaryPrimitives.WriteUInt64BigEndian(etn2, 0); // track starts at file offset 0
    BinaryPrimitives.WriteUInt64BigEndian(etn2[8..], checked((ulong)image.LongLength));
    etn2[19] = 0x00; // Mode 1, cooked 2,048-byte user-data sectors
    BinaryPrimitives.WriteUInt32BigEndian(etn2[20..], 0); // first logical sector
    WriteChunk(output, "ETN2"u8, etn2);

    Span<byte> sinf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(sinf, 1); // one track in this session
    WriteChunk(output, "SINF"u8, sinf);

    Span<byte> mtyp = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(mtyp, CdRomMediumType);
    WriteChunk(output, "MTYP"u8, mtyp);

    WriteChunk(output, "END!"u8, ReadOnlySpan<byte>.Empty);

    Span<byte> footer = stackalloc byte[12];
    "NER5"u8.CopyTo(footer);
    BinaryPrimitives.WriteUInt64BigEndian(footer[4..], trailerOffset);
    output.Write(footer);
  }

  private static void WriteChunk(Stream output, ReadOnlySpan<byte> id, ReadOnlySpan<byte> payload) {
    if (id.Length != 4)
      throw new ArgumentException("NRG chunk ids are exactly four bytes.", nameof(id));

    Span<byte> header = stackalloc byte[8];
    id.CopyTo(header);
    BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)payload.Length));
    output.Write(header);
    output.Write(payload);
  }
}

#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Pseudo-archive descriptor for the TI-TXT firmware text format used by MSP430.
/// Address lines (<c>@HHHH</c>) introduce sparse byte runs; a line containing
/// <c>q</c> terminates the file. The archive view exposes the logical firmware as
/// <c>firmware.bin</c> plus a rendered <c>metadata.ini</c> summary.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://downloads.ti.com/docs/esd/SPRU513P/Content/SPRU513P_HTML/hex-conversion-utility-description.html</c> — TI Hex Conversion Utility, TI-TXT format</description></item>
///   <item><description><c>https://www.ti.com/lit/pdf/slau131</c> — Texas Instruments MSP430 programming documentation</description></item>
///   <item><description><c>https://srecord.sourceforge.net/man/man5/srec_ti_txt.5.html</c> — SRecord TI-TXT documentation; used only as an interoperability reference</description></item>
/// </list>
/// </summary>
public sealed class TiTxtFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
  IArchiveModifiable, IArchiveDefragmentable, IArchiveWriteConstraints, ISyntheticEntryNames {

  private static readonly IReadOnlySet<string> SyntheticNames =
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FirmwareHexWriter.MetadataName };

  public string Id => "TiTxt";
  public string DisplayName => "TI-TXT (MSP430)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".txt";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'@'], Confidence: 0.15)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Encoding;
  public string Description =>
    "TI-TXT MSP430 sparse firmware text; supports create, replace/remove, purge and canonical rebuild.";

  public IReadOnlySet<string> SyntheticEntryNames => SyntheticNames;

  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    if (!input.IsDirectory) {
      reason = null;
      return true;
    }

    reason = "TI-TXT accepts one firmware payload plus optional metadata.ini; directories are not representable.";
    return false;
  }

  public long? MaxTotalArchiveSize => null;
  public string AcceptedInputsDescription => "one firmware payload plus optional metadata.ini";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    FirmwareHexCommon.BuildArchiveEntries(BuildEntries(stream));

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ValidateInputs(inputs, out _, out _);
    FirmwareHexWriter.WriteTiTxt(output, FirmwareHexWriter.ImageFrom(inputs, "TiTxt"));
  }

  /// <summary>
  /// Replaces the single logical firmware payload and/or moves its base address.
  /// A metadata-only base-address edit shifts every sparse section by the same
  /// delta so address holes remain holes rather than becoming explicit 0xFF data.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    ValidateInputs(inputs, out var payload, out var metadata);
    if (payload is null && metadata is null) return;

    var current = ReadImage(archive);
    var baseAddress = metadata is null ? current.BaseAddress : ReadBaseAddress(metadata.ReadContent()) ?? current.BaseAddress;
    FirmwareImage replacement;

    if (payload is not null) {
      var bytes = payload.ReadContent();
      replacement = ImageFromSegments(
        bytes.Length == 0 ? [] : [(baseAddress, bytes)]);
    } else if (current.Segments.Count == 0 || baseAddress == current.BaseAddress) {
      replacement = current;
    } else {
      replacement = ShiftImage(current, baseAddress);
    }

    Rewrite(archive, replacement);
  }

  /// <summary>Removing the logical firmware payload leaves the valid empty TI-TXT form, <c>q</c>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (!entryNames.Any(name => Path.GetFileName(name).Equals(FirmwareHexWriter.PayloadName, StringComparison.OrdinalIgnoreCase)))
      return;
    Rewrite(archive, ImageFromSegments([]));
  }

  /// <summary>Erases all live firmware bytes while leaving a valid empty TI-TXT document.</summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    _ = ReadImage(archive); // reject malformed input before replacing it
    Rewrite(archive, ImageFromSegments([]));
  }

  /// <summary>
  /// Canonicalises section ordering and record wrapping without flattening sparse
  /// holes. The reader merges only exactly adjacent sections; the writer then
  /// emits one address record per contiguous run and 16 bytes per full data line.
  /// </summary>
  public void Defragment(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    Rewrite(archive, ReadImage(archive));
  }

  private static void ValidateInputs(IReadOnlyList<ArchiveInputInfo> inputs,
      out ArchiveInputInfo? payload, out ArchiveInputInfo? metadata) {
    payload = null;
    metadata = null;
    foreach (var input in inputs) {
      if (input.IsDirectory)
        throw new InvalidDataException("TI-TXT cannot store directories.");
      if (Path.GetFileName(input.ArchiveName).Equals(FirmwareHexWriter.MetadataName, StringComparison.OrdinalIgnoreCase)) {
        if (metadata is not null)
          throw new InvalidDataException("TI-TXT accepts at most one metadata.ini input.");
        metadata = input;
        continue;
      }
      if (payload is not null)
        throw new InvalidDataException("TI-TXT represents one logical firmware payload; multiple payload inputs are ambiguous.");
      payload = input;
    }
  }

  private static uint? ReadBaseAddress(byte[] metadata) {
    foreach (var raw in Encoding.UTF8.GetString(metadata).Split('\n')) {
      var line = raw.Trim();
      var equals = line.IndexOf('=', StringComparison.Ordinal);
      if (equals < 0 || !line[..equals].Trim().Equals("base_address", StringComparison.OrdinalIgnoreCase)) continue;
      var value = line[(equals + 1)..].Trim();
      if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
          uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        return parsed;
    }
    return null;
  }

  private static FirmwareImage ShiftImage(FirmwareImage image, uint newBaseAddress) {
    var delta = (long)newBaseAddress - image.BaseAddress;
    var shifted = new List<(uint Address, byte[] Data)>(image.Segments.Count);
    foreach (var (address, data) in image.Segments) {
      var target = (long)address + delta;
      if (target < 0 || target > uint.MaxValue || (ulong)target + (ulong)data.LongLength > (ulong)uint.MaxValue + 1)
        throw new InvalidDataException("TI-TXT base-address edit would move a section outside the 32-bit address space.");
      shifted.Add(((uint)target, data));
    }
    return ImageFromSegments(shifted);
  }

  private static FirmwareImage ImageFromSegments(List<(uint Address, byte[] Data)> segments) {
    var totalBytes = 0;
    for (var i = 0; i < segments.Count; ++i) checked { totalBytes += segments[i].Data.Length; }
    var gaps = 0;
    for (var i = 1; i < segments.Count; ++i)
      if ((ulong)segments[i - 1].Address + (ulong)segments[i - 1].Data.Length < segments[i].Address) gaps++;
    return new FirmwareImage(segments, StartAddress: null, RecordCount: 0, GapCount: gaps,
      TotalDataBytes: totalBytes, SourceFormat: "TiTxt");
  }

  private static FirmwareImage ReadImage(Stream stream) {
    if (!stream.CanRead) throw new ArgumentException("TI-TXT stream must be readable.", nameof(stream));
    if (stream.CanSeek) stream.Position = 0;
    using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
    return TiTxtReader.Read(reader.ReadToEnd());
  }

  private static void Rewrite(Stream archive, FirmwareImage image) {
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("TI-TXT mutation requires a readable, writable, seekable stream.", nameof(archive));

    using var staged = new MemoryStream();
    FirmwareHexWriter.WriteTiTxt(staged, image);
    staged.Position = 0;
    var reparsed = ReadImage(staged);
    if (!SameSparseImage(image, reparsed))
      throw new InvalidDataException("TI-TXT rewrite verification failed: sparse address/data semantics changed.");

    archive.Position = 0;
    archive.SetLength(0);
    staged.Position = 0;
    staged.CopyTo(archive);
    archive.SetLength(archive.Position);
    archive.Position = 0;
  }

  private static bool SameSparseImage(FirmwareImage expected, FirmwareImage actual) {
    if (expected.Segments.Count != actual.Segments.Count) return false;
    for (var i = 0; i < expected.Segments.Count; ++i) {
      var left = expected.Segments[i];
      var right = actual.Segments[i];
      if (left.Address != right.Address || !left.Data.AsSpan().SequenceEqual(right.Data)) return false;
    }
    return true;
  }

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) =>
    FirmwareHexCommon.BuildEntries(ReadImage(stream));
}

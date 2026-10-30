#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Cab;
using FileFormat.PeResources;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.IExpress;

/// <summary>
/// IExpress self-extractor (Windows' iexpress.exe / wextract.exe stub): a PE whose RT_RCDATA
/// resource "CABINET" holds a Microsoft Cabinet with the payload, next to text resources that
/// configure the run (TITLE, RUNPROGRAM, POSTRUNPROGRAM, LICENSE, FINISHMSG, ...).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/windows/win32/msi/cabinet-files</c> — the cabinet format</description></item>
///   <item><description><c>https://learn.microsoft.com/previous-versions/windows/it-pro/windows-server-2003/cc782932(v=ws.10)</c> — IExpress</description></item>
/// </list>
/// </summary>
public sealed class IExpressFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  private const ushort RtRcData = 10;
  private static readonly string[] TextResources = ["TITLE", "RUNPROGRAM", "POSTRUNPROGRAM", "ADMQCMD", "USRQCMD", "LICENSE", "FINISHMSG", "UPROMPT"];

  public string Id => "IExpress";
  public string DisplayName => "IExpress self-extractor";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  // Detected by its CABINET resource (FormatDetector.DetectInstaller), never by ".exe".
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("mszip", "MSZIP"), new("lzx", "LZX"), new("quantum", "Quantum"), new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "IExpress / wextract self-extracting executable: the files of its embedded cabinet";

  /// <summary>True when the PE carries an RT_RCDATA "CABINET" resource that starts with "MSCF".</summary>
  public static bool IsIExpress(Stream stream) {
    if (PeResourceLocator.Find(stream, RtRcData, "CABINET") is not { Size: >= 36 } at)
      return false;
    Span<byte> magic = stackalloc byte[4];
    stream.Position = at.Offset;
    return stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4 && magic.SequenceEqual("MSCF"u8);
  }

  /// <summary>Returns the embedded cabinet's bytes, or null when there is none.</summary>
  public static byte[]? ReadCabinet(Stream stream) => ReadResource(stream, "CABINET");

  private static byte[]? ReadResource(Stream stream, string name) {
    if (PeResourceLocator.Find(stream, RtRcData, name) is not { } at)
      return null;
    var data = new byte[at.Size];
    stream.Position = at.Offset;
    stream.ReadExactly(data);
    return data;
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    byte[]? cab;
    try {
      cab = ReadCabinet(stream);
    } catch (Exception ex) when (ex is IOException or InvalidDataException) {
      cab = null;
    }
    var metadata = Metadata(stream, cab == null ? "no CABINET resource" : null);
    var result = new List<ArchiveEntryInfo> { new(0, "iexpress.ini", metadata.Length, metadata.Length, "stored", false, false, null, Kind: "metadata") };
    if (cab == null)
      return result;
    try {
      using var reader = new CabReader(new MemoryStream(cab, writable: false));
      result.AddRange(reader.Entries.Select((e, i) => new ArchiveEntryInfo(i + 1, e.FileName, e.UncompressedSize, -1, "cab", false, false, e.LastModified)));
    } catch (InvalidDataException) {
      // The metadata entry still describes the package.
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var cab = ReadCabinet(stream);
    if (files == null || MatchesFilter("iexpress.ini", files))
      WriteFile(outputDir, "iexpress.ini", Metadata(stream, cab == null ? "no CABINET resource" : null));
    if (cab == null)
      return;
    using var reader = new CabReader(new MemoryStream(cab, writable: false));
    foreach (var e in reader.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files))
        continue;
      WriteFile(outputDir, e.FileName, reader.ExtractEntry(e));
    }
  }

  private static byte[] Metadata(Stream stream, string? error) {
    var sb = new StringBuilder("[iexpress]\n");
    sb.Append("parse_status=").Append(error == null ? "ok" : "partial").Append('\n');
    if (error != null)
      sb.Append("error=").Append(error).Append('\n');
    foreach (var name in TextResources) {
      byte[]? raw;
      try {
        raw = ReadResource(stream, name);
      } catch (IOException) {
        raw = null;
      }
      if (raw == null)
        continue;
      // The stub stores these as ANSI strings with a terminating NUL.
      var text = Encoding.Latin1.GetString(raw).TrimEnd('\0').Replace("\r", "\\r").Replace("\n", "\\n");
      sb.Append(name.ToLowerInvariant()).Append('=').Append(text).Append('\n');
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

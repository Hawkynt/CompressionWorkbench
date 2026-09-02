#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Numpy;

/// <summary>
/// Pseudo-archive descriptor for NumPy's NPY array serialization format.
/// Splits an <c>.npy</c> file into <c>metadata.ini</c> (dtype, shape, header-length,
/// version, fortran order) and <c>array.bin</c> (the raw payload bytes after the
/// header).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://numpy.org/doc/stable/reference/generated/numpy.lib.format.html</c> — the NPY format specification (numpy.lib.format)</description></item>
///   <item><description><c>https://github.com/numpy/numpy</c> — canonical implementation</description></item>
/// </list>
/// </summary>
/// <remarks>
/// NPY magic is 6 bytes (<c>\x93NUMPY</c>) + 2-byte version + header length,
/// followed by an ASCII Python-dict header and raw array bytes. Supports v1
/// (u16 header length), v2 (u32), and v3 (UTF-8 dict, otherwise identical to v2).
/// </remarks>
public sealed class NpyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Npy";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "NumPy NPY";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".npy";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".npy"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'], Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "NumPy NPY array serialization (v1/v2/v3); surfaces dtype + shape + raw array bytes.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null, e.Kind))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in this.BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// WORM create — concatenates every input's bytes into a single uint8 NPY
  /// array. When exactly one input is supplied and it is itself a valid NPY
  /// file, it is written through verbatim so callers can round-trip an
  /// existing array. The dtype/shape can be overridden via
  /// <see cref="FormatCreateOptions"/> keys <c>npy_dtype</c>, <c>npy_shape</c>
  /// and <c>npy_fortran_order</c>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    // Concatenate every file input's bytes — non-file inputs (directories) are skipped.
    using var ms = new MemoryStream();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var data = i.ReadContent();
      ms.Write(data, 0, data.Length);
    }
    var payload = ms.ToArray();

    // If the single input is itself a valid NPY, copy it through unchanged.
    if (inputs.Count == 1 && !inputs[0].IsDirectory && IsNpyPayload(payload)) {
      output.Write(payload);
      return;
    }

    // If the inputs were extracted by our own descriptor (header.bin + array.bin),
    // honour the embedded header by writing header.bin then array.bin in order.
    var header = inputs.FirstOrDefault(i => !i.IsDirectory &&
      string.Equals(i.ArchiveName, "header.bin", StringComparison.OrdinalIgnoreCase));
    var array = inputs.FirstOrDefault(i => !i.IsDirectory &&
      string.Equals(i.ArchiveName, "array.bin", StringComparison.OrdinalIgnoreCase));
    if (header != null && array != null) {
      var headerBytes = header.ReadContent();
      var arrayBytes = array.ReadContent();
      if (IsNpyPayload(headerBytes)) {
        output.Write(headerBytes);
        output.Write(arrayBytes);
        return;
      }
    }

    var dtype = options?.GetOption("npy_dtype", NpyWriter.DefaultDtype) ?? NpyWriter.DefaultDtype;
    var shape = options?.GetOption("npy_shape", string.Empty);
    var fortran = options?.GetOptionBool("npy_fortran_order", false) ?? false;

    NpyWriter.Write(output, payload, dtype, string.IsNullOrEmpty(shape) ? null : shape, fortran);
  }

  private static bool IsNpyPayload(ReadOnlySpan<byte> data)
    => data.Length >= 6 &&
       data[0] == 0x93 && data[1] == (byte)'N' && data[2] == (byte)'U' &&
       data[3] == (byte)'M' && data[4] == (byte)'P' && data[5] == (byte)'Y';

  private List<(string Name, byte[] Data, string Kind)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);

    var result = new List<(string, byte[], string)>();
    try {
      var arr = NpyReader.Read(span);
      result.Add(("metadata.ini", BuildMetadata(arr, ms.Length), "Metadata"));
      result.Add(("header.bin", arr.HeaderBytes, "Header"));
      result.Add(("array.bin", arr.ArrayBytes, "Payload"));
    } catch (Exception ex) {
      var sb = new StringBuilder();
      sb.Append("[npy]\r\n");
      sb.Append("parse_status=error\r\n");
      sb.Append("file_size=").Append(ms.Length).Append("\r\n");
      sb.Append("error=").Append(ex.Message).Append("\r\n");
      result.Add(("metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()), "Metadata"));
    }
    return result;
  }

  private static byte[] BuildMetadata(NpyReader.NpyArray a, long fileSize) {
    var sb = new StringBuilder();
    sb.Append("[npy]\r\n");
    sb.Append("parse_status=ok\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size={fileSize}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"version={a.MajorVersion}.{a.MinorVersion}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_len={a.HeaderLength}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"header_bytes={a.HeaderBytes.Length}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"array_bytes={a.ArrayBytes.Length}\r\n");
    sb.Append("dtype=").Append(a.Dtype ?? "(unknown)").Append("\r\n");
    sb.Append("shape=").Append(a.Shape ?? "(unknown)").Append("\r\n");
    sb.Append("fortran_order=").Append(a.FortranOrder ? "true" : "false").Append("\r\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}

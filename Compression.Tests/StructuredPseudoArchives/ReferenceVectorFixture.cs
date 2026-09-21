using System.IO.Compression;
using System.Reflection;
using System.Text;
using Compression.Registry;

namespace Compression.Tests.StructuredPseudoArchives;

/// <summary>
/// Access to the embedded reference vectors in <c>StructuredPseudoArchives/ReferenceVectors</c>.
///
/// Every vector was produced by the format's own reference implementation, never by
/// CompressionWorkbench. The two directions they support are what the structured pseudo-archive
/// formats are gated on: <see cref="Load"/> feeds a third party's bytes to our reader, and the
/// writer tests compare our bytes against a third party's for the same value. A round trip through
/// our own writer and our own reader is excluded on purpose -- it cannot see a defect the two
/// share, which is the failure CONTRIBUTING.md calls the mutual-compensation trap.
/// </summary>
internal static class ReferenceVectorFixture {
  /// <summary>The three inputs every writer-parity vector encodes, in the order
  /// <see cref="StructuredArchiveInputs"/> hands them over.</summary>
  public static readonly byte[] FileBin = [0x00, 0x01, 0x7f, 0x80, 0xff, 0x0a];

  public static readonly byte[] LongBin = [.. Enumerable.Range(0, 64).Select(x => (byte)x)];

  public static readonly byte[] TopBin = [0xde, 0xad, 0xbe, 0xef];

  public static IReadOnlyList<ArchiveInputInfo> StructuredArchiveInputs => [
    ArchiveInputInfo.InMemory("dir/file.bin", FileBin),
    ArchiveInputInfo.InMemory("dir/long.bin", LongBin),
    ArchiveInputInfo.InMemory("top.bin", TopBin),
  ];

  /// <summary>Reads one embedded vector, transparently gunzipping the ones stored compressed.</summary>
  public static byte[] Load(string name) {
    var resource = $"ReferenceVectors.{name}";
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
      ?? throw new FileNotFoundException($"Embedded reference vector '{resource}' is missing from the test assembly.");

    using var buffer = new MemoryStream();
    if (name.EndsWith(".gz", StringComparison.Ordinal)) {
      using var gzip = new GZipStream(stream, CompressionMode.Decompress);
      gzip.CopyTo(buffer);
    } else {
      stream.CopyTo(buffer);
    }

    return buffer.ToArray();
  }

  /// <summary>Runs a descriptor's <c>Create</c> over <see cref="StructuredArchiveInputs"/>.</summary>
  public static byte[] Create(IArchiveCreatable descriptor) {
    using var output = new MemoryStream();
    descriptor.Create(output, [.. StructuredArchiveInputs], new FormatCreateOptions());
    return output.ToArray();
  }

  public static List<ArchiveEntryInfo> List(IArchiveFormatOperations descriptor, byte[] bytes) {
    using var stream = new MemoryStream(bytes, writable: false);
    return descriptor.List(stream, null);
  }

  public static byte[] Extract(IArchiveInMemoryExtract descriptor, byte[] bytes, string entryName) {
    using var input = new MemoryStream(bytes, writable: false);
    using var output = new MemoryStream();
    descriptor.ExtractEntry(input, entryName, output, null);
    return output.ToArray();
  }

  public static string ExtractText(IArchiveInMemoryExtract descriptor, byte[] bytes, string entryName)
    => Encoding.UTF8.GetString(Extract(descriptor, bytes, entryName));

  /// <summary>Renders a byte mismatch as hex so a failing parity assertion names the offending byte.</summary>
  public static string Hex(byte[] value) => Convert.ToHexString(value);
}

#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Onnx;

/// <summary>
/// Read-only descriptor for ONNX neural-network model files. Parses the
/// protobuf-serialized <c>ModelProto</c> via the in-tree minimal reader and
/// surfaces: <c>metadata.ini</c> (IR version, producer, opsets, input/output
/// shapes, op counts), <c>ops.txt</c> (one line per graph operation), and one
/// <c>initializers/{name}.bin</c> per weight tensor.
/// </summary>
/// <remarks>
/// ONNX has no fixed magic — the file starts with protobuf wire-format tags.
/// The most common first byte pair is <c>0x08 0x01</c> (field 1
/// <c>ir_version</c>, varint, value 1+), which we register as a very-low-
/// confidence hint. Primary detection is extension-based.
/// Reference: <c>https://github.com/onnx/onnx/blob/main/onnx/onnx.proto</c>.
/// </remarks>
public sealed class OnnxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Onnx";
  public string DisplayName => "ONNX";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".onnx";
  public IReadOnlyList<string> Extensions => [".onnx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Very low confidence — ir_version tag (field 1, varint) + a small version value.
    // Real detection is extension-based; this is just a soft hint.
    new([0x08], Confidence: 0.05),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "ONNX (Open Neural Network Exchange) model; extracts the ModelProto's metadata, " +
    "operator list, and initializer weight tensors.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    this.BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null, e.Kind))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in this.BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private List<(string Name, byte[] Data, string Kind)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);

    var result = new List<(string, byte[], string)>();
    try {
      var model = OnnxReader.Read(span);
      result.Add(("metadata.ini", BuildMetadata(model, ms.Length), "Metadata"));
      result.Add(("ops.txt", BuildOpsText(model), "Index"));

      var seenNames = new HashSet<string>(StringComparer.Ordinal);
      for (var i = 0; i < model.Initializers.Count; i++) {
        var t = model.Initializers[i];
        var safe = SanitizeTensorName(t.Name, i);
        if (!seenNames.Add(safe)) safe = $"{safe}_{i}";
        var entryName = $"initializers/{safe}.bin";
        result.Add((entryName, t.RawData, "Tensor"));
      }
    } catch (Exception ex) {
      var sb = new StringBuilder();
      sb.Append("[onnx]\r\n");
      sb.Append("parse_status=error\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"file_size={ms.Length}\r\n");
      sb.Append("error=").Append(ex.Message).Append("\r\n");
      result.Add(("metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()), "Metadata"));
    }
    return result;
  }

  private static byte[] BuildMetadata(OnnxReader.Model m, long fileSize) {
    var sb = new StringBuilder();
    sb.Append("[onnx]\r\n");
    sb.Append("parse_status=ok\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size={fileSize}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"ir_version={m.IrVersion}\r\n");
    sb.Append("producer_name=").Append(m.ProducerName).Append("\r\n");
    sb.Append("producer_version=").Append(m.ProducerVersion).Append("\r\n");
    sb.Append("domain=").Append(m.Domain).Append("\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"model_version={m.ModelVersion}\r\n");
    sb.Append("graph_name=").Append(m.GraphName).Append("\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"op_count={m.Operators.Count}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"initializer_count={m.Initializers.Count}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"input_count={m.Inputs.Count}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"output_count={m.Outputs.Count}\r\n");

    for (var i = 0; i < m.OpsetImports.Count; i++) {
      var o = m.OpsetImports[i];
      sb.Append(CultureInfo.InvariantCulture, $"[opset_{i}]\r\n");
      sb.Append("domain=").Append(string.IsNullOrEmpty(o.Domain) ? "(default)" : o.Domain).Append("\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"version={o.Version}\r\n");
    }
    for (var i = 0; i < m.Inputs.Count; i++) {
      var v = m.Inputs[i];
      sb.Append(CultureInfo.InvariantCulture, $"[input_{i}]\r\n");
      sb.Append("name=").Append(v.Name).Append("\r\n");
      sb.Append("elem_type=").Append(OnnxReader.DataTypeName(v.ElementType)).Append("\r\n");
      sb.Append("dims=").Append(string.Join(",", v.Dims)).Append("\r\n");
    }
    for (var i = 0; i < m.Outputs.Count; i++) {
      var v = m.Outputs[i];
      sb.Append(CultureInfo.InvariantCulture, $"[output_{i}]\r\n");
      sb.Append("name=").Append(v.Name).Append("\r\n");
      sb.Append("elem_type=").Append(OnnxReader.DataTypeName(v.ElementType)).Append("\r\n");
      sb.Append("dims=").Append(string.Join(",", v.Dims)).Append("\r\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static byte[] BuildOpsText(OnnxReader.Model m) {
    var sb = new StringBuilder();
    foreach (var op in m.Operators) {
      sb.Append(op.OpType);
      if (!string.IsNullOrEmpty(op.Name)) sb.Append("  name=").Append(op.Name);
      if (!string.IsNullOrEmpty(op.Domain)) sb.Append("  domain=").Append(op.Domain);
      if (op.Inputs.Count > 0) sb.Append("  in=(").Append(string.Join(",", op.Inputs)).Append(')');
      if (op.Outputs.Count > 0) sb.Append("  out=(").Append(string.Join(",", op.Outputs)).Append(')');
      sb.Append("\r\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string SanitizeTensorName(string name, int index) {
    if (string.IsNullOrWhiteSpace(name)) return $"tensor_{index:D4}";
    var sb = new StringBuilder(name.Length);
    foreach (var c in name) {
      if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
          (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.')
        sb.Append(c);
      else
        sb.Append('_');
    }
    return sb.Length == 0 ? $"tensor_{index:D4}" : sb.ToString();
  }
}

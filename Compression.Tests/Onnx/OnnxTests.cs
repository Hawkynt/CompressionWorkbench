using System.Text;
using FileFormat.Onnx;

namespace Compression.Tests.Onnx;

[TestFixture]
public class OnnxTests {

  // ── Minimal protobuf writer (varint + length-delimited only) ────────

  private static void WriteVarint(MemoryStream ms, ulong v) {
    while (v >= 0x80) { ms.WriteByte((byte)((v & 0x7F) | 0x80)); v >>= 7; }
    ms.WriteByte((byte)v);
  }

  private static void WriteTag(MemoryStream ms, int fieldNumber, int wireType)
    => WriteVarint(ms, (ulong)((fieldNumber << 3) | wireType));

  private static void WriteLengthDelimited(MemoryStream ms, int fieldNumber, byte[] payload) {
    WriteTag(ms, fieldNumber, 2);
    WriteVarint(ms, (ulong)payload.Length);
    ms.Write(payload);
  }

  private static void WriteVarintField(MemoryStream ms, int fieldNumber, ulong v) {
    WriteTag(ms, fieldNumber, 0);
    WriteVarint(ms, v);
  }

  private static void WriteStringField(MemoryStream ms, int fieldNumber, string s)
    => WriteLengthDelimited(ms, fieldNumber, Encoding.UTF8.GetBytes(s));

  // TensorProto: 1 dims, 2 data_type, 8 name, 9 raw_data
  private static byte[] BuildTensor(string name, int dataType, long[] dims, byte[] rawData) {
    using var ms = new MemoryStream();
    foreach (var d in dims) WriteVarintField(ms, 1, (ulong)d);
    WriteVarintField(ms, 2, (ulong)dataType);
    WriteStringField(ms, 8, name);
    WriteLengthDelimited(ms, 9, rawData);
    return ms.ToArray();
  }

  // NodeProto: 1 input, 2 output, 3 name, 4 op_type, 7 domain
  private static byte[] BuildNode(string opType, string name = "", string[]? inputs = null, string[]? outputs = null) {
    using var ms = new MemoryStream();
    foreach (var inp in inputs ?? []) WriteStringField(ms, 1, inp);
    foreach (var outp in outputs ?? []) WriteStringField(ms, 2, outp);
    if (!string.IsNullOrEmpty(name)) WriteStringField(ms, 3, name);
    WriteStringField(ms, 4, opType);
    return ms.ToArray();
  }

  // GraphProto: 1 node, 2 name, 5 initializer, 11 input, 12 output
  private static byte[] BuildGraph(string name, byte[][] nodes, byte[][] initializers) {
    using var ms = new MemoryStream();
    foreach (var n in nodes) WriteLengthDelimited(ms, 1, n);
    WriteStringField(ms, 2, name);
    foreach (var t in initializers) WriteLengthDelimited(ms, 5, t);
    return ms.ToArray();
  }

  // OperatorSetIdProto: 1 domain, 2 version
  private static byte[] BuildOpset(string domain, long version) {
    using var ms = new MemoryStream();
    WriteStringField(ms, 1, domain);
    WriteVarintField(ms, 2, (ulong)version);
    return ms.ToArray();
  }

  // ModelProto: 1 ir_version, 2 producer_name, 3 producer_version, 7 graph, 8 opset_import
  private static byte[] BuildModel(long irVersion, string producer, byte[] graph, byte[][] opsets) {
    using var ms = new MemoryStream();
    WriteVarintField(ms, 1, (ulong)irVersion);
    WriteStringField(ms, 2, producer);
    WriteStringField(ms, 3, "1.0");
    WriteLengthDelimited(ms, 7, graph);
    foreach (var o in opsets) WriteLengthDelimited(ms, 8, o);
    return ms.ToArray();
  }

  // ── Reader tests ────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void OnnxReader_ParsesSimpleModel() {
    var weights = new byte[12];
    for (var i = 0; i < weights.Length; i++) weights[i] = (byte)(0x10 + i);

    var tensor = BuildTensor("W", dataType: 1, dims: [3], rawData: weights);
    var node = BuildNode("Relu", "relu_0", inputs: ["x"], outputs: ["y"]);
    var graph = BuildGraph("g", nodes: [node], initializers: [tensor]);
    var opset = BuildOpset("", version: 14);
    var model = BuildModel(irVersion: 7, producer: "CompressionWorkbench", graph, opsets: [opset]);

    var parsed = OnnxReader.Read(model);
    Assert.Multiple(() => {
      Assert.That(parsed.IrVersion, Is.EqualTo(7));
      Assert.That(parsed.ProducerName, Is.EqualTo("CompressionWorkbench"));
      Assert.That(parsed.GraphName, Is.EqualTo("g"));
      Assert.That(parsed.Operators, Has.Count.EqualTo(1));
      Assert.That(parsed.Operators[0].OpType, Is.EqualTo("Relu"));
      Assert.That(parsed.Initializers, Has.Count.EqualTo(1));
      Assert.That(parsed.Initializers[0].Name, Is.EqualTo("W"));
      Assert.That(parsed.Initializers[0].Dims, Is.EqualTo(new long[] { 3 }).AsCollection);
      Assert.That(parsed.Initializers[0].RawData, Is.EqualTo(weights).AsCollection);
      Assert.That(parsed.OpsetImports, Has.Count.EqualTo(1));
      Assert.That(parsed.OpsetImports[0].Version, Is.EqualTo(14));
    });
  }

  [Test, Category("HappyPath")]
  public void ProtobufReader_LooksLikeProtobuf_AcceptsRealModel() {
    var graph = BuildGraph("g", nodes: [], initializers: []);
    var model = BuildModel(7, "x", graph, opsets: []);
    Assert.That(ProtobufReader.LooksLikeProtobuf(model), Is.True);
  }

  [Test, Category("EdgeCase")]
  public void ProtobufReader_LooksLikeProtobuf_RejectsGarbage() {
    // A sequence starting with a tag whose wire type is 7 (invalid) should fail.
    var bogus = new byte[] { 0x0F, 0xFF, 0xFF };
    Assert.That(ProtobufReader.LooksLikeProtobuf(bogus), Is.False);
  }

  [Test, Category("EdgeCase")]
  public void OnnxReader_EmptyModel_ReturnsDefaults() {
    var parsed = OnnxReader.Read(ReadOnlySpan<byte>.Empty);
    Assert.Multiple(() => {
      Assert.That(parsed.IrVersion, Is.EqualTo(0));
      Assert.That(parsed.Operators, Is.Empty);
      Assert.That(parsed.Initializers, Is.Empty);
    });
  }

  // ── Descriptor tests ────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void OnnxDescriptor_ListEmitsMetadataOpsAndInitializer() {
    var weights = new byte[8];
    var tensor = BuildTensor("W", dataType: 1, dims: [2], rawData: weights);
    var node = BuildNode("Add", "add_0");
    var graph = BuildGraph("g", nodes: [node], initializers: [tensor]);
    var model = BuildModel(7, "p", graph, opsets: [BuildOpset("", 11)]);

    using var ms = new MemoryStream(model);
    var entries = new OnnxFormatDescriptor().List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("ops.txt"));
      Assert.That(names.Any(n => n.StartsWith("initializers/", StringComparison.Ordinal)), Is.True);
    });
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void OnnxDescriptor_ExtractWritesInitializerBytes() {
    var weights = new byte[10];
    for (var i = 0; i < weights.Length; i++) weights[i] = (byte)(0xA0 + i);
    var tensor = BuildTensor("layer.weight", dataType: 1, dims: [10], rawData: weights);
    var graph = BuildGraph("g", nodes: [], initializers: [tensor]);
    var model = BuildModel(7, "p", graph, opsets: []);

    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(model);
      new OnnxFormatDescriptor().Extract(ms, tmp, null, null);
      // Name is sanitized: '.' is allowed, so "layer.weight" stays intact.
      var path = Path.Combine(tmp, "initializers", "layer.weight.bin");
      Assert.That(File.Exists(path), Is.True);
      Assert.That(File.ReadAllBytes(path), Is.EqualTo(weights).AsCollection);
      Assert.That(File.Exists(Path.Combine(tmp, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(tmp, "ops.txt")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void OnnxDescriptor_TruncatedInput_EmitsErrorMetadata() {
    // A 1-byte buffer starts a varint and then EOF mid-varint — reader should throw.
    var bogus = new byte[] { 0x80 };
    using var ms = new MemoryStream(bogus);
    var entries = new OnnxFormatDescriptor().List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}

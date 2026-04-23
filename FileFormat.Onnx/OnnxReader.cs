#pragma warning disable CS1591

namespace FileFormat.Onnx;

/// <summary>
/// Minimal read-only parser for ONNX <c>ModelProto</c> files. Extracts the
/// fields needed for metadata surfacing + tensor extraction without pulling
/// in a full protobuf library: IR version, producer info, opset imports,
/// graph-level inputs/outputs, operator list, and each <c>initializer</c>
/// tensor's raw bytes (either the inline <c>raw_data</c> field or a
/// reconstructed little-endian serialization of one of the typed arrays).
/// </summary>
/// <remarks>
/// Schema reference: <c>https://github.com/onnx/onnx/blob/main/onnx/onnx.proto</c>.
/// ModelProto field numbers used here:
///   1 ir_version (varint), 2 producer_name (string), 3 producer_version (string),
///   4 domain (string), 5 model_version (varint), 6 doc_string (string),
///   7 graph (GraphProto), 8 opset_import (OperatorSetIdProto, repeated).
/// GraphProto field numbers: 1 node (repeated NodeProto), 2 name (string),
/// 5 initializer (repeated TensorProto), 10 doc_string (string),
/// 11 input (repeated ValueInfoProto), 12 output (repeated ValueInfoProto).
/// </remarks>
public sealed class OnnxReader {

  public sealed record OpsetImport(string Domain, long Version);
  public sealed record Operator(string OpType, string Domain, string Name, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);
  public sealed record Tensor(string Name, int DataType, long[] Dims, byte[] RawData);
  public sealed record ValueInfo(string Name, int ElementType, long[] Dims);

  public sealed record Model(
    long IrVersion,
    string ProducerName,
    string ProducerVersion,
    string Domain,
    long ModelVersion,
    string DocString,
    string GraphName,
    IReadOnlyList<OpsetImport> OpsetImports,
    IReadOnlyList<Operator> Operators,
    IReadOnlyList<Tensor> Initializers,
    IReadOnlyList<ValueInfo> Inputs,
    IReadOnlyList<ValueInfo> Outputs
  );

  /// <summary>Parses an in-memory ONNX <c>ModelProto</c>.</summary>
  public static Model Read(ReadOnlySpan<byte> data) {
    long ir = 0, modelVersion = 0;
    string producer = "", producerVersion = "", domain = "", docString = "", graphName = "";
    var opsets = new List<OpsetImport>();
    var ops = new List<Operator>();
    var inits = new List<Tensor>();
    var inputs = new List<ValueInfo>();
    var outputs = new List<ValueInfo>();

    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1: ir = (long)reader.ReadVarint(); break;
        case 2: producer = reader.ReadString(); break;
        case 3: producerVersion = reader.ReadString(); break;
        case 4: domain = reader.ReadString(); break;
        case 5: modelVersion = (long)reader.ReadVarint(); break;
        case 6: docString = reader.ReadString(); break;
        case 7:
          // GraphProto (length-delimited).
          var gSlice = reader.ReadBytes();
          ParseGraph(gSlice, out graphName, ops, inits, inputs, outputs);
          break;
        case 8:
          // repeated OperatorSetIdProto.
          var opsetSlice = reader.ReadBytes();
          opsets.Add(ParseOpset(opsetSlice));
          break;
        default:
          reader.SkipField(wt);
          break;
      }
    }

    return new Model(ir, producer, producerVersion, domain, modelVersion, docString, graphName,
      opsets, ops, inits, inputs, outputs);
  }

  // ── GraphProto ──────────────────────────────────────────────────────

  private static void ParseGraph(
    ReadOnlySpan<byte> data,
    out string name,
    List<Operator> ops,
    List<Tensor> inits,
    List<ValueInfo> inputs,
    List<ValueInfo> outputs
  ) {
    name = "";
    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1: ops.Add(ParseNode(reader.ReadBytes())); break;
        case 2: name = reader.ReadString(); break;
        case 5: inits.Add(ParseTensor(reader.ReadBytes())); break;
        case 11: inputs.Add(ParseValueInfo(reader.ReadBytes())); break;
        case 12: outputs.Add(ParseValueInfo(reader.ReadBytes())); break;
        default: reader.SkipField(wt); break;
      }
    }
  }

  // NodeProto: 1 input (repeated string), 2 output (repeated string), 3 name, 4 op_type, 7 domain, 5 attribute, 6 doc_string
  private static Operator ParseNode(ReadOnlySpan<byte> data) {
    var inputs = new List<string>();
    var outputs = new List<string>();
    string name = "", opType = "", domain = "";
    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1: inputs.Add(reader.ReadString()); break;
        case 2: outputs.Add(reader.ReadString()); break;
        case 3: name = reader.ReadString(); break;
        case 4: opType = reader.ReadString(); break;
        case 7: domain = reader.ReadString(); break;
        default: reader.SkipField(wt); break;
      }
    }
    return new Operator(opType, domain, name, inputs, outputs);
  }

  // TensorProto: 1 dims (repeated int64, packed or unpacked), 2 data_type (int32),
  // 8 name (string), 9 raw_data (bytes), plus typed arrays: 4 float_data, 5 int32_data,
  // 6 string_data, 7 int64_data, 10 double_data, 11 uint64_data.
  // For extraction we prefer raw_data when present; otherwise we serialize the typed array to LE bytes.
  private static Tensor ParseTensor(ReadOnlySpan<byte> data) {
    var dims = new List<long>();
    var dataType = 0;
    var name = "";
    byte[]? raw = null;

    // Collect typed arrays lazily — most tensors use raw_data in practice.
    List<float>? floatData = null;
    List<int>? int32Data = null;
    List<long>? int64Data = null;
    List<double>? doubleData = null;

    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1:
          // dims — packed (wire 2) or unpacked (wire 0). Handle both.
          if (wt == ProtobufReader.WireLengthDelimited) {
            var packed = reader.ReadBytes();
            var pr = new ProtobufReader(packed);
            while (!pr.AtEnd) dims.Add((long)pr.ReadVarint());
          } else dims.Add((long)reader.ReadVarint());
          break;
        case 2: dataType = (int)reader.ReadVarint(); break;
        case 4:
          floatData ??= [];
          if (wt == ProtobufReader.WireLengthDelimited) {
            var packed = reader.ReadBytes();
            for (var i = 0; i + 4 <= packed.Length; i += 4)
              floatData.Add(System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(packed[i..]));
          } else floatData.Add(BitConverter.Int32BitsToSingle((int)reader.ReadFixed32()));
          break;
        case 5:
          int32Data ??= [];
          if (wt == ProtobufReader.WireLengthDelimited) {
            var packed = reader.ReadBytes();
            var pr = new ProtobufReader(packed);
            while (!pr.AtEnd) int32Data.Add((int)pr.ReadVarint());
          } else int32Data.Add((int)reader.ReadVarint());
          break;
        case 7:
          int64Data ??= [];
          if (wt == ProtobufReader.WireLengthDelimited) {
            var packed = reader.ReadBytes();
            var pr = new ProtobufReader(packed);
            while (!pr.AtEnd) int64Data.Add((long)pr.ReadVarint());
          } else int64Data.Add((long)reader.ReadVarint());
          break;
        case 8: name = reader.ReadString(); break;
        case 9: raw = reader.ReadBytes().ToArray(); break;
        case 10:
          doubleData ??= [];
          if (wt == ProtobufReader.WireLengthDelimited) {
            var packed = reader.ReadBytes();
            for (var i = 0; i + 8 <= packed.Length; i += 8)
              doubleData.Add(BitConverter.Int64BitsToDouble(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(packed[i..])));
          } else doubleData.Add(BitConverter.Int64BitsToDouble((long)reader.ReadFixed64()));
          break;
        default: reader.SkipField(wt); break;
      }
    }

    // Prefer raw_data — it's already exactly the on-disk bytes. Otherwise serialize the typed array.
    if (raw == null) {
      if (floatData != null) {
        raw = new byte[floatData.Count * 4];
        for (var i = 0; i < floatData.Count; i++)
          System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 4), floatData[i]);
      } else if (int32Data != null) {
        raw = new byte[int32Data.Count * 4];
        for (var i = 0; i < int32Data.Count; i++)
          System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(i * 4), int32Data[i]);
      } else if (int64Data != null) {
        raw = new byte[int64Data.Count * 8];
        for (var i = 0; i < int64Data.Count; i++)
          System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(i * 8), int64Data[i]);
      } else if (doubleData != null) {
        raw = new byte[doubleData.Count * 8];
        for (var i = 0; i < doubleData.Count; i++)
          System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(raw.AsSpan(i * 8), doubleData[i]);
      } else {
        raw = [];
      }
    }

    return new Tensor(name, dataType, dims.ToArray(), raw);
  }

  // OperatorSetIdProto: 1 domain (string), 2 version (int64)
  private static OpsetImport ParseOpset(ReadOnlySpan<byte> data) {
    var d = "";
    long v = 0;
    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1: d = reader.ReadString(); break;
        case 2: v = (long)reader.ReadVarint(); break;
        default: reader.SkipField(wt); break;
      }
    }
    return new OpsetImport(d, v);
  }

  // ValueInfoProto: 1 name (string), 2 type (TypeProto)
  // TypeProto: 1 tensor_type (TypeProto.Tensor)
  // TypeProto.Tensor: 1 elem_type (int32), 2 shape (TensorShapeProto)
  // TensorShapeProto: 1 dim (repeated Dimension). Dimension: 1 dim_value (int64), 2 dim_param (string)
  private static ValueInfo ParseValueInfo(ReadOnlySpan<byte> data) {
    var name = "";
    var elemType = 0;
    var dims = new List<long>();

    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1: name = reader.ReadString(); break;
        case 2: ParseType(reader.ReadBytes(), ref elemType, dims); break;
        default: reader.SkipField(wt); break;
      }
    }
    return new ValueInfo(name, elemType, dims.ToArray());
  }

  private static void ParseType(ReadOnlySpan<byte> data, ref int elemType, List<long> dims) {
    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1: ParseTensorType(reader.ReadBytes(), ref elemType, dims); break;
        default: reader.SkipField(wt); break;
      }
    }
  }

  private static void ParseTensorType(ReadOnlySpan<byte> data, ref int elemType, List<long> dims) {
    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      switch (fn) {
        case 1: elemType = (int)reader.ReadVarint(); break;
        case 2: ParseShape(reader.ReadBytes(), dims); break;
        default: reader.SkipField(wt); break;
      }
    }
  }

  private static void ParseShape(ReadOnlySpan<byte> data, List<long> dims) {
    var reader = new ProtobufReader(data);
    while (reader.ReadTag(out var fn, out var wt)) {
      if (fn == 1) {
        var dimBytes = reader.ReadBytes();
        var dr = new ProtobufReader(dimBytes);
        long v = -1;
        while (dr.ReadTag(out var dfn, out var dwt)) {
          if (dfn == 1) v = (long)dr.ReadVarint();
          else dr.SkipField(dwt);
        }
        dims.Add(v);
      } else {
        reader.SkipField(wt);
      }
    }
  }

  /// <summary>ONNX data-type names as defined in <c>TensorProto.DataType</c>.</summary>
  public static string DataTypeName(int code) => code switch {
    0 => "UNDEFINED",
    1 => "FLOAT",
    2 => "UINT8",
    3 => "INT8",
    4 => "UINT16",
    5 => "INT16",
    6 => "INT32",
    7 => "INT64",
    8 => "STRING",
    9 => "BOOL",
    10 => "FLOAT16",
    11 => "DOUBLE",
    12 => "UINT32",
    13 => "UINT64",
    14 => "COMPLEX64",
    15 => "COMPLEX128",
    16 => "BFLOAT16",
    _ => $"type_{code}",
  };
}

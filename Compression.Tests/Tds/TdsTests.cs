using System.Buffers.Binary;
using System.Text;
using FileFormat.Tds;

namespace Compression.Tests.Tds;

[TestFixture]
public class TdsTests {

  /// <summary>
  /// Builds a minimal 3DS file: primary 4D4D chunk wrapping an EDIT3DS (3D3D) chunk that
  /// contains one OBJECT (4000) "cube" with a TRI_MESH (4100) holding a VERTEX_LIST (4110)
  /// of 3 vertices and a FACE_LIST (4120) of 1 face.
  /// </summary>
  private static byte[] BuildMinimalTds() {
    // vertex list body: uint16 n + n * 3 floats
    var vertList = BuildChunk(0x4110, MakeVertexList(3));
    // face list body: uint16 n + n * (4 uint16) [v0,v1,v2,flag]
    var faceList = BuildChunk(0x4120, MakeFaceList(1));
    // tri mesh wraps vertex+face
    var triMesh = BuildChunk(0x4100, Concat(vertList, faceList));
    // named OBJECT: name (zero-terminated ascii) + TRI_MESH
    var nameBytes = Encoding.ASCII.GetBytes("cube");
    var objectBody = new byte[nameBytes.Length + 1 + triMesh.Length];
    nameBytes.CopyTo(objectBody.AsSpan(0));
    objectBody[nameBytes.Length] = 0;
    triMesh.CopyTo(objectBody.AsSpan(nameBytes.Length + 1));
    var objectChunk = BuildChunk(0x4000, objectBody);
    // EDIT3DS wraps OBJECT
    var edit = BuildChunk(0x3D3D, objectChunk);
    // Main 4D4D wraps EDIT3DS
    return BuildChunk(0x4D4D, edit);
  }

  private static byte[] MakeVertexList(int n) {
    var buf = new byte[2 + n * 12];
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)n);
    // Fill vertices with simple 0/1 coords.
    for (var i = 0; i < n; i++) {
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(2 + i * 12 + 0), i);
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(2 + i * 12 + 4), 0);
      BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(2 + i * 12 + 8), 0);
    }
    return buf;
  }

  private static byte[] MakeFaceList(int n) {
    var buf = new byte[2 + n * 8];
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)n);
    for (var i = 0; i < n; i++) {
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2 + i * 8 + 0), 0);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2 + i * 8 + 2), 1);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2 + i * 8 + 4), 2);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2 + i * 8 + 6), 0); // flags
    }
    return buf;
  }

  private static byte[] BuildChunk(ushort id, byte[] body) {
    var chunk = new byte[6 + body.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(0), id);
    BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(2), (uint)chunk.Length);
    body.CopyTo(chunk.AsSpan(6));
    return chunk;
  }

  private static byte[] Concat(params byte[][] parts) {
    var total = parts.Sum(p => p.Length);
    var result = new byte[total];
    var pos = 0;
    foreach (var p in parts) { p.CopyTo(result.AsSpan(pos)); pos += p.Length; }
    return result;
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_EmitsMetadataAndTopLevelChunk() {
    var data = BuildMinimalTds();
    using var ms = new MemoryStream(data);
    var entries = new TdsFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name.StartsWith("chunk_", StringComparison.Ordinal) && e.Name.Contains("3D3D", StringComparison.OrdinalIgnoreCase)), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_MetadataContainsVertexAndFaceCounts() {
    var data = BuildMinimalTds();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(data);
      new TdsFormatDescriptor().Extract(ms, tmp, null, null);
      var meta = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(meta, Contains.Substring("primary_size_matches=true"));
      Assert.That(meta, Contains.Substring("mesh_count=1"));
      Assert.That(meta, Contains.Substring("vertex_count=3"));
      Assert.That(meta, Contains.Substring("face_count=1"));
      Assert.That(meta, Contains.Substring("object_names=cube"));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Descriptor_BadMagic_ReportsPrimarySizeMatchFalse() {
    var data = new byte[20]; // zeros — magic fails
    using var ms = new MemoryStream(data);
    var entries = new TdsFormatDescriptor().List(ms, null);
    using var metaStream = new MemoryStream();
    new TdsFormatDescriptor().ExtractEntry(new MemoryStream(data), "metadata.ini", metaStream, null);
    Assert.That(Encoding.UTF8.GetString(metaStream.ToArray()), Contains.Substring("primary_size_matches=false"));
  }
}

#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Pdf;

/// <summary>
/// Writes a minimal PDF 1.7 file containing the input files as embedded file
/// attachments (EmbeddedFiles in the Names tree). Every PDF viewer can list
/// them via "File → Attachments"; our <see cref="PdfReader"/> extracts them too.
/// One blank page is emitted (PDF spec requires at least one page).
/// </summary>
public sealed class PdfWriter {
  private readonly List<(string name, byte[] data)> _files = [];

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data));
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    // Object numbering:
    //   1 = Catalog, 2 = Pages, 3 = Page
    //   For each file i: 4+i*2 = Filespec, 5+i*2 = EmbeddedFile stream
    var nextObj = 4 + _files.Count * 2;
    var offsets = new List<long>();
    var sb = new StringBuilder();

    // Track output position for xref.
    long pos = 0;
    void Emit(string s) { var bytes = Encoding.ASCII.GetBytes(s); output.Write(bytes); pos += bytes.Length; }
    void EmitRaw(byte[] d) { output.Write(d); pos += d.Length; }

    Emit("%PDF-1.7\n");

    // Catalog (obj 1)
    offsets.Add(pos);
    var namesEntries = new StringBuilder();
    for (var i = 0; i < _files.Count; i++) {
      if (i > 0) namesEntries.Append(' ');
      namesEntries.Append('(').Append(EscapePdfString(_files[i].name)).Append(") ");
      namesEntries.Append(4 + i * 2).Append(" 0 R");
    }
    Emit($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [{namesEntries}] >> >> >>\nendobj\n");

    // Pages (obj 2)
    offsets.Add(pos);
    Emit("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    // Page (obj 3) — blank page, 8.5 × 11 inches
    offsets.Add(pos);
    Emit("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

    // Filespec + EmbeddedFile pairs
    for (var i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      var fsObjNum = 4 + i * 2;
      var efObjNum = 5 + i * 2;

      offsets.Add(pos);
      Emit($"{fsObjNum} 0 obj\n<< /Type /Filespec /F ({EscapePdfString(name)}) /EF << /F {efObjNum} 0 R >> >>\nendobj\n");

      offsets.Add(pos);
      Emit($"{efObjNum} 0 obj\n<< /Type /EmbeddedFile /Length {data.Length} >>\nstream\n");
      EmitRaw(data);
      Emit("\nendstream\nendobj\n");
    }

    // xref table
    var xrefPos = pos;
    var totalObjs = 1 + offsets.Count; // 0 (free) + objects
    Emit($"xref\n0 {totalObjs}\n");
    Emit("0000000000 65535 f \n");
    foreach (var off in offsets)
      Emit(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

    // Trailer
    Emit($"trailer\n<< /Size {totalObjs} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");
  }

  private static string EscapePdfString(string s) =>
    s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}

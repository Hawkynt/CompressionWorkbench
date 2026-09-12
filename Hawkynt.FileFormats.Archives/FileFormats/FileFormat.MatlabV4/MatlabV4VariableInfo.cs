namespace FileFormat.MatlabV4;

/// <summary>
/// Summary info about a single MATLAB MAT v4 record (top-level variable) discovered while walking the file.
/// </summary>
public sealed class MatlabV4VariableInfo {

  /// <summary>Variable name as decoded from the record's name bytes (null terminator stripped).</summary>
  public string Name { get; }

  /// <summary>Human-readable type name (e.g. "double", "single", "int32", "text", "sparse").</summary>
  public string TypeName { get; }

  /// <summary>Row count from the record header.</summary>
  public uint Rows { get; }

  /// <summary>Column count from the record header.</summary>
  public uint Cols { get; }

  /// <summary>True when the record's ImagFlag is non-zero (imaginary parts follow the real data).</summary>
  public bool IsImaginary { get; }

  /// <summary>Constructs a new <see cref="MatlabV4VariableInfo"/>.</summary>
  public MatlabV4VariableInfo(string name, string typeName, uint rows, uint cols, bool isImaginary) {
    this.Name = name;
    this.TypeName = typeName;
    this.Rows = rows;
    this.Cols = cols;
    this.IsImaginary = isImaginary;
  }
}

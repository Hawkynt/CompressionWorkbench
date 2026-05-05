#pragma warning disable CS1591
namespace FileFormat.Matlab;

/// <summary>
/// Summary info about a single top-level MATLAB array discovered in a MAT v5 file.
/// </summary>
public sealed class MatlabArrayInfo {

  public string Name { get; }
  public string ClassName { get; }
  public int[] Dimensions { get; }

  public MatlabArrayInfo(string name, string className, int[] dimensions) {
    this.Name = name;
    this.ClassName = className;
    this.Dimensions = dimensions;
  }
}

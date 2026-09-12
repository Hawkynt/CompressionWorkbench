#pragma warning disable CS1591
namespace FileFormat.Matlab;

/// <summary>
/// Summary info about a single top-level MATLAB array discovered in a MAT v5 file.
/// </summary>
public sealed class MatlabArrayInfo {

  /// <summary>
  /// Gets the name.
  /// </summary>
  public string Name { get; }
  /// <summary>
  /// Gets the class name.
  /// </summary>
  public string ClassName { get; }
  /// <summary>
  /// Gets the dimensions.
  /// </summary>
  public int[] Dimensions { get; }

  /// <summary>
  /// Initializes a new instance of <see cref="MatlabArrayInfo"/>.
  /// </summary>
  public MatlabArrayInfo(string name, string className, int[] dimensions) {
    this.Name = name;
    this.ClassName = className;
    this.Dimensions = dimensions;
  }
}

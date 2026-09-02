namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 2048 x 27 class 4.
/// </summary>
public class Line2048X27Class4 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        2, 4, 7, 13, 4, 5, 7, 15, 8, 7, 10, 16, 16, 14, 16, 16,
        2, 4, 7, 16, 3, 4, 7, 14, 8, 8, 10, 16, 16, 16, 15, 16,
        6, 8, 11, 16, 7, 7, 9, 16, 11, 9, 13, 16, 16, 16, 15, 16,
        16, 16, 16, 16, 14, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = CodeBookMapType.None;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;
    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = null;
}

namespace OggVorbisEncoder.Setup.Templates.FloorBooks;

/// <summary>
/// Represents a line 1024 x 27 class 3.
/// </summary>
public class Line1024X27Class3 : IStaticCodeBook
{
        /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 1;

        /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
        1, 5, 7, 21, 5, 8, 9, 21, 10, 9, 12, 20, 20, 16, 20, 20,
        4, 8, 9, 20, 6, 8, 9, 20, 11, 11, 13, 20, 20, 15, 17, 20,
        9, 11, 14, 20, 8, 10, 15, 20, 11, 13, 15, 20, 20, 20, 20, 20,
        20, 20, 20, 20, 13, 20, 20, 20, 18, 18, 20, 20, 20, 20, 20, 20,
        3, 6, 8, 20, 6, 7, 9, 20, 10, 9, 12, 20, 20, 20, 20, 20,
        5, 7, 9, 20, 6, 6, 9, 20, 10, 9, 12, 20, 20, 20, 20, 20,
        8, 10, 13, 20, 8, 9, 12, 20, 11, 10, 12, 20, 20, 20, 20, 20,
        18, 20, 20, 20, 15, 17, 18, 20, 18, 17, 18, 20, 20, 20, 20, 20,
        7, 10, 12, 20, 8, 9, 11, 20, 14, 13, 14, 20, 20, 20, 20, 20,
        6, 9, 12, 20, 7, 8, 11, 20, 12, 11, 13, 20, 20, 20, 20, 20,
        9, 11, 15, 20, 8, 10, 14, 20, 12, 11, 14, 20, 20, 20, 20, 20,
        20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20,
        11, 16, 18, 20, 15, 15, 17, 20, 20, 17, 20, 20, 20, 20, 20, 20,
        9, 14, 16, 20, 12, 12, 15, 20, 17, 15, 18, 20, 20, 20, 20, 20,
        16, 19, 18, 20, 15, 16, 20, 20, 17, 17, 20, 20, 20, 20, 20, 20,
        20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20, 20
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

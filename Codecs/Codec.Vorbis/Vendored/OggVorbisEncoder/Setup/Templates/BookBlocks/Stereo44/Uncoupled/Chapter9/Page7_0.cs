namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter9;

/// <summary>
/// Represents a page 7 0.
/// </summary>
public class Page7_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
public byte[] LengthList { get; } = {
         1, 4, 5, 6, 6, 7, 7, 8, 9,10,10,11,11, 5, 6, 6,
         7, 7, 8, 8, 9, 9,10,10,11,11, 5, 6, 6, 7, 7, 8,
         8, 9, 9,10,10,11,11, 6, 7, 7, 8, 8, 9, 9,10,10,
        11,11,12,12, 6, 7, 7, 8, 8, 9, 9,10,10,11,11,12,
        12, 8, 8, 8, 9, 9,10,10,11,11,12,12,13,13, 8, 8,
         8, 9, 9,10,10,11,11,12,12,13,13, 9, 9, 9,10,10,
        11,11,12,12,13,13,13,13, 9, 9, 9,10,10,11,11,12,
        12,13,13,14,14,10,10,10,11,11,12,12,13,13,14,13,
        15,14,10,10,10,11,11,12,12,13,13,14,14,14,14,11,
        11,12,12,12,13,13,14,14,14,14,15,15,11,11,12,12,
        12,13,13,14,14,14,15,15,15,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
public int QuantMin { get; } = -523206656;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
public int QuantDelta { get; } = 1618345984;
    /// <summary>
    /// Gets the quant.
    /// </summary>
public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
public int[] QuantList { get; } = {
        6,
        5,
        7,
        4,
        8,
        3,
        9,
        2,
        10,
        1,
        11,
        0,
        12,
    };
}

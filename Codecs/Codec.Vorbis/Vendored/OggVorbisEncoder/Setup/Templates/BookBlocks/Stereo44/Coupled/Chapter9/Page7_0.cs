namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter9;

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
         2, 4, 4, 6, 6, 7, 7, 8, 8,10,10,11,11, 6, 4, 4,
         6, 6, 8, 8, 9, 9,10,10,12,12, 6, 4, 5, 6, 6, 8,
         8, 9, 9,10,10,12,12,20, 6, 6, 6, 6, 8, 8, 9,10,
        11,11,12,12,20, 6, 6, 6, 6, 8, 8,10,10,11,11,12,
        12,20,10,10, 7, 7, 9, 9,10,10,11,11,12,12,20,11,
        11, 7, 7, 9, 9,10,10,11,11,12,12,20,20,20, 9, 9,
         9, 9,11,11,12,12,13,13,20,20,20, 9, 9, 9, 9,11,
        11,12,12,13,13,20,20,20,13,13,10,10,11,11,12,13,
        13,13,20,20,20,13,13,10,10,11,11,12,13,13,13,20,
        20,20,20,19,12,12,12,12,13,13,14,15,19,19,19,19,
        19,12,12,12,12,13,13,14,14,
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

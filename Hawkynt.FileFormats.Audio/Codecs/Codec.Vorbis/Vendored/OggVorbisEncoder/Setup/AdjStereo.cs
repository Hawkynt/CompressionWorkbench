namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents an adj stereo.
/// </summary>
public class AdjStereo
{
    /// <summary>
    /// Initializes a new instance of <see cref="AdjStereo"/>.
    /// </summary>
    public AdjStereo(
        int[] pre,
        int[] post,
        float[] kilohertz,
        float[] lowPassKilohertz)
    {
        Pre = pre;
        Post = post;
        Kilohertz = kilohertz;
        LowPassKilohertz = lowPassKilohertz;
    }

    /// <summary>
    /// Gets the pre.
    /// </summary>
    public int[] Pre { get; }
    /// <summary>
    /// Gets the post.
    /// </summary>
    public int[] Post { get; }
    /// <summary>
    /// Gets the kilohertz.
    /// </summary>
    public float[] Kilohertz { get; }
    /// <summary>
    /// Gets the low pass kilohertz.
    /// </summary>
    public float[] LowPassKilohertz { get; }
}

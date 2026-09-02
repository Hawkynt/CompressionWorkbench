using System.Runtime.CompilerServices;

namespace OggVorbisEncoder;

/// <summary>
/// Represents a float extensions.
/// </summary>
public static class FloatExtensions
{
        /// <summary>
    /// Performs the to decibel operation.
    /// </summary>
public static float ToDecibel(this float x)
    {
        var i = Unsafe.As<float, uint>(ref x);
        i &= 0x7fffffff;
        return i * 7.17711438e-7f - 764.6161886f;
    }
}

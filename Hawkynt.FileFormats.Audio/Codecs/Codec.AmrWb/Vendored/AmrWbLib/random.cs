/*-------------------------------------------------------------------*
 *                         RANDOM.C                   *
 *-------------------------------------------------------------------*
 * Signed 16 bits random generator.                     *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    short Random(ref short seed)
    {
        /* static Word16 seed = 21845; */

        seed = extract_l(L_add(L_shr(L_mult(seed, 31821), 1), 13849));

        return (seed);
    }
}

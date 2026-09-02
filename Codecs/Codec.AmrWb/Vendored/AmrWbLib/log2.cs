/********************************************************************************
*
*      File             : log2.c
*      Purpose          : Computes log2(L_x)
*
********************************************************************************
*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    /*******************************************************************************
    *
    *      File             : log2.tab
    *      Purpose          : Table for routine Log2().
    *      $Id $
    *
    ********************************************************************************
    */
    private static short[] table = new short[33] 
    {
    0, 1455, 2866, 4236, 5568, 6863, 8124, 9352, 10549, 11716,
    12855, 13967, 15054, 16117, 17156, 18172, 19167, 20142, 21097, 22033,
    22951, 23852, 24735, 25603, 26455, 27291, 28113, 28922, 29716, 30497,
    31266, 32023, 32767
    };

    /*************************************************************************
    *
    *   FUNCTION:   Log2_norm()
    *
    *   PURPOSE:   Computes log2(L_x, exp),  where   L_x is positive and
    *              normalized, and exp is the normalisation exponent
    *              If L_x is negative or zero, the result is 0.
    *
    *   DESCRIPTION:
    *        The function Log2(L_x) is approximated by a table and linear
    *        interpolation. The following steps are used to compute Log2(L_x)
    *
    *           1- exponent = 30-norm_exponent
    *           2- i = bit25-b31 of L_x;  32<=i<=63  (because of normalization).
    *           3- a = bit10-b24
    *           4- i -=32
    *           5- fraction = table[i]<<16 - (table[i] - table[i+1]) * a * 2
    *
    *************************************************************************/
    void Log2_norm(
        int L_x,         /* (i) : input value (normalized)                    */
        short exp,         /* (i) : norm_l (L_x)                                */
        ref short exponent,   /* (o) : Integer part of Log2.   (range: 0<=val<=30) */
        ref short fraction    /* (o) : Fractional part of Log2. (range: 0<=val<1)  */
    )
    {
        short i, a, tmp;
        int L_y;

        if (L_x <= (int)0)
        {
            exponent = 0;
            fraction = 0;
            return;
        }

        exponent = sub(30, exp);

        L_x = L_shr(L_x, 9);
        i = extract_h(L_x);                /* Extract b25-b31 */
        L_x = L_shr(L_x, 1);
        a = extract_l(L_x);                /* Extract b10-b24 of fraction */
        a = (short)(a & (short)0x7fff);

        i = sub(i, 32);

        L_y = L_deposit_h(table[i]);       /* table[i] << 16        */
        tmp = sub(table[i], table[i + 1]); /* table[i] - table[i+1] */
        L_y = L_msu(L_y, tmp, a);          /* L_y -= tmp*a*2        */

        fraction = extract_h(L_y);

        return;
    }

    /*************************************************************************
     *
     *   FUNCTION:   Log2()
     *
     *   PURPOSE:   Computes log2(L_x),  where   L_x is positive.
     *              If L_x is negative or zero, the result is 0.
     *
     *   DESCRIPTION:
     *        normalizes L_x and then calls Log2_norm().
     *
     *************************************************************************/
    void Log2(
        int L_x,         /* (i) : input value                                 */
        ref short exponent,   /* (o) : Integer part of Log2.   (range: 0<=val<=30) */
        ref short fraction    /* (o) : Fractional part of Log2. (range: 0<=val<1) */
    )
    {
        short exp;

        exp = norm_l(L_x);
        Log2_norm(L_shl(L_x, exp), exp, ref exponent, ref fraction);
    }
}

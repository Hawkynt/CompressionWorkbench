/*-------------------------------------------------------------------*
 *                         PREEMPH.C                 *
 *-------------------------------------------------------------------*
 * Preemphasis: filtering through 1 - g z^-1                         *
 *                                                                   *
 * Preemph2 --> signal is multiplied by 2.                           *
 *-------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "count.h"


namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private unsafe void Preemph(
         short* x,                           /* (i/o)   : input signal overwritten by the output */
         short mu,                            /* (i) Q15 : preemphasis coefficient                */
         short lg,                            /* (i)     : lenght of filtering                    */
         ref short mem                          /* (i/o)   : memory (x[-1])                         */
    )
    {
        short i, temp;
        int L_tmp;

        temp = x[lg - 1];

        for (i = (short)(lg - 1); i > 0; i--)
        {
            L_tmp = L_deposit_h(x[i]);
            L_tmp = L_msu(L_tmp, x[i - 1], mu);
            x[i] = round(L_tmp);
        }

        L_tmp = L_deposit_h(x[0]);
        L_tmp = L_msu(L_tmp, mem, mu);
        x[0] = round(L_tmp);

        mem = temp;

        return;
    }

    private unsafe void Preemph2(
         short* x,                           /* (i/o)   : input signal overwritten by the output */
         short mu,                            /* (i) Q15 : preemphasis coefficient                */
         short lg,                            /* (i)     : lenght of filtering                    */
         short* mem                          /* (i/o)   : memory (x[-1])                         */
    )
    {
        short i, temp;
        int L_tmp;

        temp = x[lg - 1];

        for (i = (short)(lg - 1); i > 0; i--)
        {
            L_tmp = L_deposit_h(x[i]);
            L_tmp = L_msu(L_tmp, x[i - 1], mu);
            L_tmp = L_shl(L_tmp, 1);
            x[i] = round(L_tmp);
        }

        L_tmp = L_deposit_h(x[0]);
        L_tmp = L_msu(L_tmp, *mem, mu);
        L_tmp = L_shl(L_tmp, 1);
        x[0] = round(L_tmp);    

        *mem = temp;
    }

}

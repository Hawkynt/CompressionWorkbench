/*-----------------------------------------------------------------------*
 *                         PIT_SHRP.C                   *
 *-----------------------------------------------------------------------*
 * Performs Pitch sharpening routine                             *
 *-----------------------------------------------------------------------*/

//#include "typedef.h"
//#include "basic_op.h"
//#include "count.h"

namespace AmrWbLib;

public partial class AmrWb
{
    private unsafe void Pit_shrp(
         short* x,                           /* in/out: impulse response (or algebraic code) */
         short pit_lag,                       /* input : pitch lag                            */
         short sharp,                         /* input : pitch sharpening factor (Q15)        */
         short L_subfr                        /* input : subframe size                        */
    )
    {
        short i;
        int L_tmp;

        for (i = pit_lag; i < L_subfr; i++)
        {
            L_tmp = L_deposit_h(x[i]);
            L_tmp = L_mac(L_tmp, x[i - pit_lag], sharp);
            x[i] = round(L_tmp);
        }
    }

}

/*-------------------------------------------------------------------*
 *                         weight_a.c                                *
 *-------------------------------------------------------------------*
 * Weighting of LPC coefficients.                   *
 *   ap[i]  =  a[i] * (gamma ** i)                   *
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
    private unsafe void Weight_a(
         short* a,                           /* (i) Q12 : a[m+1]  LPC coefficients             */
         short* ap,                          /* (o) Q12 : Spectral expanded LPC coefficients   */
         short gamma,                         /* (i) Q15 : Spectral expansion factor.           */
         short m                              /* (i)     : LPC order.                           */
    )
    {
        short i, fac;

        ap[0] = a[0];
        fac = gamma;
        for (i = 1; i < m; i++)
        {
            ap[i] = round(L_mult(a[i], fac));
            fac = round(L_mult(fac, gamma));
        }
        ap[m] = round(L_mult(a[m], fac));
    }

}

using AVMTradeReporter.Models.Data.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AVMTradeReporterTests.Pool
{
    public class PoolTests
    {
        // Calculate the virtual Asset A and Asset B amounts
        [Test]
        public void CalculateVirtualAmounts()
        {
            //    var pool = new AVMTradeReporter.Models.Data.Pool
            //    {
            //        AMMType = AVMTradeReporter.Models.Data.AMMType.ConcentratedLiquidityAMM,
            //        A = 419186_162941759,
            //        B = 31058_727092031,
            //        PMin = 1.4m,
            //        PMax = 1.6m,
            //        L = 3_715_598_743_719_791
            //    };
            var pool = new AVMTradeReporter.Models.Data.Pool
            {
                AMMType = AMMType.ConcentratedLiquidityAMM,
                A = 419186_162941759,
                B = 31058_727092031,
                PMin = 1.4m,
                PMax = 1.6m,
                L = 3_715_598_743_719_791
            };
            var virtualAmountA = Convert.ToDecimal(pool.VirtualAmountA);
            var virtualAmountB = Convert.ToDecimal(pool.VirtualAmountB);
            Assert.That(virtualAmountB / virtualAmountA, Is.EqualTo(1.4091058619237558219157887306m));
            Assert.That(virtualAmountA, Is.EqualTo(6810660.9657368758238680553836M));
            Assert.That(virtualAmountB, Is.EqualTo(9596942.290395139625435727689M));
        }
    }
}

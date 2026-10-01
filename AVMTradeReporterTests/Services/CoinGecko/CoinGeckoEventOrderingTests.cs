using AVMTradeReporter.Services;
using AVMTradeReporter.Services.CoinGecko;

namespace AVMTradeReporterTests.Services.CoinGecko
{
    public class CoinGeckoEventOrderingTests
    {
        private static EventOrderKey Real(ulong block, ulong txn, uint ev, string tx = "A", int kind = 0) => new(block, txn, ev, tx, tx + ev, kind);
        private static EventOrderKey Legacy(ulong block, string group, string tx, int kind = 0) => new(block, null, null, group, tx, kind);

        [Test]
        public void RealPositions_AreKept()
        {
            var positions = CoinGeckoEventOrdering.AssignPositions(new[] { Real(10, 3, 0), Real(10, 3, 1), Real(10, 9, 0) });
            Assert.That(positions, Is.EqualTo(new[] { new EventPositionPair(3, 0), new EventPositionPair(3, 1), new EventPositionPair(9, 0) }));
        }

        [Test]
        public void LegacyDocuments_GetDeterministicPositionsAfterTheRealOnes()
        {
            var keys = new[]
            {
                Legacy(10, "TXB", "TXB-1"),
                Real(10, 5, 0),
                Legacy(10, "TXA", "TXA-1"),
            };
            var positions = CoinGeckoEventOrdering.AssignPositions(keys);

            Assert.That(positions[1], Is.EqualTo(new EventPositionPair(5, 0)));
            Assert.That(positions[2], Is.EqualTo(new EventPositionPair(6, 0)), "group TXA sorts before TXB");
            Assert.That(positions[0], Is.EqualTo(new EventPositionPair(7, 0)));

            // same answer when the input arrives in another order (ES returns hits in no defined order within a block)
            var reversed = CoinGeckoEventOrdering.AssignPositions(keys.Reverse().ToArray()).Reverse().ToArray();
            Assert.That(reversed, Is.EqualTo(positions));
        }

        [Test]
        public void LegacyEventsOfOneTransaction_ShareTxnIndexWithIncreasingEventIndex()
        {
            var positions = CoinGeckoEventOrdering.AssignPositions(new[]
            {
                Legacy(10, "TOP", "TOP-liq", kind: 1),
                Legacy(10, "TOP", "TOP-swap2"),
                Legacy(10, "TOP", "TOP-swap1"),
            });
            Assert.That(positions.Select(p => p.TxnIndex).Distinct().Count(), Is.EqualTo(1));
            Assert.That(positions.Select(p => p.EventIndex).OrderBy(i => i), Is.EqualTo(new uint[] { 0, 1, 2 }));
            Assert.That(positions[0].EventIndex, Is.EqualTo(2u), "swaps (kind 0) before liquidity (kind 1), then by tx id");
        }

        [Test]
        public void KnownTransactionWithoutEventIndex_TakesTheSlotAfterTheStampedEvents()
        {
            var keys = new[] { new EventOrderKey(10, 5, null, "T", "T-unstamped", 0), Real(10, 5, 0, "T"), Real(10, 5, 1, "T") };
            var positions = CoinGeckoEventOrdering.AssignPositions(keys);
            Assert.That(positions[1], Is.EqualTo(new EventPositionPair(5, 0)));
            Assert.That(positions[2], Is.EqualTo(new EventPositionPair(5, 1)));
            Assert.That(positions[0], Is.EqualTo(new EventPositionPair(5, 2)), "after the stamped ones, not colliding with event 0");
        }

        [Test]
        public void Positions_AreScopedPerBlock()
        {
            var positions = CoinGeckoEventOrdering.AssignPositions(new[] { Legacy(10, "X", "X1"), Legacy(11, "X", "X1"), Real(12, 2, 0) });
            Assert.That(positions[0], Is.EqualTo(new EventPositionPair(1, 0)));
            Assert.That(positions[1], Is.EqualTo(new EventPositionPair(1, 0)), "a new block starts again");
        }

        [Test]
        public void DuplicateRealPosition_IsBumpedInsteadOfEmittedTwice()
        {
            // same (txn, event) twice in one block would make GeckoTerminal halt indexing
            var positions = CoinGeckoEventOrdering.AssignPositions(new[] { Real(10, 4, 0, "A"), Real(10, 4, 0, "B") });
            Assert.That(positions.Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void Positions_AreUniquePerBlock_ForMixedInput()
        {
            var keys = new List<EventOrderKey>();
            for (ulong b = 100; b < 103; b++)
            {
                for (uint i = 0; i < 4; i++) keys.Add(Real(b, 1 + i / 2, i % 2, $"R{b}-{i}"));
                for (var i = 0; i < 5; i++) keys.Add(Legacy(b, $"G{i % 3}", $"L{b}-{i}", i % 2));
            }
            var positions = CoinGeckoEventOrdering.AssignPositions(keys);
            var unique = keys.Select((k, i) => (k.BlockId, positions[i])).Distinct().Count();
            Assert.That(unique, Is.EqualTo(keys.Count));
        }

        [Test]
        public void EventPosition_CountsEventsOfOneTransactionFromZero()
        {
            var position = new EventPosition(12);
            Assert.That(position.TxnIndex, Is.EqualTo(12UL));
            Assert.That(position.NextEventIndex(), Is.EqualTo(0u));
            Assert.That(position.NextEventIndex(), Is.EqualTo(1u));
            Assert.That(position.NextEventIndex(), Is.EqualTo(2u));
        }
    }
}

using System.Globalization;

namespace AVMTradeReporter.Services
{
    /// <summary>
    /// Encoding of the hourly per-asset real-TVL snapshot values stored in the Redis hashes
    /// <c>asset:tvl:hourly:{unixHour}</c> (hash field = asset id, hash value = this codec's format).
    ///
    /// Current format is an intra-hour OHLC quadruple <c>"open;high;low;close"</c> built up by the
    /// ~5 minute TopAssets recompute loop, so the hourly TVL candles show real movement within the
    /// hour. Legacy entries written before the OHLC upgrade are a single plain number and are decoded
    /// as a flat candle (o=h=l=c).
    /// </summary>
    public static class TvlSnapshotCodec
    {
        /// <summary>Merges the current TVL observation into an existing encoded value (or starts a new flat candle).</summary>
        public static string Merge(string? existing, decimal current)
        {
            if (!string.IsNullOrEmpty(existing) && TryParse(existing, out var o, out var h, out var l, out _))
            {
                return Encode(o, Math.Max(h, current), Math.Min(l, current), current);
            }
            return Encode(current, current, current, current);
        }

        public static string Encode(decimal o, decimal h, decimal l, decimal c) =>
            string.Create(CultureInfo.InvariantCulture, $"{o};{h};{l};{c}");

        /// <summary>Decodes an encoded snapshot value. Accepts both the OHLC and the legacy single-number format.</summary>
        public static bool TryParse(string? value, out decimal o, out decimal h, out decimal l, out decimal c)
        {
            o = h = l = c = 0m;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var parts = value.Split(';');
            if (parts.Length == 1)
            {
                if (!decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var single)) return false;
                o = h = l = c = single;
                return true;
            }
            if (parts.Length != 4) return false;
            if (!decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out o)) return false;
            if (!decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out h)) return false;
            if (!decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out l)) return false;
            if (!decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out c)) return false;
            return true;
        }
    }
}

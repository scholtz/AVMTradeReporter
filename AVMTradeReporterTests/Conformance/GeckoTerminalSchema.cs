using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AVMTradeReporterTests.Conformance
{
    /// <summary>
    /// Strict validator of the "GeckoTerminal Integration API Standards v0.1" (non-EVM DEX). GeckoTerminal halts the indexing
    /// of a whole DEX on the first schema violation, so this is deliberately stricter than the spec's prose: every rule is
    /// written down here once and used both offline (against the service's own output) and live (against stage / production).
    /// Each method returns the list of violations - empty means conformant.
    /// </summary>
    public static class GeckoTerminalSchema
    {
        private static readonly Regex PlainDecimal = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

        // ------------------------------------------------------------------------------------------------ latest-block

        public static List<string> ValidateLatestBlock(JsonElement root)
        {
            var errors = new List<string>();
            if (!RequireOnly(root, "", errors, "block")) return errors;
            ValidateBlock(root.GetProperty("block"), "block", errors);
            return errors;
        }

        // ------------------------------------------------------------------------------------------------ asset / pair

        public static List<string> ValidateAsset(JsonElement root, string expectedId)
        {
            var errors = new List<string>();
            if (!RequireOnly(root, "", errors, "asset")) return errors;
            var a = root.GetProperty("asset");
            var allowed = new[] { "id", "name", "symbol", "decimals", "totalSupply", "circulatingSupply", "coinGeckoId", "metadata" };
            OnlyKnownProperties(a, "asset", allowed, errors);
            if (Str(a, "id", "asset", errors) is { } id && id != expectedId) errors.Add($"asset.id '{id}' != requested '{expectedId}'");
            Str(a, "name", "asset", errors);
            Str(a, "symbol", "asset", errors);
            if (!a.TryGetProperty("decimals", out var dec) || dec.ValueKind != JsonValueKind.Number || !dec.TryGetInt32(out var d) || d < 0 || d > 19)
                errors.Add("asset.decimals must be an integer 0..19");
            if (a.TryGetProperty("totalSupply", out var ts)) NumberLike(ts, "asset.totalSupply", errors);
            if (a.TryGetProperty("circulatingSupply", out var cs)) NumberLike(cs, "asset.circulatingSupply", errors);
            if (a.TryGetProperty("metadata", out var md)) StringMap(md, "asset.metadata", errors);
            return errors;
        }

        public static List<string> ValidatePair(JsonElement root, string expectedId)
        {
            var errors = new List<string>();
            if (!RequireOnly(root, "", errors, "pair")) return errors;
            var p = root.GetProperty("pair");
            OnlyKnownProperties(p, "pair", new[] { "id", "dexKey", "asset0Id", "asset1Id", "createdAtBlockNumber", "createdAtBlockTimestamp", "createdAtTxnId", "creator", "feeBps", "pool", "metadata" }, errors);
            if (Str(p, "id", "pair", errors) is { } id && id != expectedId) errors.Add($"pair.id '{id}' != requested '{expectedId}'");
            Str(p, "dexKey", "pair", errors);
            var a0 = Str(p, "asset0Id", "pair", errors);
            var a1 = Str(p, "asset1Id", "pair", errors);
            if (a0 != null && a0 == a1) errors.Add("pair.asset0Id and asset1Id must differ");
            if (p.TryGetProperty("feeBps", out var fee))
            {
                if (fee.ValueKind != JsonValueKind.Number || fee.GetDecimal() < 0 || fee.GetDecimal() >= 10000) errors.Add("pair.feeBps must be a number in [0, 10000)");
            }
            if (p.TryGetProperty("metadata", out var md)) StringMap(md, "pair.metadata", errors);
            return errors;
        }

        // ------------------------------------------------------------------------------------------------ events

        /// <summary>Validates the whole <c>/events</c> response for the inclusive block range [from, to].</summary>
        public static List<string> ValidateEvents(JsonElement root, ulong from, ulong to)
        {
            var errors = new List<string>();
            if (!RequireOnly(root, "", errors, "events")) return errors;
            var events = root.GetProperty("events");
            if (events.ValueKind != JsonValueKind.Array)
            {
                errors.Add("events must be an array");
                return errors;
            }

            (ulong block, ulong txn, uint ev)? previous = null;
            var seen = new HashSet<(ulong, ulong, uint)>();
            var blockTimestamps = new Dictionary<ulong, long>();
            var index = 0;
            foreach (var e in events.EnumerateArray())
            {
                var where = $"events[{index++}]";
                var before = errors.Count;
                ValidateEvent(e, where, errors);
                if (errors.Count != before) continue; // fields unusable - ordering checks would only add noise

                var block = e.GetProperty("block").GetProperty("blockNumber").GetUInt64();
                var ts = e.GetProperty("block").GetProperty("blockTimestamp").GetInt64();
                var txn = e.GetProperty("txnIndex").GetUInt64();
                var ev = e.GetProperty("eventIndex").GetUInt32();

                if (block < from || block > to) errors.Add($"{where}: block {block} outside the requested range {from}-{to} (both bounds are inclusive)");
                if (previous is { } p && (block, txn, ev).CompareTo(p) < 0) errors.Add($"{where}: not sorted by (block, txnIndex, eventIndex)");
                previous = (block, txn, ev);
                if (!seen.Add((block, txn, ev))) errors.Add($"{where}: (txnIndex {txn}, eventIndex {ev}) is not unique within block {block}");
                if (blockTimestamps.TryGetValue(block, out var known) && known != ts) errors.Add($"{where}: block {block} has two different timestamps ({known}, {ts})");
                blockTimestamps[block] = ts;
            }
            return errors;
        }

        public static void ValidateEvent(JsonElement e, string where, List<string> errors)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{where}: must be an object");
                return;
            }
            var type = Str(e, "eventType", where, errors);
            if (type is not ("swap" or "join" or "exit"))
            {
                errors.Add($"{where}: eventType '{type}' must be swap, join or exit");
                return;
            }

            if (e.TryGetProperty("block", out var block)) ValidateBlock(block, $"{where}.block", errors);
            else errors.Add($"{where}: block is required");
            Str(e, "txnId", where, errors);
            UInt(e, "txnIndex", where, errors);
            UInt(e, "eventIndex", where, errors);
            Str(e, "maker", where, errors);
            Str(e, "pairId", where, errors);
            if (!e.TryGetProperty("reserves", out var reserves) || reserves.ValueKind != JsonValueKind.Object) errors.Add($"{where}: reserves {{asset0, asset1}} is required");
            else
            {
                NumberLikeRequired(reserves, "asset0", $"{where}.reserves", errors);
                NumberLikeRequired(reserves, "asset1", $"{where}.reserves", errors);
            }

            if (type == "swap") ValidateSwap(e, where, errors);
            else ValidateJoinExit(e, where, errors);
        }

        private static void ValidateSwap(JsonElement e, string where, List<string> errors)
        {
            OnlyKnownProperties(e, where, new[] { "block", "eventType", "txnId", "txnIndex", "eventIndex", "maker", "pairId", "asset0In", "asset1In", "asset0Out", "asset1Out", "priceNative", "reserves", "metadata" }, errors);
            decimal? v(string name) => e.TryGetProperty(name, out var x) && TryDecimal(x, out var d) ? d : null;
            var a0In = v("asset0In"); var a1In = v("asset1In"); var a0Out = v("asset0Out"); var a1Out = v("asset1Out");
            foreach (var name in new[] { "asset0In", "asset1In", "asset0Out", "asset1Out" })
            {
                if (e.TryGetProperty(name, out var x) && !IsNumberLike(x)) errors.Add($"{where}.{name}: must be a plain decimal number or string");
            }

            // exactly one of: asset0In + asset1Out   |   asset1In + asset0Out
            var zeroToOne = a0In.HasValue && a1Out.HasValue && !a1In.HasValue && !a0Out.HasValue;
            var oneToZero = a1In.HasValue && a0Out.HasValue && !a0In.HasValue && !a1Out.HasValue;
            if (zeroToOne == oneToZero) errors.Add($"{where}: a swap must carry exactly asset0In+asset1Out or asset1In+asset0Out");
            foreach (var amount in new[] { a0In, a1In, a0Out, a1Out })
            {
                if (amount is <= 0) errors.Add($"{where}: swap amounts must be > 0");
            }

            if (!e.TryGetProperty("priceNative", out var price) || !IsNumberLike(price) || !TryDecimal(price, out var p) || p <= 0)
            {
                errors.Add($"{where}.priceNative: required and must be > 0");
            }
            else if (zeroToOne ^ oneToZero)
            {
                // priceNative = price of asset0 quoted in asset1 = asset1 amount / asset0 amount of THIS swap
                var expected = zeroToOne ? a1Out!.Value / a0In!.Value : a1In!.Value / a0Out!.Value;
                if (Math.Abs(p - expected) > expected * 0.000001m) errors.Add($"{where}.priceNative {p} does not match amounts (asset1/asset0 = {expected})");
            }

            if (e.TryGetProperty("metadata", out var md))
            {
                OnlyKnownProperties(md, $"{where}.metadata", new[] { "fees0In", "fees1In", "fees0Out", "fees1Out" }, errors);
                foreach (var f in md.EnumerateObject())
                {
                    if (!IsNumberLike(f.Value) || !TryDecimal(f.Value, out var fee) || fee < 0) errors.Add($"{where}.metadata.{f.Name}: must be a non-negative decimal");
                }
            }
        }

        private static void ValidateJoinExit(JsonElement e, string where, List<string> errors)
        {
            OnlyKnownProperties(e, where, new[] { "block", "eventType", "txnId", "txnIndex", "eventIndex", "maker", "pairId", "amount0", "amount1", "reserves", "metadata" }, errors);
            NumberLikeRequired(e, "amount0", where, errors);
            NumberLikeRequired(e, "amount1", where, errors);
            if (e.TryGetProperty("amount0", out var a0) && e.TryGetProperty("amount1", out var a1) && TryDecimal(a0, out var d0) && TryDecimal(a1, out var d1))
            {
                if (d0 < 0 || d1 < 0) errors.Add($"{where}: amounts must not be negative");
                if (d0 == 0 && d1 == 0) errors.Add($"{where}: a join/exit with both amounts 0 carries no information");
            }
        }

        private static void ValidateBlock(JsonElement block, string where, List<string> errors)
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{where}: must be an object");
                return;
            }
            UInt(block, "blockNumber", where, errors);
            // UNIX seconds: a milliseconds value (13 digits) is the classic mistake and would put the block in the year 50000
            if (!block.TryGetProperty("blockTimestamp", out var ts) || ts.ValueKind != JsonValueKind.Number || !ts.TryGetInt64(out var seconds))
                errors.Add($"{where}.blockTimestamp: required integer (UNIX seconds)");
            else if (seconds < 1_400_000_000 || seconds > 4_000_000_000)
                errors.Add($"{where}.blockTimestamp {seconds} is not UNIX seconds (milliseconds?)");
        }

        // ------------------------------------------------------------------------------------------------ primitives

        private static bool RequireOnly(JsonElement root, string where, List<string> errors, string property)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out _))
            {
                errors.Add($"{where}response must be an object with '{property}'");
                return false;
            }
            OnlyKnownProperties(root, "response", new[] { property }, errors);
            return true;
        }

        private static void OnlyKnownProperties(JsonElement obj, string where, string[] allowed, List<string> errors)
        {
            if (obj.ValueKind != JsonValueKind.Object) return;
            foreach (var p in obj.EnumerateObject())
            {
                if (!allowed.Contains(p.Name)) errors.Add($"{where}: unexpected property '{p.Name}'");
            }
        }

        private static string? Str(JsonElement obj, string name, string where, List<string> errors)
        {
            if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
            errors.Add($"{where}.{name}: required non-empty string");
            return null;
        }

        private static void UInt(JsonElement obj, string name, string where, List<string> errors)
        {
            if (!(obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out _)))
                errors.Add($"{where}.{name}: required non-negative integer");
        }

        private static void NumberLikeRequired(JsonElement obj, string name, string where, List<string> errors)
        {
            if (!obj.TryGetProperty(name, out var v)) errors.Add($"{where}.{name}: required");
            else NumberLike(v, $"{where}.{name}", errors);
        }

        private static void NumberLike(JsonElement v, string where, List<string> errors)
        {
            if (!IsNumberLike(v)) errors.Add($"{where}: must be a plain decimal number or numeric string (no exponent, no sign)");
        }

        /// <summary>A JSON number or a string of plain decimal digits - up to 50 fractional digits are supported by GeckoTerminal.</summary>
        public static bool IsNumberLike(JsonElement v)
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { } s && PlainDecimal.IsMatch(s) && (s.IndexOf('.') < 0 || s.Length - s.IndexOf('.') - 1 <= 50),
                JsonValueKind.Number => !v.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase) && !v.GetRawText().StartsWith('-'),
                _ => false,
            };
        }

        public static bool TryDecimal(JsonElement v, out decimal value)
        {
            value = 0;
            var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
            return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
        }

        private static void StringMap(JsonElement md, string where, List<string> errors)
        {
            if (md.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{where}: must be an object of strings");
                return;
            }
            foreach (var p in md.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String) errors.Add($"{where}.{p.Name}: must be a string");
            }
        }

        private static int CompareTo(this (ulong block, ulong txn, uint ev) a, (ulong block, ulong txn, uint ev) b)
        {
            var c = a.block.CompareTo(b.block);
            if (c != 0) return c;
            c = a.txn.CompareTo(b.txn);
            return c != 0 ? c : a.ev.CompareTo(b.ev);
        }
    }
}

#!/usr/bin/env python3
"""
Records golden data for the GeckoTerminal conformance suite from a deployed API.

    python record-golden.py https://api.algorand.scan.biatec.io mainnet [cases=3] [max_chunks=600]

It walks back from latest-block in 1000-block slices, takes a few ranges that hold events (preferably one with a swap, one with
a join/exit when available), and writes golden/<name>.json with those exact events plus every referenced pair. The suite then
asserts that a historical event NEVER changes (price, amounts, reserves, order, fees) and that a pair's asset0/asset1 order and
fee are immutable - both are things GeckoTerminal reads once and never re-reads.

Review the file before committing: the recorded data becomes the truth the next deploy is held to. Record from a deployment you
trust (the one that was verified), at a block range old enough to be final (the script skips the newest 100 blocks).
"""
import json
import os
import sys
import urllib.request

base = sys.argv[1].rstrip("/")
name = sys.argv[2]
wanted = int(sys.argv[3]) if len(sys.argv) > 3 else 3
max_chunks = int(sys.argv[4]) if len(sys.argv) > 4 else 600


def get(path):
    with urllib.request.urlopen(f"{base}/api/coingecko/{path}", timeout=60) as r:
        return json.load(r)


latest = get("latest-block")["block"]["blockNumber"]
cases, pairs, kinds = [], {}, set()
for i in range(max_chunks):
    to = latest - 100 - i * 1000
    if to < 1000 or len(cases) >= wanted:
        break
    frm = to - 999
    events = get(f"events?fromBlock={frm}&toBlock={to}")["events"]
    if not events:
        continue
    types = {e["eventType"] for e in events}
    # keep variety: skip a slice that adds no new event type once we already hold something
    if cases and types <= kinds and len(cases) >= 2:
        continue
    kinds |= types
    # narrow the case to the blocks that hold events (keeps the file small and the case stable)
    first, last = events[0]["block"]["blockNumber"], events[-1]["block"]["blockNumber"]
    cases.append({
        "name": f"{'/'.join(sorted(types))} events, blocks {first}-{last}",
        "fromBlock": first,
        "toBlock": last,
        "events": get(f"events?fromBlock={first}&toBlock={last}")["events"],
    })
    for e in events:
        pid = e["pairId"]
        if pid not in pairs:
            pairs[pid] = get(f"pair?id={pid}")["pair"]

if not cases:
    sys.exit("no events found - nothing to record")

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "golden", f"{name}.json")
os.makedirs(os.path.dirname(out), exist_ok=True)
with open(out, "w", encoding="utf-8") as f:
    json.dump({"network": base, "recordedAtLatestBlock": latest, "cases": cases, "pairs": list(pairs.values())}, f, indent=2)
print(f"wrote {out}: {len(cases)} cases, {sum(len(c['events']) for c in cases)} events, {len(pairs)} pairs")

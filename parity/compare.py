"""Compares two parity traces, one from the GDScript original and one from this port.

Both runs are single peer, so nothing in them depends on packet timing: the state of every tick is a pure function of
the tick number. That makes the comparison exact rather than statistical - the tolerance below is for float formatting,
not for behaviour.
"""

import csv
import sys

# Positions are printed to six decimals; anything bigger than this is a real difference, not rounding
TOLERANCE = 1e-4


def load(path):
    with open(path, newline="") as file:
        return {int(row["tick"]): row for row in csv.DictReader(file)}


def main(gdscript_path, csharp_path):
    gdscript, csharp = load(gdscript_path), load(csharp_path)
    problems = []

    only_gdscript = sorted(set(gdscript) - set(csharp))
    only_csharp = sorted(set(csharp) - set(gdscript))
    if only_gdscript:
        problems.append(f"ticks simulated only by the original: {only_gdscript[:10]} ({len(only_gdscript)} total)")
    if only_csharp:
        problems.append(f"ticks simulated only by the port: {only_csharp[:10]} ({len(only_csharp)} total)")

    common = sorted(set(gdscript) & set(csharp))
    if not common:
        problems.append("the two runs have no tick in common")

    for tick in common:
        a, b = gdscript[tick], csharp[tick]
        if a["counter"] != b["counter"]:
            problems.append(f"@{tick}: counter {a['counter']} against {b['counter']}")
        for axis in ("x", "z"):
            if abs(float(a[axis]) - float(b[axis])) > TOLERANCE:
                problems.append(f"@{tick}: {axis} {a[axis]} against {b[axis]}")
        if a["sims"] != b["sims"]:
            problems.append(f"@{tick}: simulated {a['sims']} times against {b['sims']}")

    exact = sum(1 for tick in common if gdscript[tick]["x"] == csharp[tick]["x"] and gdscript[tick]["z"] == csharp[tick]["z"])
    print(f"PARITY {len(common)} ticks compared, {len(problems)} differences, {exact} with identical positions")

    for problem in problems[:20]:
        print(f"  - {problem}")
    if len(problems) > 20:
        print(f"  ... and {len(problems) - 20} more")

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))

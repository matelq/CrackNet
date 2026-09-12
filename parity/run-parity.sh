#!/usr/bin/env sh
# Runs the same scene against the GDScript original and against this port, and compares the two tick traces.
#
# Both runs use a single offline peer, so nothing depends on packet timing and the comparison is exact. What it
# catches is the class of porting error that never shows up as a crash: a tick simulated one too early or late, input
# landing on the wrong tick, state recorded against the wrong tick, a display offset applied twice.
#
# Usage: parity/run-parity.sh [<godot binary>]
#   The upstream clone is expected in netfox/, as CLAUDE.md describes. GODOT can also come from the environment.
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)
godot=${1:-${GODOT:-$root/.tools/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}}
out=${PARITY_OUT:-$root/.parity}

if [ ! -d "$root/netfox/addons/netfox" ]; then
  echo "No upstream clone in netfox/. Clone foxssake/netfox there first." >&2
  exit 1
fi

mkdir -p "$out"

# The original ships a tickrate of its own; the trace only means something if both run at the same one
sed -i.bak 's|^time/tickrate=.*|time/tickrate=30|' "$root/netfox/project.godot"
rm -f "$root/netfox/project.godot.bak"

mkdir -p "$root/netfox/parity"
cp "$root/parity/parity.gd" "$root/parity/parity-input.gd" "$root/parity/parity.tscn" "$root/netfox/parity/"

echo "== original"
"$godot" --headless --path "$root/netfox" res://parity/parity.tscn -- "--trace=$out/gdscript.csv"

echo "== port"
"$godot" --headless --path "$root" res://examples/parity/Parity.tscn -- "--trace=$out/csharp.csv"

echo "== comparison"
${PYTHON:-$(command -v python3 || command -v python)} "$root/parity/compare.py" "$out/gdscript.csv" "$out/csharp.csv"

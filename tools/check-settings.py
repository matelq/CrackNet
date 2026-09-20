"""Guards the `cracknet/*` project settings: every registered key is read, and no playtest value is committed.

Two failures this catches, both of which have happened:

1. A key renamed in `CrackNetPlugin.cs` and not in `CrackNetSettings.cs`, or the other way round. The setting keeps
   working in the editor - it just silently reads its fallback forever.
2. Autoconnect or the network simulator left on in `project.godot`, which is committed. Then every clone starts its
   instances connected to each other, and the headless checks join the editor's session.
"""
import pathlib, re, sys

root = pathlib.Path(__file__).resolve().parent.parent
errors = []

registered = set(re.findall(r'"(cracknet/[^"]+)"', (root / "addons/cracknet/Editor/CrackNetPlugin.cs").read_text(encoding="utf-8")))
read = set(re.findall(r'"(cracknet/[^"]+)"', (root / "addons/cracknet/CrackNetSettings.cs").read_text(encoding="utf-8")))

for key in sorted(registered - read):
    errors.append(f"{key} is registered by the plugin but never read by CrackNetSettings")
for key in sorted(read - registered):
    errors.append(f"{key} is read by CrackNetSettings but never registered by the plugin")

# What a playtest turns on. project.godot only stores settings that differ from the registered default, so any of
# these appearing at all means someone's local playtest went in with a commit.
playtest = re.compile(r"^(autoconnect/|extras/auto_tile_windows|extras/tile_)")
section = ""
for line in (root / "project.godot").read_text(encoding="utf-8").splitlines():
    if line.startswith("["):
        section = line.strip("[]")
    elif section == "cracknet" and playtest.match(line):
        errors.append(f"project.godot carries a playtest setting: cracknet/{line} - reset it in Project Settings")

for error in errors:
    print(error)
sys.exit(1 if errors else 0)

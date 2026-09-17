"""Compiles the C# blocks of docs/examples.md, so the examples cannot drift from the API.

Each block is preceded by a marker: `<!-- check: usings -->` (prepended to every generated file), `file` (a whole
file), `members <Class>` (members of that partial class) or `body <Class>` (statements, wrapped in a method with
`delta` and `other` in scope). The blocks go into a folder inside the Godot project, the project is built, and the
folder is removed again.

The blocks compile into the same assembly as the addon, so an example using an internal member still passes - exactly
as it would in a game, which compiles the addon into its own assembly too.
"""
import pathlib, re, shutil, subprocess, sys

root = pathlib.Path(__file__).resolve().parent.parent
text = (root / "docs/examples.md").read_text(encoding="utf8")
blocks = re.findall(r"<!-- check: ([^>]*?) -->\n```csharp\n(.*?)```", text, re.S)
unmarked = len(re.findall(r"```csharp", text)) - len(blocks)
if unmarked:
    sys.exit(f"{unmarked} C# block(s) in docs/examples.md have no check marker")

usings = "".join(code for kind, code in blocks if kind == "usings")
out = root / "examples" / "_doccheck"
shutil.rmtree(out, ignore_errors=True)
out.mkdir(parents=True)
try:
    for i, (kind, code) in enumerate(blocks):
        header = usings + "namespace DocCheck;\n\n"
        if kind == "usings":
            continue
        if kind == "file":
            body = code
            # A spawnable class needs its scene next to it, named after it, or the spawn analyzer fails the build
            spawnable = re.search(r"(?:\[Scene\]\s*public partial class|public partial class) (\w+)[^\n]*(?:ISpawnedWith|$)", code, re.M)
            name = re.search(r"public partial class (\w+)", code)
            if name and ("[Scene]" in code or "ISpawnedWith" in code):
                cls = name.group(1)
                (out / f"{cls}.cs").write_text(header + body, encoding="utf8")
                (out / f"{cls}.tscn").write_text(
                    f'[gd_scene load_steps=2 format=3]\n\n[ext_resource type="Script" path="res://examples/_doccheck/{cls}.cs" id="1"]\n\n'
                    f'[node name="{cls}" type="Node"]\nscript = ExtResource("1")\n', encoding="utf8")
                continue
        elif kind.startswith("members "):
            body = f"public partial class {kind.split()[1]}\n{{\n{code}\n}}\n"
        elif kind.startswith("body "):
            cls = kind.split()[1]
            body = f"public partial class {cls}\n{{\n    private void Block{i}(double delta, {cls} other)\n    {{\n{code}\n    }}\n}}\n"
        else:
            sys.exit(f"unknown check marker: {kind}")
        # Godot binds one class per file named after it; these are never attached to nodes, so any name works
        (out / f"Block{i}.cs").write_text(header + body, encoding="utf8")
    build = subprocess.run(["dotnet", "build", str(root / "Netfox.csproj")], capture_output=True, text=True)
    errors = sorted({line.strip() for line in build.stdout.splitlines() if "error CS" in line})
    print("\n".join(errors) if errors else f"docs/examples.md: {len(blocks) - 1} blocks compile")
    sys.exit(build.returncode)
finally:
    shutil.rmtree(out, ignore_errors=True)

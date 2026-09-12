"""Turns the compiler's XML doc output into docs/api.md.

Not a replacement for the guides - it is the index you reach for when you know roughly what you want and not what it
is called. Regenerate after changing public API:

    dotnet build Netfox.csproj
    python docs/generate-api.py
"""

import html
import re
import sys
import xml.etree.ElementTree as ElementTree
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
XML_PATHS = [
    ROOT / ".godot/mono/temp/bin/Debug/Netfox.xml",
    ROOT / ".godot/mono/temp/bin/Debug/Netfox.Core.xml",
]
OUT = ROOT / "docs/api.md"

# Types nobody writing a game needs to reach for
SKIP_NAMESPACES = ("Netfox.Tests", "Netfox.Examples", "Netfox.SourceGenerators", "Netfox.Internal", "Netfox.Editor")
SKIP_SUFFIXES = ("Tests", "Benchmarks")

# Godot's own source generator emits one of these per script class; they are plumbing, not API
GODOT_NESTED = ("MethodName", "PropertyName", "SignalName", "ConstructorName")

# The same generator adds a handful of members to every script class, each documented with this exact sentence.
# One rule rather than a list of names, which was always one name out of date.
GODOT_MARKER = "Do not call this method."

# XML carries no accessibility, so internal members have to be recognised by what their summary says they are
INTERNAL_SUMMARY_PREFIXES = ("Test hook",)

KIND_ORDER = {"P": 0, "F": 1, "M": 2, "E": 3}
KIND_LABEL = {"P": "property", "F": "field", "M": "method", "E": "event"}


def text_of(node):
    """The element's text with tags flattened: <see cref="T:X.Y"/> becomes `Y`."""
    if node is None:
        return ""

    parts = []
    for item in node.iter():
        if item.tag in ("see", "seealso", "paramref", "typeparamref"):
            ref = item.get("cref") or item.get("name") or ""
            parts.append(f"`{ref.split('.')[-1].rstrip('()')}`")
        elif item.tag in ("c", "code"):
            parts.append(f"`{(item.text or '').strip()}`")
        elif item.text and item.tag != "see":
            parts.append(item.text)
        if item.tail:
            parts.append(item.tail)

    collapsed = re.sub(r"\s+", " ", "".join(parts)).strip()
    return html.unescape(collapsed)


def split_member(name):
    """'M:Netfox.NetworkTime.Start' -> ('M', 'Netfox.NetworkTime', 'Start')."""
    kind, _, rest = name.partition(":")
    signature = ""
    if "(" in rest:
        rest, _, signature = rest.partition("(")
        signature = "(" + signature

    if kind == "T":
        return kind, rest, ""

    owner, _, member = rest.rpartition(".")
    return kind, owner, member + signature


def collect():
    types = {}
    members = defaultdict(list)

    for path in XML_PATHS:
        if not path.exists():
            print(f"missing {path}; run dotnet build Netfox.csproj first", file=sys.stderr)
            return None, None

        for entry in ElementTree.parse(path).getroot().findall("./members/member"):
            name = entry.get("name", "")
            summary = text_of(entry.find("summary"))
            kind, owner, member = split_member(name)

            if owner.startswith(SKIP_NAMESPACES) or owner.endswith(SKIP_SUFFIXES):
                continue

            if kind == "T":
                types[owner] = summary
                continue

            if not summary or summary.startswith(INTERNAL_SUMMARY_PREFIXES) or GODOT_MARKER in summary:
                continue

            members[owner].append((kind, member, summary))

    return types, members


def main():
    types, members = collect()
    if types is None:
        return 1

    # A nested type's "namespace" is its declaring type. Fold those away rather than inventing namespaces for them.
    for full_name in list(types):
        namespace, _, short = full_name.rpartition(".")
        if short in GODOT_NESTED or namespace in types:
            del types[full_name]

    by_namespace = defaultdict(list)
    for full_name in types:
        namespace, _, short = full_name.rpartition(".")
        by_namespace[namespace].append((short, full_name))

    lines = [
        "# API reference",
        "",
        "Generated from the XML doc comments by `docs/generate-api.py`. The [guides](README.md) are the place to",
        "start; this is the index for when you know roughly what you want and not what it is called.",
        "",
    ]

    for namespace in sorted(by_namespace):
        lines += [f"## {namespace}", ""]

        for short, full_name in sorted(by_namespace[namespace]):
            lines += [f"### {short}", ""]
            if types[full_name]:
                lines += [types[full_name], ""]

            own = sorted(members.get(full_name, []), key=lambda m: (KIND_ORDER.get(m[0], 9), m[1]))
            if own:
                lines += ["| | Member | Summary |", "|---|---|---|"]
                for kind, member, summary in own:
                    lines.append(f"| {KIND_LABEL.get(kind, kind)} | `{member}` | {summary} |")
                lines.append("")

    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"{OUT}: {len(types)} types across {len(by_namespace)} namespaces")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Generate a LibOrbisPkg GP4 project for a PS2-classic fpkg.

Usage: gen_gp4.py <project_dir> <disc_iso_path> <content_id> [<volume_id>]

Walks <project_dir> (the app0 root) and emits a <file> entry for every file
(referencing it in place via orig_path), plus the disc image at
image/disc01.iso (referenced in place from <disc_iso_path>), then a matching
<rootdir> tree. Prints the GP4 XML to stdout.
"""
import os
import sys
import html


def main():
    if len(sys.argv) < 4:
        sys.exit("usage: gen_gp4.py <project_dir> <disc_iso_path> <content_id> [volume_id]")
    proj = os.path.abspath(sys.argv[1])
    iso = os.path.abspath(sys.argv[2])
    content_id = sys.argv[3]
    volume_id = sys.argv[4] if len(sys.argv) > 4 else "PS2CLASSIC"

    files = []
    for root, dirs, fs in os.walk(proj):
        dirs.sort()
        for f in sorted(fs):
            full = os.path.join(root, f)
            rel = os.path.relpath(full, proj).replace(os.sep, "/")
            files.append((rel, full))
    files.append(("image/disc01.iso", iso))

    dirset = set()
    for rel, _ in files:
        parts = rel.split("/")[:-1]
        for i in range(len(parts)):
            dirset.add("/".join(parts[: i + 1]))

    tree = {}
    for d in sorted(dirset):
        node = tree
        for part in d.split("/"):
            node = node.setdefault(part, {})

    def emit(node, indent):
        out = ""
        for name in sorted(node):
            child = node[name]
            pad = "  " * indent
            if child:
                out += f'{pad}<dir targ_name="{html.escape(name)}">\n'
                out += emit(child, indent + 1)
                out += f"{pad}</dir>\n"
            else:
                out += f'{pad}<dir targ_name="{html.escape(name)}"/>\n'
        return out

    file_lines = "".join(
        f'    <file targ_path="{html.escape(r)}" orig_path="{html.escape(o)}"/>\n'
        for r, o in files
    )

    print(
        f"""<?xml version="1.0" encoding="utf-8"?>
<psproject fmt="gp4" version="1000">
  <volume>
    <volume_type>pkg_ps4_app</volume_type>
    <volume_id>{html.escape(volume_id)}</volume_id>
    <volume_ts>2020-01-01 00:00:00</volume_ts>
    <package content_id="{html.escape(content_id)}" passcode="00000000000000000000000000000000" storage_type="digital50" app_type="full"/>
    <chunk_info chunk_count="1" scenario_count="1">
      <chunks>
        <chunk id="0" layer_no="0" label="Chunk #0"/>
      </chunks>
      <scenarios default_id="0">
        <scenario id="0" type="sp" initial_chunk_count="1" label="Scenario #0">0</scenario>
      </scenarios>
    </chunk_info>
  </volume>
  <files img_no="0">
{file_lines}  </files>
  <rootdir>
{emit(tree, 2)}  </rootdir>
</psproject>"""
    )


if __name__ == "__main__":
    main()

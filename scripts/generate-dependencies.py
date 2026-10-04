#!/usr/bin/env python3

import difflib
import os
import subprocess
import sys
import tempfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(REPO_ROOT, "DEPENDENCIES.md")
SOLUTION = "Vortex.slnx"
ALLOWED = os.path.join(REPO_ROOT, "scripts", "allowed-licenses.json")
NOTICES = "THIRD_PARTY_NOTICES.txt"
TOOL_PACKAGE = "nuget-license"
TOOL_VERSION = "4.0.18"

HEADER = """# Dependencies

Third-party NuGet packages referenced by Vortex, and the licenses they are published
under.

This file is generated. Do not edit it by hand.

## Scope

Vortex depends on these NuGet packages. Their DLLs are included when you build Vortex.
We only use packages with permissive licenses (MIT, Apache 2.0, BSD-3-Clause, Zlib, Unlicense, CC0).

"""


def fail(msg):
    print("error: %s" % msg, file=sys.stderr)
    sys.exit(1)


def tool_path():
    exe = "nuget-license.exe" if os.name == "nt" else "nuget-license"
    cached = os.path.join(tempfile.gettempdir(), "vortex-nuget-license", TOOL_VERSION)
    local = os.path.join(cached, exe)
    if os.path.exists(local):
        return local
    os.makedirs(cached, exist_ok=True)
    print("installing %s %s..." % (TOOL_PACKAGE, TOOL_VERSION))
    proc = subprocess.run(
        ["dotnet", "tool", "install", "--tool-path", cached, TOOL_PACKAGE,
         "--version", TOOL_VERSION],
        capture_output=True, text=True,
    )
    if proc.returncode != 0 or not os.path.exists(local):
        fail("could not install %s\n%s" % (TOOL_PACKAGE, proc.stderr.strip()))
    return local


def render(tool):
    tmp = tempfile.mkdtemp(prefix="vortex-deps-")
    try:
        table = os.path.join(tmp, "deps.md")
        proc = subprocess.run(
            [tool, "-i", SOLUTION, "-t", "-a", ALLOWED, "-o", "Markdown", "-fo", table],
            capture_output=True, text=True, cwd=REPO_ROOT,
        )
        if not os.path.exists(table):
            fail("nuget-license produced no output\n%s" % proc.stderr.strip())
        if proc.returncode != 0:
            fail("license policy check failed; see the table below\n%s"
                 % open(table, encoding="utf-8-sig").read())
        with open(table, encoding="utf-8-sig") as fh:
            body = fh.read()
    finally:
        import shutil
        shutil.rmtree(tmp, ignore_errors=True)
    return HEADER % {"notices": NOTICES} + body


def main():
    if not os.path.exists(os.path.join(REPO_ROOT, SOLUTION)):
        fail("%s not found" % SOLUTION)
    rendered = render(tool_path())

    if "--check" in sys.argv[1:]:
        if not os.path.exists(OUTPUT):
            fail("%s does not exist; run without --check to create it"
                 % os.path.basename(OUTPUT))
        with open(OUTPUT, encoding="utf-8") as fh:
            committed = fh.read()
        if committed == rendered:
            print("%s is up to date" % os.path.basename(OUTPUT))
            return
        diff = difflib.unified_diff(
            committed.splitlines(keepends=True), rendered.splitlines(keepends=True),
            fromfile="committed/%s" % os.path.basename(OUTPUT),
            tofile="generated/%s" % os.path.basename(OUTPUT),
        )
        print("", file=sys.stderr)
        print("%s is out of date:" % os.path.basename(OUTPUT), file=sys.stderr)
        sys.stderr.writelines(diff)
        print("\nRe-run without --check and commit the result.", file=sys.stderr)
        sys.exit(1)

    with open(OUTPUT, "w", encoding="utf-8") as fh:
        fh.write(rendered)
    print("wrote %s" % os.path.basename(OUTPUT))


if __name__ == "__main__":
    main()

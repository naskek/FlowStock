#!/usr/bin/env python3
"""Static contract for canonical CI triggers."""

from pathlib import Path
import re


WORKFLOW = Path(".github/workflows/ci.yml")
source = WORKFLOW.read_text(encoding="utf-8")

match = re.search(r"(?ms)^on:\s*\n(?P<body>.*?)(?=^permissions:\s*$)", source)
if match is None:
    raise AssertionError("CI trigger block is missing")

trigger_block = match.group("body")

if re.search(r"(?m)^\s{2}push:\s*$", trigger_block):
    raise AssertionError("canonical CI must not run automatically on push to main")

if re.search(r"(?m)^\s{2}pull_request:\s*$", trigger_block) is None:
    raise AssertionError("canonical CI must run for pull requests")

if re.search(r"(?m)^\s{4}branches:\s*\[main\]\s*$", trigger_block) is None:
    raise AssertionError("canonical CI pull_request trigger must target main")

if re.search(r"(?m)^\s{2}workflow_dispatch:\s*$", trigger_block) is None:
    raise AssertionError("canonical CI must remain manually dispatchable")

print("CI trigger static contract passed")

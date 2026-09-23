#!/usr/bin/env python3
"""Static contract for the CI-success -> review-ready repository automation."""

from pathlib import Path
import re


WORKFLOW = Path(".github/workflows/review-ready.yml")
source = WORKFLOW.read_text(encoding="utf-8")


def require(pattern: str, message: str) -> None:
    if re.search(pattern, source, re.MULTILINE | re.DOTALL) is None:
        raise AssertionError(message)


require(r"(?m)^\s*workflow_run:\s*$", "workflow_run trigger is missing")
require(r'workflows:\s*\["CI"\]', "automation must depend on canonical CI")
require(r"types:\s*\[completed\]", "workflow must react only to completed CI runs")
require(
    r"github\.event\.workflow_run\.event == 'pull_request'.*"
    r"github\.event\.workflow_run\.conclusion == 'success'",
    "job must require successful pull_request CI",
)
require(r"(?m)^\s*actions:\s*read\s*$", "actions permission must be read-only")
require(r"(?m)^\s*contents:\s*read\s*$", "contents permission must be read-only")
require(r"(?m)^\s*issues:\s*write\s*$", "issues write permission is required only for the marker comment")
require(r"(?m)^\s*pull-requests:\s*read\s*$", "pull-request permission must be read-only")
require(
    r"group:\s*review-ready-\$\{\{ github\.event\.workflow_run\.head_sha \}\}",
    "same-SHA events must share a concurrency group",
)
require(r"cancel-in-progress:\s*false", "same-SHA reruns must serialize rather than cancel one another")
require(
    r"pulls\?state=open&base=main&per_page=100",
    "PR discovery must be limited to open PRs targeting main",
)
require(
    r"select\(\.head\.sha == \\"\$RUN_SHA\\"\)",
    "PR discovery must match the exact successful CI HEAD SHA",
)
require(
    r'\[\.state, \.base\.ref, \.head\.sha\].*current_sha',
    "workflow must re-read current PR state/base/head before commenting",
)
require(
    r'\[\[ "\$state" != "open" \|\| "\$base_ref" != "main" \|\| "\$current_sha" != "\$RUN_SHA" \]\]',
    "stale, closed, or retargeted PRs must be skipped",
)
require(r"flowstock-review-ready:\$RUN_SHA", "dedupe marker must include the exact PR HEAD SHA")
require(
    r"comments\?per_page=100.*contains\(\\\"\$marker\\\"\).*existing",
    "existing SHA marker must be checked before posting",
)
require(
    r'gh api --method POST "repos/\$REPO/issues/\$pr_number/comments"',
    "automation must publish only a top-level PR comment",
)
require(r"Ready for ChatGPT review", "marker must clearly hand off review to ChatGPT")

comment_scan = source.find("comments?per_page=100")
comment_post = source.find('gh api --method POST "repos/$REPO/issues/$pr_number/comments"')
if comment_scan < 0 or comment_post < 0 or comment_scan >= comment_post:
    raise AssertionError("dedupe check must happen before posting the review-ready marker")

for forbidden, message in {
    "actions/checkout": "workflow_run automation must never checkout PR code with a write-capable token",
    "secrets.": "workflow must not read repository secrets",
    "@codex": "Codex review must not be triggered by this automation",
    "ssh ": "review-ready automation must not run SSH/production actions",
    "/opt/FlowStock": "review-ready automation must not reference production checkout",
    "deploy-production": "review-ready automation must not invoke deploy tooling",
}.items():
    if forbidden in source:
        raise AssertionError(message)

print("review-ready workflow static contract passed")

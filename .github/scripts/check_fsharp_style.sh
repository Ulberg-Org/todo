#!/usr/bin/env bash
# CI check for the rules in docs/fsharp-style.md that a grep can catch: no mutation and no null handling outside
# a commented library boundary (§2–3), and the shortest lambdas (§9). Comments are ignored.
set -euo pipefail
status=0
while IFS= read -r file; do
  # Strip // comments before matching, so documentation can mention the words.
  # §2–3: mutation and null. §9: `fun () ->` (use `fun _ ->`) and `fun x -> x.Name` (use `_.Name`).
  if hits=$(sed 's|//.*$||' "$file" |
    grep -nE '\bmutable\b|<-|\bisNull\b|= *null\b|\| *null\b|Unchecked\.defaultof|fun \(\) ->|fun ([a-z][A-Za-z0-9]*) -> \1\.[A-Za-z]+\)'); then
    while IFS= read -r hit; do echo "$file:$hit"; done <<<"$hits"
    status=1
  fi
done < <(git ls-files '*.fs' '*.fsx')
if [ $status -ne 0 ]; then
  echo "F# style: mutation, null or a long lambda found. See docs/fsharp-style.md §2–3 and §9."
else
  echo "F# style ok"
fi
exit $status

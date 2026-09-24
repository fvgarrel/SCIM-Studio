#!/usr/bin/env bash
# Writes a release's notes as Markdown: the commits since the tag before it, grouped by their Conventional Commit type, each
# with its hash linked, then a table of the downloads the release carries.
#
#   packaging/release-notes.sh <tag> [download ...]
#
# A feature, fix or speed-up brings the first paragraph of its body along, which says why; the rest are listed folded away.
# Needs the whole history with its tags - a shallow clone has no tag before this one and lists every commit.
set -euo pipefail

tag="$1"
shift
repo="https://github.com/${GITHUB_REPOSITORY:-fvgarrel/SCIM-Studio}"
previous="$(git describe --tags --abbrev=0 --match 'v*' "$tag^" 2>/dev/null || true)"

# Unit and record separators keep a subject or body that holds any other character in one piece.
git log --no-merges --format='%H%x1f%s%x1f%b%x1e' "${previous:+$previous..}$tag" | awk -v repo="$repo" '
    BEGIN { RS = "\036"; FS = "\037" }

    function paragraph(lines, from, count,    i, text) {
        text = ""
        for (i = from; i <= count && lines[i] != ""; i++) {
            text = text (text == "" ? "" : " ") lines[i]
        }
        return text
    }

    {
        hash = $1
        gsub(/\n/, "", hash)
        if (hash == "") {
            next
        }

        subject = $2
        type = "other"
        scope = ""
        breaking = 0
        if (subject ~ /^[a-z]+(\([^)]*\))?!?: /) {
            head = substr(subject, 1, index(subject, ": ") - 1)
            subject = substr(subject, index(subject, ": ") + 2)
            if (head ~ /!$/) {
                breaking = 1
                head = substr(head, 1, length(head) - 1)
            }
            if (index(head, "(")) {
                scope = substr(head, index(head, "(") + 1)
                scope = substr(scope, 1, length(scope) - 1)
                head = substr(head, 1, index(head, "(") - 1)
            }
            type = head
        }

        count = split($3, lines, "\n")
        why = paragraph(lines, 1, count)
        if (why ~ /^BREAKING[ -]CHANGE: /) {
            why = ""
        }
        for (i = 1; i <= count; i++) {
            if (lines[i] ~ /^BREAKING[ -]CHANGE: /) {
                lines[i] = substr(lines[i], index(lines[i], ": ") + 2)
                why = "**What to do:** " paragraph(lines, i, count)
                breaking = 1
            }
        }

        # A breaking change is listed once, under its own heading and with what to do about it, rather than again under its type.
        group = breaking ? "breaking" : type == "feat" || type == "fix" || type == "perf" ? type : "other"

        # Under a heading the type goes without saying; among the maintenance it is what tells a docs change from a build one.
        label = group == "other" && type != "other" ? type (scope == "" ? "" : "(" scope ")") : scope
        entry = "- " (label == "" ? "" : "**" label ":** ") subject " ([`" substr(hash, 1, 7) "`](" repo "/commit/" hash "))"

        if (group == "other") {
            notes[group] = notes[group] entry "\n"
            others++
        } else {
            notes[group] = notes[group] (notes[group] == "" ? "" : "\n") entry "\n" (why == "" ? "" : "\n  " why "\n")
        }
    }

    END {
        split("breaking feat fix perf", order, " ")
        titles["breaking"] = "Breaking changes"
        titles["feat"] = "New"
        titles["fix"] = "Fixed"
        titles["perf"] = "Faster"
        for (i = 1; i <= 4; i++) {
            if (order[i] in notes) {
                printf "## %s\n\n%s\n", titles[order[i]], notes[order[i]]
            }
        }
        if (others) {
            printf "<details>\n<summary>Maintenance - %d %s</summary>\n\n", others, others == 1 ? "change" : "changes"
            printf "%s\n</details>\n\n", notes["other"]
        }
    }
'

if (($# > 0)); then
    printf '## Downloads\n\n| System | File |\n| --- | --- |\n'
    for file in "$@"; do
        name="$(basename "$file")"
        case "$name" in
            *-win-x64.*) system="Windows (x64)" ;;
            *-linux-x64.*) system="Linux (x64)" ;;
            *-osx-arm64.*) system="macOS (Apple silicon)" ;;
            *-osx-x64.*) system="macOS (Intel)" ;;
            *) system="" ;;
        esac
        printf '| %s | [%s](%s/releases/download/%s/%s) |\n' "$system" "$name" "$repo" "$tag" "$name"
    done
    printf '\n'
fi

if [[ -n "$previous" ]]; then
    printf '**Every change:** [%s...%s](%s/compare/%s...%s)\n' "$previous" "$tag" "$repo" "$previous" "$tag"
else
    printf '**Every change:** [the history up to %s](%s/commits/%s)\n' "$tag" "$repo" "$tag"
fi

#!/bin/bash

# Call from a fixture which owns a writable game directory and its enrolled client certificate.
verify_directory_pages() {
    local listing="$1" game="$2" work="$3"
    shift 3
    local fixture="$game/pagination-test" index name cursor first_cursor page status
    local -a client=(curl --silent --show-error --max-time 20 "$@")
    local -a options
    mkdir "$fixture"
    for ((index=1; index<=4201; index++)); do
        printf -v name 'mod-%04d.cfg' "$index"
        : > "$fixture/$name"
        printf '%s\n' "$name"
    done > "$work/paging-expected"
    ln -s /etc/passwd "$fixture/mod-link.cfg"

    cursor=''
    first_cursor=''
    : > "$work/paging-observed"
    for ((page=1; page<=23; page++)); do
        options=(--get --data-urlencode path=pagination-test)
        if [[ -n "$cursor" ]]; then options+=(--data-urlencode "cursor=$cursor"); fi
        "${client[@]}" --fail "${options[@]}" "$listing/page" > "$work/paging-response"
        jq -e '(.entries | length) <= 200 and .truncated == (.nextCursor != null)' "$work/paging-response" >/dev/null
        jq -r '.entries[].name' "$work/paging-response" >> "$work/paging-observed"
        cursor="$(jq -r '.nextCursor // empty' "$work/paging-response")"
        if [[ "$page" == 1 ]]; then first_cursor="$cursor"; fi
        if [[ -z "$cursor" ]]; then break; fi
    done
    test -n "$first_cursor"
    test -z "$cursor"
    LC_ALL=C sort "$work/paging-observed" > "$work/paging-sorted"
    cmp "$work/paging-expected" "$work/paging-sorted"

    "${client[@]}" --fail --get --data-urlencode path=pagination-test --data-urlencode filter=missing-mod \
        "$listing/page" > "$work/paging-response"
    cursor="$(jq -er 'select(.entries == [] and .truncated) | .nextCursor' "$work/paging-response")"
    "${client[@]}" --fail --get --data-urlencode path=pagination-test --data-urlencode filter=missing-mod \
        --data-urlencode "cursor=$cursor" "$listing/page" > "$work/paging-response"
    jq -e '.entries == [] and .nextCursor == null and (.truncated | not)' "$work/paging-response" >/dev/null

    "${client[@]}" --fail --get --data-urlencode path=pagination-test --data-urlencode filter=MOD-0109 \
        "$listing/page" > "$work/paging-response"
    jq -r '.entries[].name' "$work/paging-response" > "$work/paging-match"
    cursor="$(jq -er .nextCursor "$work/paging-response")"
    "${client[@]}" --fail --get --data-urlencode path=pagination-test --data-urlencode filter=MOD-0109 \
        --data-urlencode "cursor=$cursor" "$listing/page" > "$work/paging-response"
    jq -r '.entries[].name' "$work/paging-response" >> "$work/paging-match"
    test "$(cat "$work/paging-match")" = mod-0109.cfg
    jq -e '.nextCursor == null' "$work/paging-response" >/dev/null

    status="$("${client[@]}" --get --data-urlencode path=pagination-test --data-urlencode filter=mod \
        --data-urlencode "cursor=$first_cursor" --output /dev/null --write-out '%{http_code}' "$listing/page")"
    test "$status" = 409
    status="$("${client[@]}" --get --data-urlencode "cursor=$first_cursor" \
        --output /dev/null --write-out '%{http_code}' "$listing/page")"
    test "$status" = 409

    : > "$fixture/new-file.cfg"
    touch -m -d '@1760000000' "$fixture"
    status="$("${client[@]}" --get --data-urlencode path=pagination-test --data-urlencode "cursor=$first_cursor" \
        --output /dev/null --write-out '%{http_code}' "$listing/page")"
    test "$status" = 409
    echo 'Directory pagination returned all 4201 files, continued sparse filters and expired stale cursors.'
}

#!/usr/bin/env bash
set -euo pipefail

image="${1:-alpine:3.24}"
driver="${2:-arcadenode-quota}"
read -r marker < /proc/sys/kernel/random/uuid

new_name() {
    local uuid
    read -r uuid < /proc/sys/kernel/random/uuid
    printf 'arcadenode-%s-data\n' "${uuid//-/}"
}
first="$(new_name)"
second="$(new_name)"
empty="$(new_name)"
size=33554432

run_volume() {
    local name=$1
    shift
    docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
        --user 10001:10001 --mount "type=volume,source=$name,target=/data,volume-nocopy" \
        "$image" sh -ec "$1"
}

cleanup() {
    local volume
    for volume in "$first" "$second" "$empty"; do
        if [ "$(docker volume inspect --format '{{index .Labels "arcadenode.quota-probe"}}' "$volume" 2>/dev/null)" = "$marker" ]; then
            run_volume "$volume" 'rm -f -- /data/probe' || true
            docker volume rm "$volume" >/dev/null || true
        fi
    done
}
trap cleanup EXIT

for volume in "$first" "$second" "$empty"; do
    docker volume create --driver "$driver" --opt "size=$size" --label "arcadenode.quota-probe=$marker" "$volume" >/dev/null
    test "$(docker volume inspect --format '{{index .Options "size"}}' "$volume")" = "$size"
    test "$(docker volume inspect --format '{{index .Labels "arcadenode.quota-probe"}}' "$volume")" = "$marker"
done
docker volume create --driver "$driver" --opt "size=$size" --label "arcadenode.quota-probe=$marker" "$first" >/dev/null

run_volume "$first" 'dd if=/dev/zero of=/data/probe bs=1M count=16 && sync'
if overflow="$(run_volume "$first" 'dd if=/dev/zero of=/data/probe bs=1M count=32 seek=16 conv=notrunc && sync' 2>&1)"; then
    echo 'A container exceeded its confirmed XFS quota.' >&2
    exit 1
fi
printf '%s\n' "$overflow" | grep -Eiq 'disk quota exceeded|no space left on device'
run_volume "$first" 'test "$(stat -c %s /data/probe)" -le 33554432'
run_volume "$second" 'dd if=/dev/zero of=/data/probe bs=1M count=16 && sync'
if removed="$(docker volume rm "$first" 2>&1)"; then
    echo 'Docker removed an ArcadeNode volume that contained game data.' >&2
    exit 1
fi
printf '%s\n' "$removed" | grep -Fq 'contains data'
docker volume rm "$empty" >/dev/null
echo 'ArcadeNode XFS quota limits, volume isolation and data retention verified.'

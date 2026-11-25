#!/bin/bash
set -euo pipefail

image="${1:-arcadenode-zomboid-check}"
branch="${ZOMBOID_SMOKE_BRANCH:-legacy41}"
[[ "$branch" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$ ]]
source "$(dirname "${BASH_SOURCE[0]}")/official-game-cutover-common.sh"
require_local_game_runner "$image"
for tool in mkfs.xfs sqlite3; do command -v "$tool" >/dev/null; done

scratch="$(mktemp -d /tmp/arcadenode-zomboid-cutover.XXXXXXXX)"
[[ "$scratch" == /tmp/arcadenode-zomboid-cutover.* ]]
volumes="$scratch/volumes"
installer=''
source_container=''
destination_container=''
cleanup() {
    local status=$?
    trap - EXIT
    for container in "$source_container" "$destination_container" "$installer"; do
        if [[ -z "$container" ]]; then continue; fi
        if [[ "$status" != 0 ]]; then docker logs --tail 35 "$container" >&2 || true; fi
        docker rm -f "$container" >/dev/null 2>&1 || true
    done
    if [[ "$status" != 0 && -n "$installer" ]]; then
        sudo -n tail -n 25 "$volumes/steam/.local/share/Steam/logs/bootstrap_log.txt" >&2 || true
    fi
    if mountpoint -q "$volumes"; then sudo -n umount "$volumes" || exit 1; fi
    sudo -n rm -rf -- "$scratch"
    exit "$status"
}
trap cleanup EXIT
chmod 0711 "$scratch"
server_id="$(cat /proc/sys/kernel/random/uuid)"
save_name=ArcadeCutover
available="$(df -B1 --output=avail "$scratch" | tail -n 1)"
if (( available < 10 * 1024 * 1024 * 1024 )); then
    echo 'The disposable runner needs at least 10 GiB free for the Steam depot and cutover.' >&2
    exit 1
fi

publish_cutover_preflight "$scratch/preflight"
preflight="$scratch/preflight/Preflight"
mkdir "$volumes"
truncate -s 24G "$scratch/cutover.xfs"
mkfs.xfs -f -m reflink=1 "$scratch/cutover.xfs" >/dev/null
sudo -n mount -o loop,nodev,nosuid "$scratch/cutover.xfs" "$volumes"
sudo -n install -d -m 0700 -o 10001 -g 10001 "$volumes/source" "$volumes/destination" "$volumes/steam"
sudo -n touch "$volumes/source/.reflink-check"
sudo -n cp --reflink=always "$volumes/source/.reflink-check" "$volumes/destination/.reflink-check"
sudo -n rm -- "$volumes/source/.reflink-check" "$volumes/destination/.reflink-check"
isolated=(--user 10001:10001 --read-only --cap-drop ALL --security-opt no-new-privileges
    --pids-limit 512 --memory 3g --memory-swap 3g --cpus 2
    --tmpfs /tmp:rw,nosuid,nodev,noexec,size=64m,mode=1777)

steam_branch=()
if [[ "$branch" != public ]]; then steam_branch=(-beta "$branch"); fi
installer="$(docker create --name "zomboid-install-$server_id" "${isolated[@]}" \
    --env HOME=/home/steam-install --workdir /home/steam-install \
    --mount "type=bind,src=$volumes/steam,dst=/home/steam-install" \
    --mount "type=bind,src=$volumes/source,dst=/home/container" \
    --entrypoint /usr/bin/steamcmd "$image" \
    +@ShutdownOnFailedCommand 1 +@sSteamCmdForcePlatformType linux +force_install_dir /home/container \
    +login anonymous +app_info_update 1 +app_update 380870 "${steam_branch[@]}" validate +quit)"
# The base image has already updated SteamCMD as root. Seed a separate writable home for its non-root runtime.
steam_client="$volumes/steam/.local/share/Steam/steamcmd"
sudo -n mkdir -p "$steam_client"
sudo -n docker cp "$installer:/root/.local/share/Steam/steamcmd/." "$steam_client/"
sudo -n test -x "$steam_client/linux32/steamcmd"
sudo -n chown -hR 10001:10001 "$volumes/steam"
timeout --kill-after=15 1800 docker start -a "$installer"
test "$(docker inspect --format '{{.State.ExitCode}}' "$installer")" = 0
docker rm "$installer" >/dev/null
installer=''
sudo -n test -x "$volumes/source/ProjectZomboid64"
sudo -n test -x "$volumes/source/jre64/bin/java"
sudo -n setpriv --reuid=10001 --regid=10001 --clear-groups bash -eu -s -- "$volumes/source" "$save_name" <<'CONFIGURE'
    source="$1"
    config="$source/ProjectZomboid64.json"
    [[ -f "$config" && ! -L "$config" ]]
    temp="$(mktemp "$source/.launcher.XXXXXX")"
    jq -e '.vmArgs |= (map(select(test("^-Xm[sx]|^-XX:(InitialHeapSize|MinHeapSize|MaxHeapSize|SoftMaxHeapSize)=") | not)) + ["-Xmx1536m"])' "$config" > "$temp"
    chmod --reference="$config" "$temp"
    mv -fT -- "$temp" "$config"
    mkdir -p "$source/.cache/Server"
    printf "%s\n" DefaultPort=16261 UDPPort=16262 Public=false Open=false RCONPassword= \
        > "$source/.cache/Server/$2.ini"
CONFIGURE

# Keep independent file inodes for receipt validation while sharing the read-only depot extents.
sudo -n cp -a --reflink=always "$volumes/source/." "$volumes/destination/"
source_container="$(docker run -dit --name "$server_id" --network none "${isolated[@]}" \
    --label Service=Pterodactyl --label ContainerType=server_process \
    --mount "type=bind,src=$volumes/source,dst=/home/container" \
    --workdir /home/container --env HOME=/home/container --entrypoint /bin/bash "$image" -ec '
        export PATH="$PWD/jre64/bin:$PATH"
        export LD_LIBRARY_PATH="$PWD/linux64:$PWD/natives:$PWD:$PWD/jre64/lib/amd64"
        for library in jre64/lib/libjsig.so jre64/lib/amd64/libjsig.so; do
            if [[ -f "$library" ]]; then export LD_PRELOAD="$PWD/$library"; break; fi
        done
        exec ./ProjectZomboid64 -nosteam -servername "$1" -ip 0.0.0.0 -port 16261 -udpport 16262 \
            -cachedir=/home/container/.cache -adminusername admin -adminpassword ArcadeNodeSyntheticAdmin2026
    ' _ "$save_name")"
wait_for_log "$source_container" 'SERVER STARTED' 240

if sudo -n "$preflight" capture-pterodactyl-volume "$server_id" > "$scratch/live.json"; then
    echo 'The final volume capture accepted a running source.' >&2
    exit 1
fi
copy_status=0
sudo -n rsync -rltpx --safe-links --chown=10001:10001 \
    "$volumes/source/.cache/" "$volumes/destination/.cache/" || copy_status=$?
[[ "$copy_status" == 0 || "$copy_status" == 24 ]]

source_db="$volumes/source/.cache/db/$save_name.db"
destination_db="$volumes/destination/.cache/db/$save_name.db"
send_console "$source_container" 'adduser "MigrationProbe" "ArcadeNodeSyntheticPlayer2026"'
count=0
for attempt in {1..30}; do
    count="$(sudo -n sqlite3 -readonly -cmd '.timeout 1000' "$source_db" \
        "SELECT COUNT(*) FROM whitelist WHERE lower(username) = 'migrationprobe';" 2>/dev/null || true)"
    if [[ "$count" == 1 ]]; then break; fi
    sleep 1
done
test "$count" = 1
send_console "$source_container" save quit
wait_for_clean_exit "$source_container"

test "$(sudo -n sqlite3 -readonly "$source_db" 'PRAGMA quick_check;')" = ok
find_player_rows() {
    sudo -n sqlite3 -readonly "$1" 'SELECT hex(username), hex(password) FROM whitelist ORDER BY username, password;'
}
find_player_rows "$source_db" > "$scratch/source-players"
test -s "$scratch/source-players"
sudo -n test -d "$volumes/source/.cache/Saves/Multiplayer/$save_name"
test -n "$(sudo -n find "$volumes/source/.cache/Saves/Multiplayer/$save_name" -type f -print -quit)"

sudo -n "$preflight" capture-pterodactyl-volume "$server_id" > "$scratch/source.json"
jq -e --arg id "$source_container" '.schema_version == 3 and .source_process.container_id == $id and .source_process.stopped_at != null' \
    "$scratch/source.json" >/dev/null
sudo -n rsync -rltpx --safe-links --delete-delay --checksum --chown=10001:10001 "$volumes/source/" "$volumes/destination/"
verify_destination() {
    local report="$1"
    sudo -n setpriv --reuid=10001 --regid=10001 --clear-groups --no-new-privs "$preflight" \
        verify-volume "$server_id" - "$volumes/destination" --project-zomboid "$save_name" < "$scratch/source.json" > "$report"
    jq -e --arg save "$save_name" '.matches and .source.tree_sha256 == .destination.tree_sha256 and
        .project_zomboid == {layout:"pterodactyl",save_name:$save,game_port:16261,direct_port:16262,rcon_port:null}' \
        "$report" >/dev/null
}
verify_destination "$scratch/verified.json"
destination_container="$(docker create -it --name "zomboid-restored-$server_id" --network none "${isolated[@]}" \
    --env ARCADENODE_RESTORED_VOLUME=true --env NO_STEAM=true --env "SERVER_NAME=$save_name" \
    --env ARCADENODE_JAVA_HEAP_MIB=1536 --env ARCADENODE_PUBLISHED_PORTS=16261,16262 \
    --mount "type=bind,src=$volumes/destination,dst=/home/pzserver" "$image")"
test "$(docker inspect --format '{{.State.Status}}' "$destination_container")" = created
sudo -n "$preflight" confirm-pterodactyl-source "$server_id" "$scratch/source.json" --writes-fenced > "$scratch/fence.json"
jq -e --arg digest "$(jq -er '.tree_sha256' "$scratch/source.json")" \
    '.schema_version == 2 and .writes_fenced and .tree_sha256 == $digest' "$scratch/fence.json" >/dev/null
verify_destination "$scratch/final-volume.json"
docker start "$destination_container" >/dev/null
wait_for_log "$destination_container" 'SERVER STARTED' 240
send_console "$destination_container" save quit
wait_for_clean_exit "$destination_container"
test "$(sudo -n sqlite3 -readonly "$destination_db" 'PRAGMA quick_check;')" = ok
find_player_rows "$destination_db" > "$scratch/destination-players"
cmp "$scratch/source-players" "$scratch/destination-players"
sudo -n test -d "$volumes/destination/.cache/Saves/Multiplayer/$save_name"
echo "Official Zomboid branch $branch reopened the same world and preserved its player credentials."

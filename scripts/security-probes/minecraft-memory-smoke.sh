#!/bin/bash
set -euo pipefail

image="${1:-arcadenode-minecraft-check}"
docker run --rm -i --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
    --memory 512m --memory-swap 512m \
    --tmpfs /tmp:size=16m,mode=1777,exec \
    --tmpfs /home/minecraft:size=16m,uid=10001,gid=10001,mode=0700 \
    --env ARCADENODE_JAVA_HEAP_MIB=384 --env SERVER_MEMORY=512 \
    --entrypoint /bin/bash "$image" -s <<'PROBE'
set -euo pipefail
test "$(cat /sys/fs/cgroup/memory.max 2>/dev/null || cat /sys/fs/cgroup/memory/memory.limit_in_bytes)" = 536870912

export TEST_JAVA="$(command -v java)"
mkdir /tmp/bin server
cat > /tmp/bin/java <<'JAVA'
#!/bin/bash
set -euo pipefail
options=()
while [[ $# -gt 0 && $1 != -jar ]]; do
    options+=("$1")
    shift
done
[[ $# -ge 2 && -f $2 ]]
exec "$TEST_JAVA" "${options[@]}" -XX:+PrintFlagsFinal -version
JAVA
chmod 700 /tmp/bin/java
export PATH="/tmp/bin:$PATH"

touch server/server.jar server.jar
printf 'eula=true\n' > eula.txt
printf 'server-port=25565\n' > server.properties

check_heap() {
    local expected_initial="$1"
    /usr/local/bin/arcadenode-entrypoint > /tmp/jvm.log 2>&1 || { cat /tmp/jvm.log; return 1; }
    awk -v initial="$expected_initial" '
        $2 == "MaxHeapSize" { max = $4 }
        $2 == "InitialHeapSize" { start = $4 }
        END { exit !(max == 402653184 && (initial == "" || start == initial)) }
    ' /tmp/jvm.log || { cat /tmp/jvm.log; return 1; }
}

SERVER_TYPE=paper MIN_MEMORY=128 check_heap 134217728
SERVER_TYPE=paper MIN_MEMORY=512 check_heap 402653184
SERVER_TYPE=vanilla check_heap ''
SERVER_TYPE=vanilla ARCADENODE_RESTORED_VOLUME=true check_heap ''
echo 'Minecraft JVM heap follows the reserved Docker memory for new and restored servers.'
PROBE

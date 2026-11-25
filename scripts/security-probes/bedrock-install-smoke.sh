#!/bin/bash
set -euo pipefail

scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT
install=game-servers/minecraft-bedrock/download-bedrock.sh
mkdir -p "$scratch/bin" "$scratch/server/worlds"

export BEDROCK_TEST_CATALOG="$scratch/catalog.json"
export BEDROCK_TEST_ZIP="$scratch/current.zip"
export BEDROCK_TEST_REQUESTS="$scratch/requests"
export PATH="$scratch/bin:$PATH"
python3 - "$scratch" <<'PY'
import json, pathlib, sys, zipfile

root = pathlib.Path(sys.argv[1])
base = "https://www.minecraft.net/bedrockdedicatedserver"
catalog = {"result": {"links": [
    {"downloadType": "serverBedrockPreviewLinux", "downloadUrl": base + "/bin-linux-preview/bedrock-server-1.26.70.26.zip"},
    {"downloadType": "serverBedrockLinux", "downloadUrl": base + "/bin-linux/bedrock-server-1.26.66.1.zip"},
]}}
(root / "catalog.json").write_text(json.dumps(catalog))
catalog["result"]["links"][1]["downloadUrl"] = "https://packages.example.invalid/bedrock-server-1.26.66.1.zip"
(root / "foreign.json").write_text(json.dumps(catalog))
for release in ("current", "next"):
    with zipfile.ZipFile(root / f"{release}.zip", "w") as package:
        package.writestr("bedrock_server", b"\x7fELF" + release.encode())
        package.writestr("resource_packs/vanilla/manifest.json", release)
        package.writestr("server.properties", "from-package\n")
        package.writestr("allowlist.json", "[]")
        package.writestr("permissions.json", "[]")
        package.writestr("worlds/old/db/CURRENT", "from-package")
PY

cat > "$scratch/bin/curl" <<'SH'
#!/bin/bash
set -euo pipefail
output=''
url="${!#}"
while (($#)); do
    if [[ "$1" == --output ]]; then output="$2"; shift; fi
    shift
done
printf '%s\n' "$url" >> "$BEDROCK_TEST_REQUESTS"
case "$url" in
    https://net-secondary.web.minecraft-services.net/api/v1.0/download/links) cat "$BEDROCK_TEST_CATALOG" ;;
    https://www.minecraft.net/bedrockdedicatedserver/bin-linux/bedrock-server-*.zip) cp -- "$BEDROCK_TEST_ZIP" "$output" ;;
    *) exit 69 ;;
esac
SH
chmod +x "$scratch/bin/curl"

printf 'existing configuration\n' > "$scratch/server/server.properties"
printf '[{"name":"Existing"}]\n' > "$scratch/server/allowlist.json"
printf '[{"xuid":"1234567890","permission":"operator"}]\n' > "$scratch/server/permissions.json"
mkdir -p "$scratch/server/worlds/old/db"
printf 'world data' > "$scratch/server/worlds/old/db/CURRENT"
bash "$install" LATEST "$scratch/server"
grep -Fxq 1.26.66.1 "$scratch/server/.version"
grep -Fxq 'existing configuration' "$scratch/server/server.properties"
grep -Fq 'Existing' "$scratch/server/allowlist.json"
grep -Fq '1234567890' "$scratch/server/permissions.json"
grep -Fxq 'world data' "$scratch/server/worlds/old/db/CURRENT"
test "$(cat "$scratch/server/resource_packs/vanilla/manifest.json")" = current
test "$(wc -l < "$BEDROCK_TEST_REQUESTS")" -eq 2

# Git Bash identifies executable files by header. The staged ELF marker is checked above.
if [[ "$(uname -s)" == MINGW* ]]; then
    printf '#!/bin/bash\nexit 0\n' > "$scratch/server/bedrock_server"
    chmod +x "$scratch/server/bedrock_server"
fi
BEDROCK_TEST_ZIP="$scratch/missing.zip" bash "$install" 1.26.66.1 "$scratch/server"
test "$(wc -l < "$BEDROCK_TEST_REQUESTS")" -eq 2
if BEDROCK_TEST_ZIP="$scratch/missing.zip" bash "$install" 1.26.66.2 "$scratch/server"; then
    echo 'Bedrock accepted a failed package download.' >&2
    exit 1
fi
grep -Fxq 1.26.66.1 "$scratch/server/.version"
test "$(cat "$scratch/server/resource_packs/vanilla/manifest.json")" = current

mkdir "$scratch/empty"
if BEDROCK_TEST_CATALOG="$scratch/foreign.json" bash "$install" LATEST "$scratch/empty"; then
    echo 'Bedrock accepted a foreign download URL.' >&2
    exit 1
fi
test "$(wc -l < "$BEDROCK_TEST_REQUESTS")" -eq 4
BEDROCK_TEST_CATALOG="$scratch/missing.json" bash "$install" LATEST "$scratch/server"
grep -Fxq 1.26.66.1 "$scratch/server/.version"
mkdir "$scratch/server/.bedrock-update-pending"
if BEDROCK_TEST_CATALOG="$scratch/missing.json" bash "$install" LATEST "$scratch/server"; then
    echo 'Bedrock accepted a partially installed release during an outage.' >&2
    exit 1
fi
if [[ "$(uname -s)" == Linux ]]; then
    mkdir "$scratch/data"
    mv "$scratch/server/resource_packs" "$scratch/data/resource_packs"
    ln -s ../data/resource_packs "$scratch/server/resource_packs"
fi

BEDROCK_TEST_ZIP="$scratch/next.zip" bash "$install" 1.26.66.2 "$scratch/server"
grep -Fxq 1.26.66.2 "$scratch/server/.version"
test "$(cat "$scratch/server/resource_packs/vanilla/manifest.json")" = next
grep -Fxq 'existing configuration' "$scratch/server/server.properties"
grep -Fq 'Existing' "$scratch/server/allowlist.json"
grep -Fq '1234567890' "$scratch/server/permissions.json"
grep -Fxq 'world data' "$scratch/server/worlds/old/db/CURRENT"
test ! -e "$scratch/server/.bedrock-update-pending"
if [[ "$(uname -s)" == Linux ]]; then
    test "$(readlink "$scratch/server/resource_packs")" = ../data/resource_packs
fi

export BEDROCK_TEST_HOME="$scratch"
sed 's|^BEDROCK_HOME="/home/bedrock"$|BEDROCK_HOME="${BEDROCK_TEST_HOME}"|;
    s|^/usr/local/bin/arcadenode-download-bedrock .*|:|' \
    game-servers/minecraft-bedrock/entrypoint.sh > "$scratch/entrypoint.sh"
cat > "$scratch/server/bedrock_server" <<'SH'
#!/bin/bash
exit 0
SH
chmod +x "$scratch/server/bedrock_server"
# Use existing directories so this fixture also runs on hosts without symlink privileges.
mkdir -p "$scratch/server/behavior_packs"
SERVER_NAME='Changed environment' ALLOW_LIST_USERS='Replacement' OPS=111 \
    bash "$scratch/entrypoint.sh" >/dev/null
grep -Fxq 'existing configuration' "$scratch/server/server.properties"
grep -Fq 'Existing' "$scratch/server/allowlist.json"
grep -Fq '1234567890' "$scratch/server/permissions.json"
grep -Fxq 'world data' "$scratch/server/worlds/old/db/CURRENT"
echo 'Bedrock official catalog selection, failed updates, and persistent state passed.'

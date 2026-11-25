#!/usr/bin/env bash
set -euo pipefail

container=''
cleanup() {
    if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null || true; fi
}
trap cleanup EXIT

# This credential and every database created by the test disappear with the container.
export MSSQL_SA_PASSWORD="ArcadeSmoke9!$(openssl rand -hex 18)"
container="$(docker run -d -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e MSSQL_SA_PASSWORD \
    -p 127.0.0.1::1433 mcr.microsoft.com/mssql/server:2022-latest)"
for attempt in {1..45}; do
    if docker exec "$container" bash -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -l 2 -Q "SELECT 1"' >/dev/null 2>&1; then
        break
    fi
    if [[ "$attempt" == 45 ]]; then docker logs "$container"; exit 1; fi
    sleep 2
done
export ARCADENODE_SQL_SMOKE_PORT
ARCADENODE_SQL_SMOKE_PORT="$(docker port "$container" 1433/tcp | sed -n 's/^127\.0\.0\.1://p')"
export ARCADENODE_SQL_SMOKE_PASSWORD="$MSSQL_SA_PASSWORD"
dotnet run --project scripts/security-probes/SecurityProbes.csproj --no-launch-profile -- --sqlserver

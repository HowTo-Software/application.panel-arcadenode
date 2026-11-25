#!/usr/bin/env bash
set -eu
project_root="$(cd -- "$(dirname -- "$0")/.." && pwd)"
case "${1:-help}" in
  validate) docker compose --project-directory "$project_root" -f "$project_root/docker-compose.yml" config --quiet ;;
  build) docker compose --project-directory "$project_root" -f "$project_root/docker-compose.yml" build backend frontend ;;
  up) docker compose --project-directory "$project_root" -f "$project_root/docker-compose.yml" up -d backend frontend ;;
  stop) docker compose --project-directory "$project_root" -f "$project_root/docker-compose.yml" stop backend frontend ;;
  logs) docker compose --project-directory "$project_root" -f "$project_root/docker-compose.yml" logs --tail 100 backend frontend ;;
  migrate) docker compose --project-directory "$project_root" -f "$project_root/docker-compose.yml" --profile maintenance run --rm migrate ;;
  *) printf '%s\n' 'Usage: scripts/deploy.sh {validate|build|up|stop|logs|migrate}' 'Configure external SQL, TLS and node enrollment first: deploy/README.md' ;;
esac

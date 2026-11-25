param([ValidateSet('validate','build','up','stop','logs','migrate','help')][string]$Action = 'help')
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$composeFile = Join-Path $projectRoot 'docker-compose.yml'
switch ($Action) {
    'validate' { & docker compose --project-directory $projectRoot -f $composeFile config --quiet }
    'build' { & docker compose --project-directory $projectRoot -f $composeFile build backend frontend }
    'up' { & docker compose --project-directory $projectRoot -f $composeFile up -d backend frontend }
    'stop' { & docker compose --project-directory $projectRoot -f $composeFile stop backend frontend }
    'logs' { & docker compose --project-directory $projectRoot -f $composeFile logs --tail 100 backend frontend }
    'migrate' { & docker compose --project-directory $projectRoot -f $composeFile --profile maintenance run --rm migrate }
    'help' { Write-Output 'Usage: scripts/deploy.ps1 -Action validate|build|up|stop|logs|migrate. Read deploy/README.md first.'; exit 0 }
}
if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE" }

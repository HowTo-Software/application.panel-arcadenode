[CmdletBinding()]
param(
    [Parameter(Position=0, ValueFromRemainingArguments=$true)]
    [ValidateSet('all','minecraft','minecraft-bedrock','project-zomboid','arma-reforger')]
    [string[]]$Images = @('all'),
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$')]
    [string]$Version = 'secure',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9./:_-]{0,200}$')]
    [string]$Registry,
    [switch]$Push,
    [string]$RemoteHost,
    [switch]$List,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
$knownImages = @('minecraft','minecraft-bedrock','project-zomboid','arma-reforger')
if ($Help) { Write-Output 'Local builds: build.ps1 [images] -Version secure [-Registry registry/namespace] [-Push]. Remote builds/cleanup have been retired.'; exit 0 }
if ($List) { Write-Output $knownImages; exit 0 }
if ($RemoteHost) { throw 'Remote shell builds are retired. Run the build with an explicitly configured Docker context.' }
if ($Push -and -not $Registry) { throw 'Specify the controlled registry explicitly before publishing images.' }
if ($Images -contains 'all') { $Images = $knownImages }
foreach ($image in ($Images | Select-Object -Unique)) {
    $contextPath = Join-Path $PSScriptRoot $image
    $localTag = "arcadenode/${image}:${Version}"
    & docker build --tag $localTag $contextPath
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $image" }
    if ($Registry) {
        $registryTag = "$($Registry.TrimEnd('/'))/${image}:${Version}"
        & docker tag $localTag $registryTag
        if ($LASTEXITCODE -ne 0) { throw "Tag failed for $image" }
        if ($Push) {
            & docker push $registryTag
            if ($LASTEXITCODE -ne 0) { throw "Push failed for $image" }
        }
    }
}

# Compatibility entry point; keep one tested build and packaging pipeline.
param([string]$Destination, [switch]$IntegrationTests)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'build-treegit.ps1') -Destination $Destination -IntegrationTests:$IntegrationTests

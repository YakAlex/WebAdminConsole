#Requires -Version 5.1
<#
    Faza 7 (T7.1/T7.2): publishes AdminConsole.Api + AdminConsole.Migration in
    Release configuration, self-contained win-x64, into a single shared
    publish/ folder -- this is what gets copied to the target server on deploy.

    NO -p:PublishSingleFile=true -- intentional (agreed earlier): the Kestrel
    host has no WPF/BAML dependencies, so it doesn't have the single-file
    temp-extraction problem class the old WPF client had, and enabling it
    would only complicate diagnostics for no benefit.

    AdminConsole.Api.csproj builds the React frontend (adminconsole-web) and
    embeds it into wwwroot during publish itself (Target "PublishFrontend",
    Condition Configuration==Release) -- no separate step needed here.
#>

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$publishDir = Join-Path $root 'publish'
$rid        = 'win-x64'
$config     = 'Release'

Write-Host "== AdminConsole publish ==" -ForegroundColor Cyan
Write-Host "Output: $publishDir"

if (Test-Path $publishDir) {
    Write-Host "Removing existing publish/ ..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Path $publishDir | Out-Null

$projects = @(
    'AdminConsole.Api\AdminConsole.Api.csproj',
    'AdminConsole.Migration\AdminConsole.Migration.csproj'
)

foreach ($project in $projects) {
    $projectPath = Join-Path $root $project
    Write-Host ""
    Write-Host "-- Publishing $project --" -ForegroundColor Cyan

    dotnet publish $projectPath `
        -c $config `
        -r $rid `
        --self-contained true `
        -o $publishDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project (exit code $LASTEXITCODE)"
    }
}

Write-Host ""
Write-Host "== Publish complete ==" -ForegroundColor Green
Write-Host "Output directory: $publishDir"

$exeFiles = Get-ChildItem -Path $publishDir -Filter '*.exe' -File
Write-Host ""
Write-Host "Executables:"
if ($exeFiles) {
    $exeFiles | ForEach-Object { Write-Host "  - $($_.Name)" }
} else {
    Write-Host "  (none found!)" -ForegroundColor Red
}

$wwwroot = Join-Path $publishDir 'wwwroot'
Write-Host ""
if (Test-Path $wwwroot) {
    $wwwrootFileCount = (Get-ChildItem -Path $wwwroot -Recurse -File).Count
    Write-Host "wwwroot: OK ($wwwrootFileCount files)" -ForegroundColor Green
} else {
    Write-Host "wwwroot: MISSING - frontend was not published!" -ForegroundColor Red
}

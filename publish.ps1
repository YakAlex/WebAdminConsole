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

    Code-review fix (2026-08-23): two prior bugs.
    1) Each project used to publish straight into the shared publish/ folder;
       whichever ran second would silently overwrite any shared-filename
       dependency DLL from the first with its own resolved version, with no
       warning either way. Now each project publishes into its own staging
       subfolder first, and the merge step below hard-fails (throw) if a
       shared-path file's content actually differs between the two --
       instead of a blind last-write-wins copy.
    2) The exe/wwwroot sanity checks used to only Write-Host a red warning --
       the script still exited 0 (success) even with a broken publish. They
       now run BEFORE the success banner and throw on failure, so a broken
       publish can never be reported as complete.
#>

$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$publishDir = Join-Path $root 'publish'
$stagingDir = Join-Path $root '.publish_staging'
$rid        = 'win-x64'
$config     = 'Release'

Write-Host "== AdminConsole publish ==" -ForegroundColor Cyan
Write-Host "Output: $publishDir"

foreach ($dir in @($publishDir, $stagingDir)) {
    if (Test-Path $dir) {
        Write-Host "Removing existing $(Split-Path $dir -Leaf)/ ..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $dir
    }
}
New-Item -ItemType Directory -Path $publishDir | Out-Null

$projects = @(
    'AdminConsole.Api\AdminConsole.Api.csproj',
    'AdminConsole.Migration\AdminConsole.Migration.csproj'
)

# ── Publish each project into its own staging subfolder ────────────────────
foreach ($project in $projects) {
    $projectPath       = Join-Path $root $project
    $projectStagingDir = Join-Path $stagingDir ([IO.Path]::GetFileNameWithoutExtension($project))
    Write-Host ""
    Write-Host "-- Publishing $project --" -ForegroundColor Cyan

    dotnet publish $projectPath `
        -c $config `
        -r $rid `
        --self-contained true `
        -o $projectStagingDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project (exit code $LASTEXITCODE)"
    }
}

# ── Merge staged outputs into publish/, failing loudly on a real conflict ──
# (same relative path, different content) instead of letting the second
# project's copy silently win.
Write-Host ""
Write-Host "-- Merging published outputs --" -ForegroundColor Cyan

$conflicts = @()
foreach ($project in $projects) {
    $projectStagingDir = Join-Path $stagingDir ([IO.Path]::GetFileNameWithoutExtension($project))

    Get-ChildItem -Path $projectStagingDir -Recurse -File | ForEach-Object {
        $relativePath = $_.FullName.Substring($projectStagingDir.Length + 1)
        $destPath     = Join-Path $publishDir $relativePath

        if (Test-Path $destPath) {
            $existingHash = (Get-FileHash -Path $destPath -Algorithm SHA256).Hash
            $newHash      = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash
            if ($existingHash -ne $newHash) {
                $conflicts += $relativePath
            }
            return # identical content, or a conflict already recorded -- either way, don't overwrite
        }

        $destDir = Split-Path $destPath -Parent
        if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
        Copy-Item -Path $_.FullName -Destination $destPath -Force
    }
}
Remove-Item -Recurse -Force $stagingDir

if ($conflicts.Count -gt 0) {
    Write-Host ""
    Write-Host "Version conflicts between AdminConsole.Api and AdminConsole.Migration outputs:" -ForegroundColor Red
    $conflicts | Select-Object -Unique | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "Publish aborted: $($conflicts.Count) shared file(s) resolved to different content between the two projects -- their package versions have diverged. Align the versions and re-run."
}

# ── Post-publish verification -- gates success, does not just warn ─────────
Write-Host ""
Write-Host "-- Verifying publish output --" -ForegroundColor Cyan

$issues = @()

$exeFiles = Get-ChildItem -Path $publishDir -Filter '*.exe' -File
if (-not $exeFiles) {
    $issues += "No .exe files found in $publishDir"
}

$wwwroot          = Join-Path $publishDir 'wwwroot'
$wwwrootFileCount = 0
if (-not (Test-Path $wwwroot)) {
    $issues += "wwwroot is missing -- frontend was not published"
} else {
    $wwwrootFileCount = (Get-ChildItem -Path $wwwroot -Recurse -File).Count
    if ($wwwrootFileCount -eq 0) {
        $issues += "wwwroot exists but is empty -- frontend build produced no files"
    }
}

if ($issues.Count -gt 0) {
    Write-Host ""
    Write-Host "Publish verification FAILED:" -ForegroundColor Red
    $issues | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    throw "Publish output is incomplete -- see above. Do not deploy the publish/ folder as-is."
}

Write-Host "Executables:"
$exeFiles | ForEach-Object { Write-Host "  - $($_.Name)" }
Write-Host "wwwroot: OK ($wwwrootFileCount files)" -ForegroundColor Green

Write-Host ""
Write-Host "== Publish complete ==" -ForegroundColor Green
Write-Host "Output directory: $publishDir"

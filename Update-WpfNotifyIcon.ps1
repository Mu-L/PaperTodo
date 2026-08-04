[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$remote = "https://github.com/snownico0722/wpf-notifyicon.git"
$branch = "develop"
$dependencyPath = Join-Path $PSScriptRoot "vendor\wpf-notifyicon"
$gitMarker = Join-Path $dependencyPath ".git"

if (-not (Test-Path -LiteralPath $gitMarker)) {
    git clone --branch $branch --single-branch $remote $dependencyPath
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to clone wpf-notifyicon from $remote."
    }
}
else {
    $changes = @(git -C $dependencyPath status --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to inspect the local wpf-notifyicon checkout."
    }
    if ($changes.Count -gt 0) {
        throw "vendor\wpf-notifyicon has local changes. Commit or remove them before updating."
    }

    git -C $dependencyPath remote set-url origin $remote
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to configure the wpf-notifyicon origin remote."
    }

    git -C $dependencyPath fetch --prune origin $branch
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch the latest wpf-notifyicon $branch branch."
    }

    git -C $dependencyPath checkout --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to check out the latest wpf-notifyicon $branch revision."
    }
}

$revision = (git -C $dependencyPath rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Failed to read the wpf-notifyicon revision."
}

Write-Host "Using wpf-notifyicon $revision from $branch."

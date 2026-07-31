param(
    [string]$PaperTodoRoot
)

$ErrorActionPreference = "Stop"

$sourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($PaperTodoRoot)) {
    $PaperTodoRoot = [System.IO.Path]::GetFullPath((Join-Path $sourceDirectory "..\.."))
} else {
    $PaperTodoRoot = [System.IO.Path]::GetFullPath($PaperTodoRoot)
}

$projectPath = Join-Path $sourceDirectory "PaperTodo.Plugin.CloudGenshin.csproj"
$pluginsRoot = [System.IO.Path]::GetFullPath((Join-Path $PaperTodoRoot "plugins"))
$targetDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $pluginsRoot "sample.cloudgenshin.native"))
$expectedPrefix = $pluginsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $targetDirectory.StartsWith(
        $expectedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Plugin output path escaped the repository plugins directory: $targetDirectory"
}

if (-not (Test-Path -LiteralPath (Join-Path $PaperTodoRoot "PaperTodo.csproj") -PathType Leaf)) {
    throw "PaperTodo repository root not found: $PaperTodoRoot"
}

if (Get-Process -Name "PaperTodo" -ErrorAction SilentlyContinue) {
    throw "Please exit PaperTodo before replacing the native plugin."
}

$stagingDirectory = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    "PaperTodo.Plugin.CloudGenshin-" + [System.Guid]::NewGuid().ToString("N"))
$artifactsDirectory = Join-Path $stagingDirectory "artifacts"
$publishDirectory = Join-Path $stagingDirectory "publish"

try {
    New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null

    & dotnet publish $projectPath `
        -c Release `
        -r win-x64 `
        --self-contained false `
        --artifacts-path $artifactsDirectory `
        -o $publishDirectory `
        /p:DebugType=none `
        /p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "CloudGenshin plugin build failed with exit code $LASTEXITCODE."
    }

    $entryAssembly = Join-Path $publishDirectory "PaperTodo.Plugin.CloudGenshin.dll"
    $dependencyManifest = Join-Path $publishDirectory "PaperTodo.Plugin.CloudGenshin.deps.json"
    foreach ($requiredFile in @($entryAssembly, $dependencyManifest)) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Required plugin output is missing: $requiredFile"
        }
    }

    New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
    Get-ChildItem -LiteralPath $targetDirectory -Force |
        Where-Object { $_.Name -ne ".runtime" } |
        Remove-Item -Recurse -Force

    Copy-Item -LiteralPath (Join-Path $sourceDirectory "plugin.json") -Destination $targetDirectory
    Copy-Item -LiteralPath $entryAssembly -Destination $targetDirectory
    Copy-Item -LiteralPath $dependencyManifest -Destination $targetDirectory

    Write-Host "Installed minimal plugin output to $targetDirectory"
} finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $PSCommandPath
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$solution = Join-Path $repoRoot "lcemcp.slnx"
$project = Join-Path $repoRoot "src\LceMcp\LceMcp.csproj"
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $artifactsRoot "lcemcp"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $repoRoot $OutputDir
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputDir)

if (-not $outputFullPath.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDir must resolve inside '$artifactsRoot'. Refusing to clean '$outputFullPath'."
}

if (-not (Test-Path $solution)) {
    throw "Solution file not found: $solution"
}

if (-not (Test-Path $project)) {
    throw "Project file not found: $project"
}

Write-Host "Repository: $repoRoot"
Write-Host "Runtime:    $RuntimeIdentifier"
Write-Host "Output:     $outputFullPath"

if (-not $SkipTests) {
    Write-Host "Running tests..."
    dotnet test $solution -c $Configuration /p:UseSharedCompilation=false
}

if (Test-Path $outputFullPath) {
    Write-Host "Cleaning output..."
    Remove-Item -LiteralPath $outputFullPath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputFullPath | Out-Null

Write-Host "Publishing self-contained single-file executable..."
dotnet publish $project `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $outputFullPath `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:UseSharedCompilation=false

$exeName = if ($RuntimeIdentifier.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
    "LceMcp.exe"
}
else {
    "LceMcp"
}

$exePath = Join-Path $outputFullPath $exeName
if (-not (Test-Path $exePath)) {
    throw "Expected executable was not produced: $exePath"
}

$hash = Get-FileHash -Algorithm SHA256 -Path $exePath

Write-Host ""
Write-Host "Release artifact ready:"
Write-Host "  $exePath"
Write-Host "  SHA256: $($hash.Hash)"
Write-Host ""
Write-Host "MCP command:"
Write-Host "  $exePath serve"

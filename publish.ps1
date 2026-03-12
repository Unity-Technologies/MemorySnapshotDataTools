# Build and zip MemorySnapshotDataTools for each RID. Run from MemorySnapshotDataTools (project root).
# Produces: artifacts/MemorySnapshotDataTools-<Version>-<RID>.zip

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Project = Join-Path $Root "Cli\MemorySnapshotDataTools.Cli.csproj"
$PublishDir = Join-Path $Root "publish"
$ArtifactsDir = Join-Path $Root "artifacts"
$Rids = @("win-x64", "linux-x64", "osx-x64", "osx-arm64")

# Read version from csproj
$versionNode = Select-String -Path $Project -Pattern '<Version>([^<]+)</Version>' -AllMatches
if (-not $versionNode) { throw "Could not read Version from $Project" }
$Version = $versionNode.Matches.Groups[1].Value

New-Item -ItemType Directory -Force -Path $PublishDir, $ArtifactsDir | Out-Null
Push-Location $Root

try {
    foreach ($rid in $Rids) {
        Write-Host "Publishing $rid..."
        $outDir = Join-Path $PublishDir $rid
        dotnet publish $Project -c Release -r $rid --self-contained true -p:PublishSingleFile=true -o $outDir
        $zipName = "MemorySnapshotDataTools-$Version-$rid.zip"
        $zipPath = Join-Path $ArtifactsDir $zipName
        Write-Host "Zipping $zipName"
        Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath -Force
        Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
    }
    if ((Get-ChildItem $PublishDir -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item -Force $PublishDir -ErrorAction SilentlyContinue
    }
    Write-Host "Done. Artifacts in $ArtifactsDir:"
    Get-ChildItem (Join-Path $ArtifactsDir "*.zip") | Format-Table Name, Length -AutoSize
} finally {
    Pop-Location
}

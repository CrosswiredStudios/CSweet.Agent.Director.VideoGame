[CmdletBinding()]
param(
    [string] $RepositoriesRoot = (Split-Path $PSScriptRoot -Parent | Split-Path -Parent),
    [switch] $VerifyOnly
)
$ErrorActionPreference = 'Stop'
$owner = Split-Path $PSScriptRoot -Parent
$source = Join-Path $owner 'extensions/video-game'
$ownerMetadata = Get-Content -LiteralPath (Join-Path $source 'extension.json') -Raw | ConvertFrom-Json
$names = @('ArtDirector.VideoGame','Artist.VideoGame','AudioDesigner.VideoGame','BuildReleaseEngineer.VideoGame',
    'CreativeDirector.VideoGame','Engineer.VideoGame','GameDesigner','LevelDesigner.VideoGame','NarrativeDesigner.VideoGame',
    'PlaytestResearcher.VideoGame','Producer.VideoGame','QA.VideoGame','TechnicalArtist.VideoGame','TechnicalDirector.VideoGame','UiUxAccessibilityDesigner.VideoGame')
$files = @('VideoGameContracts.cs','VideoGameAgentKit.cs','README.md')
$ownerHashes = @{}
foreach ($file in $files) { $ownerHashes[$file] = (Get-FileHash -LiteralPath (Join-Path $source $file) -Algorithm SHA256).Hash.ToLowerInvariant() }
# Reconcile source changes individually before calling this command. Never copy
# the owner's entire adapter over differing discipline-specific implementations.
foreach ($name in $names) {
    $repository = Join-Path $RepositoriesRoot "CSweet.Agent.$name"
    if (!(Test-Path -LiteralPath (Join-Path $repository 'csweet-plugin.json'))) { throw "Missing consumer repository: $repository" }
    $target = Join-Path $repository 'extensions/video-game'
    $metadataPath = Join-Path $target 'extension.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $hashes = [ordered]@{}
    $adaptations = @()
    foreach ($file in $files) {
        $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $target $file) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hashes[$file] -ne $ownerHashes[$file]) { $adaptations += $file }
        if ($VerifyOnly -and $metadata.files.$file -ne $hashes[$file]) { throw "Local extension provenance differs: $name/$file" }
    }
    if ($VerifyOnly) {
        if ($metadata.version -ne $ownerMetadata.version -or $metadata.extensionId -ne $ownerMetadata.extensionId) { throw "Extension identity/version differs: $name" }
        if (@(Compare-Object @($metadata.localAdaptations) $adaptations).Count -gt 0) { throw "Unrecorded tailored extension: $name" }
    } else {
        [ordered]@{
            extensionId = $ownerMetadata.extensionId
            version = $ownerMetadata.version
            sourceRepository = $ownerMetadata.sourceRepository
            sourcePath = $ownerMetadata.sourcePath
            files = $hashes
            localAdaptations = $adaptations
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $metadataPath -Encoding utf8
    }
}
Write-Host "Verified $($names.Count) individually reconciled extension snapshots."

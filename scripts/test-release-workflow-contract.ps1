Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workflowPath = Join-Path $repoRoot '.github\workflows\release.yml'
$buildScriptPath = Join-Path $repoRoot 'scripts\build-release.ps1'
$packageContractPath = Join-Path $repoRoot 'scripts\test-package-contract.ps1'
foreach ($path in @($workflowPath, $buildScriptPath, $packageContractPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $path" }
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
$buildScript = Get-Content -LiteralPath $buildScriptPath -Raw
$packageContract = Get-Content -LiteralPath $packageContractPath -Raw

function Assert-Pattern {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if ($Text -notmatch $Pattern) { throw $Message }
}

Assert-Pattern $workflow "push:\s*\r?\n\s*tags:\s*\['v\*'\]" 'Publishing must be triggered only by v* tags.'
Assert-Pattern $workflow '\$buildParameters\s*=\s*@\{\s*Version\s*=\s*\$env:' 'PowerShell script parameters must use a hashtable splat.'
Assert-Pattern $workflow '\.\\scripts\\build-release\.ps1\s+@buildParameters' 'Release build must use the named-parameter splat.'
Assert-Pattern $workflow 'git merge-base --is-ancestor.+origin/main' 'Release tags must be reachable from origin/main.'
Assert-Pattern $workflow 'function\s+Assert-ReleaseTag' 'The tag object and peeled commit must be revalidated before publish.'
Assert-Pattern $workflow 'gh api --paginate --slurp' 'Release and draft lookup must fail closed through the API.'
Assert-Pattern $workflow '--signer-workflow' 'Attestation verification must constrain the signer workflow.'
Assert-Pattern $workflow '--source-digest' 'Attestation verification must constrain the source digest.'
Assert-Pattern $workflow '--source-ref' 'Attestation verification must constrain the source ref.'
Assert-Pattern $workflow '\$expected\s*=\s*@\(' 'The exact six-file release set must be enumerated.'
Assert-Pattern $workflow 'Exact release notes are missing' 'Publishing must require exact-version release notes.'
Assert-Pattern $workflow 'gh release create @arguments' 'Release assets must be staged through the GitHub CLI.'
Assert-Pattern $workflow "'--verify-tag',\s*'--draft'" 'Assets must be staged in a draft before immutable publication.'
Assert-Pattern $workflow 'Uploaded draft digest or size differs' 'GitHub draft asset digests and sizes must be compared locally.'
Assert-Pattern $workflow 'gh release verify\s+\$tag' 'The immutable GitHub release attestation must be verified.'
Assert-Pattern $workflow 'gh release verify-asset\s+\$tag' 'Every published release asset must be verified against the release attestation.'
Assert-Pattern $workflow 'gh release delete\s+\$tag' 'A mutable release or draft must be cleaned up after verification failure.'
Assert-Pattern $workflow '\$draftCreated\s+-and\s+-not\s+\$publishAttempted' 'Automatic cleanup must be limited to failures before publication is attempted.'
Assert-Pattern $workflow '-ExpectedSourceCommit\s+\$env:SSW_SOURCE_COMMIT' 'Published package validation must bind the manifest to the workflow commit.'

if ($workflow -match "(?m)^\s*\`$arguments\s*=\s*@\('-Version'") {
    throw 'Array splatting cannot preserve named parameters for a PowerShell script.'
}
if ($workflow -match 'gh release view') {
    throw 'gh release view exit code 1 is ambiguous and must not be used as a not-found check.'
}
if ($workflow -match 'immutable-releases') {
    throw 'The default GITHUB_TOKEN cannot call the repository Administration immutable-settings endpoint.'
}
if ($workflow -match '>>\s*\$env:GITHUB_OUTPUT') {
    throw 'Windows PowerShell 5.1 must write GITHUB_OUTPUT through Out-File UTF-8.'
}
if ([regex]::Matches($workflow, 'Out-File\s+-FilePath\s+\$env:GITHUB_OUTPUT\s+-Encoding\s+utf8\s+-Append').Count -lt 3) {
    throw 'All Windows PowerShell GITHUB_OUTPUT writes must explicitly use UTF-8 append mode.'
}

$lines = $workflow -split "`r?`n"
$insideRunBlock = $false
$runIndent = -1
foreach ($line in $lines) {
    if ($line -match '^(?<indent>\s*)run:\s*(?<tail>.*)$') {
        $runIndent = $Matches.indent.Length
        $insideRunBlock = $Matches.tail -eq '|'
        if ($line -match '\$\{\{') { throw 'GitHub expressions must enter PowerShell through env, not source interpolation.' }
        continue
    }
    if ($insideRunBlock) {
        if ($line.Trim().Length -gt 0) {
            $indent = $line.Length - $line.TrimStart().Length
            if ($indent -le $runIndent) { $insideRunBlock = $false }
        }
        if ($insideRunBlock -and $line -match '\$\{\{') {
            throw 'GitHub expressions must enter PowerShell through env, not source interpolation.'
        }
    }
}

for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
    if ($lines[$lineIndex] -notmatch '^(?<indent>\s*)run:\s*\|\s*$') { continue }
    $runIndent = $Matches.indent.Length
    $body = @()
    for ($bodyIndex = $lineIndex + 1; $bodyIndex -lt $lines.Count; $bodyIndex++) {
        $candidate = $lines[$bodyIndex]
        if ($candidate.Trim().Length -gt 0) {
            $candidateIndent = $candidate.Length - $candidate.TrimStart().Length
            if ($candidateIndent -le $runIndent) { break }
        }
        $body += $candidate
    }
    $nonBlankIndents = @($body | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object {
        $_.Length - $_.TrimStart().Length
    })
    if ($nonBlankIndents.Count -eq 0) { continue }
    $bodyIndent = ($nonBlankIndents | Measure-Object -Minimum).Minimum
    $scriptText = (($body | ForEach-Object {
        if ($_.Length -ge $bodyIndent) { $_.Substring($bodyIndent) } else { '' }
    }) -join "`r`n")
    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors) {
        throw "Embedded release PowerShell failed to parse near workflow line $($lineIndex + 1): $($parseErrors[0].Message)"
    }
}

$usesLines = $lines | Where-Object { $_ -match '^\s*uses:\s*' }
$unpinned = @($usesLines | Where-Object { $_ -notmatch '@[0-9a-f]{40}(?:\s+#.*)?$' })
if ($unpinned.Count -gt 0) { throw "GitHub Actions must be pinned by commit SHA: $($unpinned -join ', ')" }

$verifyIndex = $workflow.IndexOf('Verify release contract and provenance before publishing', [StringComparison]::Ordinal)
$createIndex = $workflow.IndexOf('Stage, verify, and publish immutable release exactly once', [StringComparison]::Ordinal)
if ($verifyIndex -lt 0 -or $createIndex -lt 0 -or $verifyIndex -gt $createIndex) {
    throw 'Contract and provenance verification must finish before release creation.'
}
$draftDigestIndex = $workflow.IndexOf('Uploaded draft digest or size differs', [StringComparison]::Ordinal)
$finalTagCheckIndex = $workflow.IndexOf('# The active v* tag ruleset closes the remaining fetch-to-publish race.', [StringComparison]::Ordinal)
$publishIndex = $workflow.IndexOf('--method PATCH', [StringComparison]::Ordinal)
if ($draftDigestIndex -lt $createIndex -or $finalTagCheckIndex -lt $draftDigestIndex -or
    $publishIndex -lt $finalTagCheckIndex) {
    throw 'Draft digest and final tag verification must finish immediately before publication.'
}

Assert-Pattern $buildScript 'RELEASE_NOTES_\$\{releaseNotesToken\}_KO\.md' 'Build must select only exact-version release notes.'
if ($buildScript -match 'RELEASE_NOTES_0\.[0-9]+\.[0-9]+_POC_KO\.md') {
    throw 'Build must not silently fall back to another version release note.'
}
Assert-Pattern $packageContract '\$releaseNotesName' 'Package contract must require the exact-version release note.'
Assert-Pattern $packageContract '\$rootManifest\.sourceCommit\s+-ne\s+\$ExpectedSourceCommit' 'Package contract must compare the manifest to the expected workflow commit.'

if ($workflow -notmatch 'default:\s*(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)') {
    throw 'Manual build default version is missing or invalid.'
}
$notesToken = $Matches.version.Replace('-', '_').ToUpperInvariant()
$notesPath = Join-Path $repoRoot "docs\RELEASE_NOTES_${notesToken}_KO.md"
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) { throw "Exact release notes are missing: $notesPath" }

Write-Host '[Samsung Switch Watch] GitHub release workflow contract passed.'

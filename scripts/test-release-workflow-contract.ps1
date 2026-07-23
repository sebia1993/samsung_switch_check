Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workflowPath = Join-Path $repoRoot '.github\workflows\release.yml'
$buildScriptPath = Join-Path $repoRoot 'scripts\build-release.ps1'
$packageContractPath = Join-Path $repoRoot 'scripts\test-package-contract.ps1'
$releaseProcessPath = Join-Path $repoRoot 'docs\RELEASE_PROCESS_KO.md'
foreach ($path in @($workflowPath, $buildScriptPath, $packageContractPath, $releaseProcessPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required release file is missing: $path" }
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw
$buildScript = Get-Content -LiteralPath $buildScriptPath -Raw
$packageContract = Get-Content -LiteralPath $packageContractPath -Raw
$releaseProcess = Get-Content -LiteralPath $releaseProcessPath -Raw -Encoding UTF8

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
Assert-Pattern $workflow '\$internalExpected\s*=\s*@\(' 'The exact six-file internal validation set must be enumerated.'
Assert-Pattern $workflow '\$publicAssetNames\s*=\s*@\(' 'The exact two-file public release set must be enumerated.'
Assert-Pattern $workflow '\$arguments\s*=\s*@\(\$tag\)\s*\+\s*\$publicAssets' 'Only the explicit public ZIP allowlist may be passed to GitHub Release creation.'
Assert-Pattern $workflow '\$remoteNames\s+-join\s+''\|''\)\s+-ne\s+\(\$publicAssetNames\s+-join\s+''\|''' 'Remote draft assets must match the public ZIP allowlist.'
Assert-Pattern $workflow 'Exact release notes are missing' 'Publishing must require exact-version release notes.'
Assert-Pattern $workflow 'gh release create @arguments' 'Release assets must be staged through the GitHub CLI.'
Assert-Pattern $workflow '\$draftCreateOutput\s*=\s*@\(gh release create @arguments\)' 'Draft creation output must be captured without retrying creation.'
Assert-Pattern $workflow "'--verify-tag',\s*'--draft'" 'Assets must be staged in a draft before immutable publication.'
Assert-Pattern $workflow 'foreach\s*\(\$draftLookupAttempt\s+in\s+1\.\.12\)' 'Draft discovery must use a bounded eventual-consistency retry.'
Assert-Pattern $workflow 'Created release draft was not discoverable within the bounded lookup window' 'Draft discovery must fail closed when the retry budget is exhausted.'
Assert-Pattern $workflow '\[string\]\$candidate\.html_url\s+-ne\s+\$draftUrl' 'Draft discovery must bind the candidate to the URL returned by creation.'
Assert-Pattern $workflow 'Created release draft identity changed before asset verification' 'The selected draft must be revalidated by numeric release ID.'
Assert-Pattern $workflow 'Uploaded draft digest or size differs' 'GitHub draft asset digests and sizes must be compared locally.'
Assert-Pattern $workflow 'gh release verify\s+\$tag' 'The immutable GitHub release attestation must be verified.'
Assert-Pattern $workflow 'gh release verify-asset\s+\$tag' 'Every published release asset must be verified against the release attestation.'
Assert-Pattern $workflow '--method DELETE\s+`?\s*\r?\n\s*"repos/\$\(\$env:SSW_REPOSITORY\)/releases/\$releaseId"' 'Pre-publication cleanup must delete only the confirmed numeric release ID.'
Assert-Pattern $workflow '\$draftCreated\s+-and\s+-not\s+\$publishAttempted' 'Automatic cleanup must be limited to failures before publication is attempted.'
Assert-Pattern $workflow '-ExpectedSourceCommit\s+\$env:SSW_SOURCE_COMMIT' 'Published package validation must bind the manifest to the workflow commit.'

if ($workflow -match "(?m)^\s*\`$arguments\s*=\s*@\('-Version'") {
    throw 'Array splatting cannot preserve named parameters for a PowerShell script.'
}
if ($workflow -match 'gh release view') {
    throw 'gh release view exit code 1 is ambiguous and must not be used as a not-found check.'
}
if ($workflow -match 'gh release delete') {
    throw 'Release cleanup must use a confirmed numeric release ID, never a tag lookup.'
}
if ($workflow -match 'immutable-releases') {
    throw 'The default GITHUB_TOKEN cannot call the repository Administration immutable-settings endpoint.'
}
if ($workflow -match 'subject-path:\s*artifacts/release/\*') {
    throw 'Build provenance must attest only the two public ZIP assets, never the six-file internal artifact wildcard.'
}
if ($workflow -notmatch '(?s)Upload internal validation artifact.+?path:\s*artifacts/release/\*') {
    throw 'The six-file package contract must remain available as an internal Actions artifact.'
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
$attestIndex = $workflow.IndexOf('Attest release assets', [StringComparison]::Ordinal)
if ($attestIndex -lt 0 -or $attestIndex -gt $verifyIndex) {
    throw 'Public release ZIP attestation must run before provenance verification.'
}
$attestBlock = $workflow.Substring($attestIndex, $verifyIndex - $attestIndex)
$expectedAttestationPaths = @(
    'artifacts/release/SamsungSwitchWatch-Agent-${{ needs.package.outputs.version }}-win-x64.zip'
    'artifacts/release/SamsungSwitchWatch-Viewer-${{ needs.package.outputs.version }}-win-x64.zip'
)
foreach ($path in $expectedAttestationPaths) {
    if ($attestBlock.IndexOf($path, [StringComparison]::Ordinal) -lt 0) {
        throw "Public ZIP is missing from the build provenance allowlist: $path"
    }
}
foreach ($privateValidationName in @('BUILD-MANIFEST.json', 'SBOM.spdx.json', 'SBOM.cdx.json', 'SHA256SUMS.txt')) {
    if ($attestBlock.IndexOf($privateValidationName, [StringComparison]::Ordinal) -ge 0) {
        throw "Internal validation file must not be attested as a public release asset: $privateValidationName"
    }
}
$publicAllowlistBlocks = [regex]::Matches(
    $workflow,
    '(?s)\$publicAssetNames\s*=\s*@\((?<body>.*?)\)\s*\|\s*Sort-Object')
if ($publicAllowlistBlocks.Count -lt 2) {
    throw 'Both provenance verification and publication must define the public ZIP allowlist.'
}
foreach ($block in $publicAllowlistBlocks) {
    $body = $block.Groups['body'].Value
    $publicEntries = @([regex]::Matches($body, 'SamsungSwitchWatch-(?:Agent|Viewer)-[^\r\n"'']+-win-x64\.zip'))
    if ($publicEntries.Count -ne 2 -or $body -match 'BUILD-MANIFEST|SBOM\.|SHA256SUMS') {
        throw 'Each public asset allowlist must contain exactly the Agent and Viewer ZIP files.'
    }
}
if ([regex]::Matches($workflow, 'foreach\s*\(\$name\s+in\s+\$publicAssetNames\)').Count -lt 3) {
    throw 'Attestation, draft digest, and published verify-asset loops must all use the two-file public allowlist.'
}
$draftDigestIndex = $workflow.IndexOf('Uploaded draft digest or size differs', [StringComparison]::Ordinal)
$draftCreateOutputIndex = $workflow.IndexOf('$draftCreateOutput = @(gh release create @arguments)', [StringComparison]::Ordinal)
$draftLookupRetryIndex = $workflow.IndexOf('foreach ($draftLookupAttempt in 1..12)', [StringComparison]::Ordinal)
$draftIdentityIndex = $workflow.IndexOf('Created release draft identity changed before asset verification', [StringComparison]::Ordinal)
$finalTagCheckIndex = $workflow.IndexOf('# The active v* tag ruleset closes the remaining fetch-to-publish race.', [StringComparison]::Ordinal)
$publishIndex = $workflow.IndexOf('--method PATCH', [StringComparison]::Ordinal)
if ($draftCreateOutputIndex -lt $createIndex -or $draftLookupRetryIndex -lt $draftCreateOutputIndex -or
    $draftIdentityIndex -lt $draftLookupRetryIndex -or $draftDigestIndex -lt $draftIdentityIndex -or
    $finalTagCheckIndex -lt $draftDigestIndex -or
    $publishIndex -lt $finalTagCheckIndex) {
    throw 'Draft creation, bounded discovery, ID verification, digest verification, and final tag verification must remain ordered before publication.'
}

$postPublishRetryStart = $workflow.IndexOf('$releaseAttestationVerified = $false', [StringComparison]::Ordinal)
$postPublishRetryEnd = $workflow.IndexOf('if (-not $serverConfirmedImmutable)', $postPublishRetryStart, [StringComparison]::Ordinal)
if ($postPublishRetryStart -lt 0 -or $postPublishRetryEnd -le $postPublishRetryStart) {
    throw 'The bounded post-publish verification block is missing.'
}
$postPublishRetryBlock = $workflow.Substring(
    $postPublishRetryStart,
    $postPublishRetryEnd - $postPublishRetryStart)
$lookupRetryPattern = @'
(?sx)
\$releaseJson\s*=\s*@\(\)\s*
\$releaseLookupExitCode\s*=\s*1\s*
\$oldPreference\s*=\s*\$ErrorActionPreference\s*
\$ErrorActionPreference\s*=\s*'Continue'\s*
try\s*\{.*?
gh\s+api\b.*?
\$releaseLookupExitCode\s*=\s*\$LASTEXITCODE.*?
\}\s*finally\s*\{\s*\$ErrorActionPreference\s*=\s*\$oldPreference\s*\}
'@
Assert-Pattern $postPublishRetryBlock $lookupRetryPattern `
    'Retryable release lookup must locally suppress NativeCommandError, capture LASTEXITCODE, and restore ErrorActionPreference.'
$attestationRetryPattern = @'
(?sx)
\$releaseVerifyExitCode\s*=\s*1\s*
\$oldPreference\s*=\s*\$ErrorActionPreference\s*
\$ErrorActionPreference\s*=\s*'Continue'\s*
try\s*\{\s*
gh\s+release\s+verify\s+\$tag\b.*?
\$releaseVerifyExitCode\s*=\s*\$LASTEXITCODE.*?
gh\s+release\s+verify-asset\s+\$tag\b.*?
\$assetVerifyExitCode\s*=\s*\$LASTEXITCODE.*?
\}\s*finally\s*\{\s*\$ErrorActionPreference\s*=\s*\$oldPreference\s*\}
'@
Assert-Pattern $postPublishRetryBlock $attestationRetryPattern `
    'Post-publish attestation checks must locally suppress NativeCommandError, capture each exit code, and restore ErrorActionPreference.'
if ([regex]::Matches($postPublishRetryBlock, "finally\s*\{\s*\`$ErrorActionPreference\s*=\s*\`$oldPreference\s*\}").Count -lt 2) {
    throw 'Both retryable GitHub lookup and attestation verification must restore ErrorActionPreference.'
}

$originalPreference = $ErrorActionPreference
$nativeAttempts = 0
$nativeRetrySucceeded = $false
foreach ($attempt in 1..2) {
    $nativeAttempts++
    $nativeExitCode = 1
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($attempt -eq 1) {
            & cmd.exe /d /c 'echo transient-attestation-delay 1>&2 & exit /b 1' *> $null
        }
        else {
            & cmd.exe /d /c 'exit /b 0' *> $null
        }
        $nativeExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $oldPreference }

    if ($nativeExitCode -eq 0) {
        $nativeRetrySucceeded = $true
        break
    }
}
if (-not $nativeRetrySucceeded -or $nativeAttempts -ne 2) {
    throw 'Windows PowerShell 5.1 native stderr must remain retryable after a transient first failure.'
}
if ($ErrorActionPreference -ne $originalPreference) {
    throw 'Native retry verification must restore the caller ErrorActionPreference.'
}

Assert-Pattern $buildScript 'RELEASE_NOTES_\$\{releaseNotesToken\}_KO\.md' 'Build must select only exact-version release notes.'
if ($buildScript -match 'RELEASE_NOTES_0\.[0-9]+\.[0-9]+_POC_KO\.md') {
    throw 'Build must not silently fall back to another version release note.'
}
Assert-Pattern $packageContract '\$releaseNotesName' 'Package contract must require the exact-version release note.'
Assert-Pattern $packageContract '\$rootManifest\.sourceCommit\s+-ne\s+\$ExpectedSourceCommit' 'Package contract must compare the manifest to the expected workflow commit.'
Assert-Pattern $releaseProcess 'git tag -a v0\.9\.5-poc' 'Release instructions must create an annotated tag.'
if ($releaseProcess -match 'git tag -s') {
    throw 'Release instructions must not claim a cryptographically signed tag without signature verification.'
}

if ($workflow -notmatch 'default:\s*(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)') {
    throw 'Manual build default version is missing or invalid.'
}
$notesToken = $Matches.version.Replace('-', '_').ToUpperInvariant()
$notesPath = Join-Path $repoRoot "docs\RELEASE_NOTES_${notesToken}_KO.md"
if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) { throw "Exact release notes are missing: $notesPath" }

Write-Host '[Samsung Switch Watch] GitHub release workflow contract passed.'

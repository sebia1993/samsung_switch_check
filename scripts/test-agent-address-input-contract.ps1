Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'common.ps1')

function Assert-AddressTest {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) { throw $Message }
}

function Assert-AddressFailure {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [string]$Code
    )

    $failure = $null
    try { & $Action }
    catch { $failure = $_.Exception.Message }
    Assert-AddressTest -Condition (-not [string]::IsNullOrWhiteSpace($failure)) `
        -Message 'Expected address operation to fail.'
    if ($Code) {
        Assert-AddressTest -Condition ([string]$failure -like "${Code}:*") `
            -Message "Expected $Code but received: $failure"
    }
}

function Import-InstallerFunction {
    param(
        [Parameter(Mandatory = $true)]
        [Management.Automation.Language.ScriptBlockAst]$Ast,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $definition = @($Ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq $Name
    }, $true))
    Assert-AddressTest -Condition ($definition.Count -eq 1) `
        -Message "Expected exactly one installer function named $Name."
    return [scriptblock]::Create($definition[0].Extent.Text)
}

function New-TestManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Commit,
        [Parameter(Mandatory = $true)][string]$ExecutableHash
    )

    return [pscustomobject]@{
        manifestVersion = 1
        packageKind = 'Agent'
        version = $Version
        sourceCommit = $Commit
        executable = [pscustomobject]@{
            name = 'SamsungSwitchWatch.Agent.exe'
            sha256 = $ExecutableHash
        }
    }
}

$installerPath = Join-Path $PSScriptRoot 'install-agent.ps1'
$launcherPath = Join-Path $PSScriptRoot 'Configure-Agent-Allowed-IPs.cmd'
$buildPath = Join-Path $PSScriptRoot 'build-release.ps1'
$packageContractPath = Join-Path $PSScriptRoot 'test-package-contract.ps1'
$tokens = $null
$parseErrors = $null
$installerAst = [Management.Automation.Language.Parser]::ParseFile(
    $installerPath,
    [ref]$tokens,
    [ref]$parseErrors)
Assert-AddressTest -Condition (@($parseErrors).Count -eq 0) `
    -Message 'Agent installer has PowerShell parse errors.'
foreach ($functionName in @(
    'ConvertTo-SswIpv4HostCidrs',
    'Resolve-SswAddressPolicyInput',
    'Test-SswStringSetEqual',
    'Assert-SswReconfigurationPackageMatch',
    'Confirm-SswAddressPolicy')) {
    . (Import-InstallerFunction -Ast $installerAst -Name $functionName)
}

Write-SswStep 'Agent plain IPv4 conversion contract'
$converted = @(ConvertTo-SswIpv4HostCidrs `
    -Address @('192.0.2.20, 192.0.2.10', '192.0.2.20') -Label 'test')
Assert-AddressTest -Condition (
    ($converted -join '|') -ceq '192.0.2.10/32|192.0.2.20/32'
) -Message 'Plain IPv4 addresses were not normalized to unique /32 CIDRs.'

foreach ($invalid in @(
    @('192.0.2.0/24'),
    @('192.0.002.10'),
    @('switch.example.test'),
    @('2001:db8::10'),
    @('192.0.2.10', ''))) {
    Assert-AddressFailure -Action {
        $null = ConvertTo-SswIpv4HostCidrs -Address $invalid -Label 'test'
    }
}
$tooMany = @(1..33 | ForEach-Object { "192.0.2.$_" })
Assert-AddressFailure -Action {
    $null = ConvertTo-SswIpv4HostCidrs -Address $tooMany -Label 'test'
}

Write-SswStep 'Agent address preservation and conflict contract'
$script:sswAddressInputPrompted = $false
$script:addressTestReadHostValue = ''
function Read-Host {
    param([object]$Prompt)
    return $script:addressTestReadHostValue
}

$preserved = @(Resolve-SswAddressPolicyInput `
    -RequestedAddresses @() -RequestedCidrs @() `
    -PreservedCidrs @('10.20.30.0/24') -Prompt 'test' -Label 'test' `
    -PromptEvenWhenPreserved -AllowBlankPreserve)
Assert-AddressTest -Condition (($preserved -join '|') -ceq '10.20.30.0/24') `
    -Message 'Blank reconfiguration input did not preserve the existing policy.'

$advancedCidr = @(Resolve-SswAddressPolicyInput `
    -RequestedAddresses @() -RequestedCidrs @('10.40.0.0/16') `
    -PreservedCidrs @() -Prompt 'test' -Label 'test')
Assert-AddressTest -Condition (($advancedCidr -join '|') -ceq '10.40.0.0/16') `
    -Message 'The existing advanced CIDR parameter contract was not preserved.'

$script:addressTestReadHostValue = '10.20.30.41, 10.20.30.42'
$prompted = @(Resolve-SswAddressPolicyInput `
    -RequestedAddresses @() -RequestedCidrs @() -PreservedCidrs @() `
    -Prompt 'test' -Label 'test')
Assert-AddressTest -Condition (
    ($prompted -join '|') -ceq '10.20.30.41/32|10.20.30.42/32'
) -Message 'Interactive plain IPv4 input was not converted to /32 CIDRs.'
Assert-AddressTest -Condition $script:sswAddressInputPrompted `
    -Message 'Interactive address input was not recorded for final confirmation.'

Assert-AddressFailure -Code 'AGENT_ADDRESS_INPUT_CONFLICT' -Action {
    $null = Resolve-SswAddressPolicyInput `
        -RequestedAddresses @('10.20.30.41') `
        -RequestedCidrs @('10.20.30.0/24') `
        -PreservedCidrs @() -Prompt 'test' -Label 'test'
}
Assert-AddressTest -Condition (
    Test-SswStringSetEqual `
        -Left @('10.0.0.2/32', '10.0.0.1/32') `
        -Right @('10.0.0.1/32', '10.0.0.2/32')
) -Message 'Address policy set equality must ignore ordering.'
$script:addressTestReadHostValue = 'Y'
Confirm-SswAddressPolicy -Required $true
$script:addressTestReadHostValue = 'N'
Assert-AddressFailure -Code 'AGENT_ADDRESS_CONFIGURATION_CANCELLED' -Action {
    Confirm-SswAddressPolicy -Required $true
}

Write-SswStep 'Agent reconfiguration package identity contract'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'SamsungSwitchWatch-address-contract-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    $installedExe = Join-Path $fixtureRoot 'SamsungSwitchWatch.Agent.exe'
    [IO.File]::WriteAllText($installedExe, 'test-agent-binary')
    $exeHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $commit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $sourceManifest = New-TestManifest -Version '1.2.3-test' -Commit $commit `
        -ExecutableHash $exeHash
    $installedManifest = New-TestManifest -Version '1.2.3-test' -Commit $commit `
        -ExecutableHash $exeHash
    $receipt = [pscustomobject]@{
        receiptVersion = 3
        installedVersion = '1.2.3-test'
        sourceCommit = $commit
    }
    Assert-SswReconfigurationPackageMatch -SourceManifest $sourceManifest `
        -InstalledManifest $installedManifest -InstallReceipt $receipt `
        -InstalledExecutablePath $installedExe

    $wrongVersionManifest = New-TestManifest -Version '1.2.2-test' -Commit $commit `
        -ExecutableHash $exeHash
    Assert-AddressFailure -Code 'AGENT_RECONFIGURE_SOURCE_MISMATCH' -Action {
        Assert-SswReconfigurationPackageMatch -SourceManifest $sourceManifest `
            -InstalledManifest $wrongVersionManifest -InstallReceipt $receipt `
            -InstalledExecutablePath $installedExe
    }

    $wrongCommitManifest = New-TestManifest -Version '1.2.3-test' `
        -Commit 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' -ExecutableHash $exeHash
    Assert-AddressFailure -Code 'AGENT_RECONFIGURE_SOURCE_MISMATCH' -Action {
        Assert-SswReconfigurationPackageMatch -SourceManifest $sourceManifest `
            -InstalledManifest $wrongCommitManifest -InstallReceipt $receipt `
            -InstalledExecutablePath $installedExe
    }

    Assert-AddressFailure -Code 'AGENT_RECONFIGURE_SOURCE_MISMATCH' -Action {
        Assert-SswReconfigurationPackageMatch -SourceManifest $sourceManifest `
            -InstalledManifest $installedManifest -InstallReceipt ([pscustomobject]@{}) `
            -InstalledExecutablePath $installedExe
    }

    [IO.File]::AppendAllText($installedExe, '-tampered')
    Assert-AddressFailure -Code 'AGENT_RECONFIGURE_SOURCE_MISMATCH' -Action {
        Assert-SswReconfigurationPackageMatch -SourceManifest $sourceManifest `
            -InstalledManifest $installedManifest -InstallReceipt $receipt `
            -InstalledExecutablePath $installedExe
    }
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Assert-SswChildPath -Parent ([IO.Path]::GetTempPath()) -Child $fixtureRoot
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

Write-SswStep 'Agent allowed IP launcher and package contract'
$installerText = Get-Content -LiteralPath $installerPath -Raw -Encoding UTF8
$launcherText = Get-Content -LiteralPath $launcherPath -Raw -Encoding UTF8
$buildText = Get-Content -LiteralPath $buildPath -Raw -Encoding UTF8
$packageContractText = Get-Content -LiteralPath $packageContractPath -Raw -Encoding UTF8
foreach ($required in @(
    '[string[]]$ClientManagementCidrs',
    '[string[]]$AllowedTargetCidrs',
    '[string[]]$ClientManagementAddresses',
    '[string[]]$AllowedTargetAddresses',
    '[switch]$ReconfigureAddresses',
    '-PromptEvenWhenPreserved:$ReconfigureAddresses',
    '-AllowBlankPreserve:$ReconfigureAddresses',
    'Assert-SswReconfigurationPackageMatch',
    'Confirm-SswAddressPolicy',
    'Copy-Item -LiteralPath $sourceManifestPath -Destination $staging -Force',
    "Join-Path `$staging 'BUILD-MANIFEST.json'",
    '$sourceManifestBytes = [IO.File]::ReadAllBytes($sourceManifestPath)',
    '$sha256.ComputeHash($sourceManifestBytes)',
    '$sourceManifestHash)',
    'AGENT_RECONFIGURE_REQUIRES_EXISTING_INSTALL',
    'AGENT_RECONFIGURE_SOURCE_MISMATCH')) {
    Assert-AddressTest -Condition $installerText.Contains($required) `
        -Message "Agent installer contract is missing: $required"
}
$packageMatchIndex = $installerText.IndexOf(
    'Assert-SswReconfigurationPackageMatch -SourceManifest')
$addressResolutionIndex = $installerText.IndexOf(
    '$clientCidrs = @(Resolve-SswAddressPolicyInput')
$transactionIndex = $installerText.IndexOf(
    '$transactionId = [Guid]::NewGuid().ToString(''N'')')
Assert-AddressTest -Condition (
    $packageMatchIndex -ge 0 -and
    $addressResolutionIndex -gt $packageMatchIndex -and
    $transactionIndex -gt $addressResolutionIndex
) -Message 'Reconfiguration identity and address checks must finish before the shared transaction begins.'
foreach ($required in @(
    'install-agent.ps1',
    '-ReconfigureAddresses',
    "-Verb RunAs",
    'SSW_INSTALLER_PATH',
    'SSW_POWERSHELL_PATH=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe',
    'Start-Process -FilePath $env:SSW_POWERSHELL_PATH')) {
    Assert-AddressTest -Condition $launcherText.Contains($required) `
        -Message "Allowed IP launcher contract is missing: $required"
}
Assert-AddressTest -Condition (-not ($launcherText -match '(?im)^\s*powershell\.exe(?:\s|$)')) `
    -Message 'Allowed IP launcher must not resolve Windows PowerShell through the current directory or PATH.'
Assert-AddressTest -Condition (-not $launcherText.Contains('-ExecutionPolicy Bypass')) `
    -Message 'Allowed IP launcher must respect the Windows PowerShell execution policy.'
Assert-AddressTest -Condition (-not $launcherText.Contains('Unblock-File')) `
    -Message 'Allowed IP launcher must not unblock downloaded files.'
Assert-AddressTest -Condition $launcherText.Contains(
    '& $env:SSW_INSTALLER_PATH -ReconfigureAddresses') `
    -Message 'Allowed IP launcher must invoke the packaged installer in reconfiguration mode.'
Assert-AddressTest -Condition $buildText.Contains("'Configure-Agent-Allowed-IPs.cmd'") `
    -Message 'Release build does not package the allowed IP launcher.'
Assert-AddressTest -Condition $packageContractText.Contains("'Configure-Agent-Allowed-IPs.cmd'") `
    -Message 'Release package contract does not require the allowed IP launcher.'

Write-SswStep 'Agent plain IPv4 input contract passed'

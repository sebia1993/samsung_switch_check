param(
    [string]$InstallDirectory = "$env:ProgramFiles\SamsungSwitchWatch\Agent",
    [string]$DataDirectory = "$env:ProgramData\SamsungSwitchWatch",
    [switch]$RemoveData
)

. (Join-Path $PSScriptRoot 'common.ps1')
Assert-SswAdministrator

$serviceName = Get-SswAgentServiceName
$install = [IO.Path]::GetFullPath($InstallDirectory)
$data = [IO.Path]::GetFullPath($DataDirectory)
Assert-SswProductPath -Path $install -BaseRoot $env:ProgramFiles -ProductRelativeRoot 'SamsungSwitchWatch\Agent'
Assert-SswProductPath -Path $data -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'

$configPath = Join-Path $install 'appsettings.Production.json'
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    try { $configuration = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Installed Agent configuration is invalid: $($_.Exception.Message)" }
    $configuredData = [IO.Path]::GetFullPath([string]$configuration.Agent.DataDirectory)
    Assert-SswProductPath -Path $configuredData -BaseRoot $env:ProgramData -ProductRelativeRoot 'SamsungSwitchWatch'
    if ($PSBoundParameters.ContainsKey('DataDirectory') -and
        -not $data.Equals($configuredData, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'DataDirectory does not match the installed Agent configuration.'
    }
    $data = $configuredData
}
elseif ($RemoveData) {
    throw 'Agent configuration is missing. Refusing to infer a data directory for destructive removal.'
}

$receiptPath = Join-Path $data 'install-receipt.json'
if ($RemoveData) {
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw 'A valid v3 install receipt is required before Agent data can be removed.'
    }
    try { $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { throw "Agent install receipt is invalid: $($_.Exception.Message)" }
    $null = Assert-SswAgentExecutorReceipt -Receipt $receipt -InstallDirectory $install -DataDirectory $data
}

Assert-SswAgentFirewallNameSafety
$transactionId = [Guid]::NewGuid().ToString('N')
$journalPath = Join-Path $env:ProgramData 'SamsungSwitchWatch-Operations\agent-uninstall.json'
Write-SswOperationJournal -Path $journalPath -Operation 'agent-uninstall' -TransactionId $transactionId `
    -Stage 'prepared' -Status 'running'

$errors = @(Invoke-SswBestEffortPlan -Plan @(
    [pscustomobject]@{ Name = 'stop-service'; Action = {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
        }
    } },
    [pscustomobject]@{ Name = 'delete-service'; Action = {
        if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
            & sc.exe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Service delete failed.' }
            Wait-SswServiceDeleted -Name $serviceName -TimeoutSeconds 20
        }
    } },
    [pscustomobject]@{ Name = 'remove-owned-firewall'; Action = {
        Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Http' -AllowMissing
        Remove-SswOwnedAgentFirewallRuleByName -Name 'SamsungSwitchWatchAgent-Https' -AllowMissing
    } },
    [pscustomobject]@{ Name = 'remove-program'; Action = {
        if (Test-Path -LiteralPath $install) {
            Remove-Item -LiteralPath $install -Recurse -Force
        }
    } },
    [pscustomobject]@{ Name = 'remove-data'; Action = {
        if ($RemoveData -and (Test-Path -LiteralPath $data)) {
            Remove-Item -LiteralPath $data -Recurse -Force
        }
        elseif (Test-Path -LiteralPath $data) {
            Write-Host "Agent identity and configuration data were preserved: $data"
        }
    } }
))

$status = if ($errors.Count -eq 0) { 'succeeded' } else { 'failed' }
Write-SswOperationJournal -Path $journalPath -Operation 'agent-uninstall' -TransactionId $transactionId `
    -Stage 'completed' -Status $status -ErrorCodes $errors
if ($errors.Count -gt 0) { throw "Agent uninstall completed with errors: $($errors -join ', ')" }
Write-SswStep 'Agent uninstall completed'

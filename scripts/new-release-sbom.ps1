param(
    [Parameter(Mandatory = $true)][string]$SolutionPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$DotNetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageJson = & $DotNetPath list $SolutionPath package --include-transitive --format json --output-version 1
if ($LASTEXITCODE -ne 0) { throw 'SBOM용 NuGet 의존성 목록을 만들지 못했습니다.' }
$packageReport = ($packageJson -join "`n") | ConvertFrom-Json
$packagesByKey = @{}
foreach ($project in @($packageReport.projects | Where-Object { $_.path -match '[/\\]src[/\\]' })) {
    foreach ($framework in @($project.frameworks)) {
        $topLevelProperty = $framework.PSObject.Properties['topLevelPackages']
        $transitiveProperty = $framework.PSObject.Properties['transitivePackages']
        $frameworkPackages = @()
        if ($topLevelProperty) { $frameworkPackages += @($topLevelProperty.Value) }
        if ($transitiveProperty) { $frameworkPackages += @($transitiveProperty.Value) }
        foreach ($package in $frameworkPackages) {
            if ($null -eq $package -or [string]::IsNullOrWhiteSpace([string]$package.id)) { continue }
            $resolvedVersion = [string]$package.resolvedVersion
            $key = "{0}@{1}" -f ([string]$package.id).ToLowerInvariant(), $resolvedVersion
            if (-not $packagesByKey.ContainsKey($key)) {
                $packagesByKey[$key] = [pscustomobject]@{ Id = [string]$package.id; Version = $resolvedVersion }
            }
        }
    }
}
$packages = @($packagesByKey.Values | Sort-Object Id, Version)
$created = [DateTimeOffset]::UtcNow.ToString('O')
$namespaceCommit = if ([string]::IsNullOrWhiteSpace($SourceCommit)) { 'unknown' } else { $SourceCommit }
$documentNamespace = "https://github.com/sebia1993/samsung_switch_check/sbom/$Version/$namespaceCommit"
$rootSpdxId = 'SPDXRef-Package-SamsungSwitchWatch'

$spdxPackages = New-Object Collections.Generic.List[object]
$spdxPackages.Add([ordered]@{
    name = 'SamsungSwitchWatch'
    SPDXID = $rootSpdxId
    versionInfo = $Version
    downloadLocation = 'NOASSERTION'
    filesAnalyzed = $false
    supplier = 'NOASSERTION'
})
$relationships = New-Object Collections.Generic.List[object]
$relationships.Add([ordered]@{ spdxElementId = 'SPDXRef-DOCUMENT'; relationshipType = 'DESCRIBES'; relatedSpdxElement = $rootSpdxId })

$cycloneComponents = New-Object Collections.Generic.List[object]
$cycloneDependencies = New-Object Collections.Generic.List[string]
foreach ($package in $packages) {
    $safeId = ([regex]::Replace("$($package.Id)-$($package.Version)", '[^A-Za-z0-9.-]', '-'))
    $spdxId = "SPDXRef-NuGet-$safeId"
    $purl = "pkg:nuget/$([Uri]::EscapeDataString($package.Id))@$([Uri]::EscapeDataString($package.Version))"
    $spdxPackages.Add([ordered]@{
        name = $package.Id
        SPDXID = $spdxId
        versionInfo = $package.Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        supplier = 'NOASSERTION'
        externalRefs = @([ordered]@{ referenceCategory = 'PACKAGE-MANAGER'; referenceType = 'purl'; referenceLocator = $purl })
    })
    $relationships.Add([ordered]@{ spdxElementId = $rootSpdxId; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = $spdxId })
    $componentRef = "nuget:$($package.Id)@$($package.Version)"
    $cycloneDependencies.Add($componentRef)
    $cycloneComponents.Add([ordered]@{
        type = 'library'
        name = $package.Id
        version = $package.Version
        'bom-ref' = $componentRef
        purl = $purl
    })
}

$spdx = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "SamsungSwitchWatch-$Version"
    documentNamespace = $documentNamespace
    creationInfo = [ordered]@{ created = $created; creators = @('Tool: SamsungSwitchWatch-release-builder') }
    packages = $spdxPackages.ToArray()
    relationships = $relationships.ToArray()
}
$cyclone = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = $created
        tools = [ordered]@{ components = @([ordered]@{ type = 'application'; name = 'SamsungSwitchWatch release builder' }) }
        component = [ordered]@{ type = 'application'; name = 'SamsungSwitchWatch'; version = $Version; 'bom-ref' = 'app:SamsungSwitchWatch' }
    }
    components = $cycloneComponents.ToArray()
    dependencies = @([ordered]@{ ref = 'app:SamsungSwitchWatch'; dependsOn = $cycloneDependencies.ToArray() })
}

if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SBOM.spdx.json'), ($spdx | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SBOM.cdx.json'), ($cyclone | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))

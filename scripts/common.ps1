Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SswDotNet {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks -match '^10\.')) {
            return $candidate
        }
    }

    throw '.NET 10 SDK를 찾지 못했습니다. https://dotnet.microsoft.com/download/dotnet/10.0 에서 x64 SDK를 설치하세요.'
}

function Assert-SswAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '관리자 권한 PowerShell에서 실행해야 합니다.'
    }
}

function Assert-SswChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "안전 범위를 벗어난 경로입니다: $childFull"
    }
}

function Write-SswStep {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[Samsung Switch Watch] $Message" -ForegroundColor Cyan
}

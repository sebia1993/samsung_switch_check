param(
    [Parameter(Mandatory = $true)][string]$AgentUri,
    [Parameter(Mandatory = $true)][ValidateCount(1, 2)][string[]]$CertificateFingerprint
)

. (Join-Path $PSScriptRoot 'common.ps1')

$uri = $AgentUri.TrimEnd('/')
if (-not $uri.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'AgentUri는 https:// 주소여야 합니다.'
}
$expected = @($CertificateFingerprint | ForEach-Object {
    -join ($_.ToUpperInvariant().ToCharArray() | Where-Object { [Uri]::IsHexDigit($_) })
} | Select-Object -Unique)
if ($expected.Count -lt 1 -or $expected.Count -gt 2 -or @($expected | Where-Object { $_.Length -ne 64 }).Count -gt 0) {
    throw '인증서 SHA-256 지문은 각각 64자리이며 최대 2개까지 허용됩니다.'
}
$code = Read-Host 'Agent PC에서 생성한 일회용 페어링 코드'
if ([string]::IsNullOrWhiteSpace($code)) { throw '페어링 코드가 비어 있습니다.' }

Add-Type -AssemblyName System.Net.Http
if (-not ('SswDualPinnedHttpClientHandler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public sealed class SswDualPinnedHttpClientHandler : HttpClientHandler
{
    private readonly string[] expected;
    public SswDualPinnedHttpClientHandler(string[] expectedFingerprints)
    {
        expected = expectedFingerprints;
        ServerCertificateCustomValidationCallback = Validate;
    }
    private bool Validate(HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
    {
        if (certificate == null) return false;
        using (var sha = SHA256.Create())
        {
            var actual = BitConverter.ToString(sha.ComputeHash(certificate.RawData)).Replace("-", "");
            var accepted = false;
            foreach (var fingerprint in expected)
            {
                var difference = actual.Length ^ fingerprint.Length;
                var count = Math.Min(actual.Length, fingerprint.Length);
                for (var index = 0; index < count; index++) difference |= actual[index] ^ fingerprint[index];
                accepted |= difference == 0;
            }
            return accepted;
        }
    }
}
'@ -ReferencedAssemblies 'System.Net.Http.dll'
}
$handler = New-Object SswDualPinnedHttpClientHandler -ArgumentList (,$expected)
$client = New-Object Net.Http.HttpClient($handler)
try {
    $payload = @{ code = $code.Trim() } | ConvertTo-Json -Compress
    $content = New-Object Net.Http.StringContent($payload, [Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync("$uri/api/v1/pairing/exchange", $content).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { throw "페어링 실패: HTTP $([int]$response.StatusCode)" }
    $result = $body | ConvertFrom-Json
    Write-Host ''
    Write-Host '페어링이 완료되었습니다. 아래 값은 Viewer 연결 설정에 한 번만 입력하세요.' -ForegroundColor Green
    Write-Host "Agent HTTPS 주소: $uri"
    Write-Host "인증서 SHA-256 지문: $($expected -join ', ')"
    Write-Host "페어링 토큰: $($result.token)"
    Write-Warning '토큰 화면을 공유하거나 파일에 저장하지 마세요. Viewer 저장 후 현재 PowerShell 창을 닫으세요.'
}
finally {
    $client.Dispose()
    $handler.Dispose()
    $code = $null
}

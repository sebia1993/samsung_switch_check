param(
    [Parameter(Mandatory = $true)][string]$AgentUri,
    [Parameter(Mandatory = $true)][string]$CertificateFingerprint
)

. (Join-Path $PSScriptRoot 'common.ps1')

$uri = $AgentUri.TrimEnd('/')
if (-not $uri.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'AgentUri는 https:// 주소여야 합니다.'
}
$expected = -join ($CertificateFingerprint.ToUpperInvariant().ToCharArray() | Where-Object { [Uri]::IsHexDigit($_) })
if ($expected.Length -ne 64) { throw '인증서 SHA-256 지문은 64자리여야 합니다.' }
$code = Read-Host 'Agent PC에서 생성한 일회용 페어링 코드'
if ([string]::IsNullOrWhiteSpace($code)) { throw '페어링 코드가 비어 있습니다.' }

Add-Type -AssemblyName System.Net.Http
if (-not ('SswPinnedHttpClientHandler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public sealed class SswPinnedHttpClientHandler : HttpClientHandler
{
    private readonly string expected;
    public SswPinnedHttpClientHandler(string expectedFingerprint)
    {
        expected = expectedFingerprint;
        ServerCertificateCustomValidationCallback = Validate;
    }
    private bool Validate(HttpRequestMessage request, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
    {
        if (certificate == null) return false;
        using (var sha = SHA256.Create())
        {
            var actual = BitConverter.ToString(sha.ComputeHash(certificate.RawData)).Replace("-", "");
            return String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
    }
}
'@ -ReferencedAssemblies 'System.Net.Http.dll'
}
$handler = New-Object SswPinnedHttpClientHandler($expected)
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
    Write-Host "인증서 SHA-256 지문: $expected"
    Write-Host "페어링 토큰: $($result.token)"
    Write-Warning '토큰 화면을 공유하거나 파일에 저장하지 마세요. Viewer 저장 후 현재 PowerShell 창을 닫으세요.'
}
finally {
    $client.Dispose()
    $handler.Dispose()
    $code = $null
}

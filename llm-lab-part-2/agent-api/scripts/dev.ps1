<#
  Yerel geliştirme: agent-api, MCP sunucuları ve A2A servisini başlatır / durdurur.

    .\scripts\dev.ps1            # derle, servisleri ayrı pencerelerde başlat
    .\scripts\dev.ps1 -Hidden    # pencere açma; çıktılar %TEMP%\llmlab-<ad>.log dosyalarına gider
    .\scripts\dev.ps1 -Stop      # hepsini durdur
    .\scripts\dev.ps1 -NoBuild   # derlemeyi atla

  Portlar:
    API                5090   (sohbet, ajan kayıt defteri, diyalog, alt ajanlar)
    MCP araçlar        5101   (saat, hesap)
    MCP belgeler       5102   (belge arama, listeleme)
    MCP sandbox        5103   (Python kodu, Docker'da)
    A2A servisi        5201   (kayıt defterindeki ajanlar /a2a/{ajan} altında)

  Ollama (11434) ve SQL Server önceden çalışıyor olmalı. Sandbox için imaj yerelde olmalı: docker pull python:3.12-slim
  A2A servisi ajan listesini açılışta okur: API (migration + başlangıç ajanları) hazır olunca başlatılır.
#>
param(
    [switch]$Hidden,
    [switch]$Stop,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent   # agent-api klasörü

$services = @(
    @{ Name = 'mcp-tools';     Project = 'src\LlmLab.Mcp.Tools';     Url = 'http://localhost:5101'; After = $null },
    @{ Name = 'mcp-documents'; Project = 'src\LlmLab.Mcp.Documents'; Url = 'http://localhost:5102'; After = $null },
    @{ Name = 'mcp-sandbox';   Project = 'src\LlmLab.Mcp.Sandbox';   Url = 'http://localhost:5103'; After = $null },
    @{ Name = 'api';           Project = 'src\LlmLab.Api';           Url = 'http://localhost:5090'; After = $null },
    @{ Name = 'agent-host';    Project = 'src\LlmLab.AgentHost';     Url = 'http://localhost:5201'; After = 'api' }
)

function Stop-LlmLab {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
        Where-Object { $_.CommandLine -match 'LlmLab\.(Api|AgentHost|Mcp\.Tools|Mcp\.Documents|Mcp\.Sandbox)' -and $_.CommandLine -notmatch 'testhost|vstest|test' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force; "durduruldu: PID $($_.ProcessId)" }
}

function Wait-Ready([hashtable]$s) {
    for ($i = 0; $i -lt 90; $i++) {
        try { Invoke-WebRequest "$($s.Url)/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true }
        catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}

function Start-Service([hashtable]$s) {
    $args = @('run', '--project', (Join-Path $root $s.Project), '--no-build', '--urls', $s.Url)
    if ($Hidden) {
        $log = Join-Path $env:TEMP "llmlab-$($s.Name).log"
        Start-Process dotnet -ArgumentList $args -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    } else {
        Start-Process dotnet -ArgumentList $args
    }
    "$($s.Name) başlatıldı: $($s.Url)"
}

if ($Stop) { Stop-LlmLab; return }

Stop-LlmLab | Out-Null
if (-not $NoBuild) {
    dotnet build (Join-Path $root 'LlmLab.slnx') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Derleme başarısız.' }
}

$env:ASPNETCORE_ENVIRONMENT = 'Development'

# Önce bağımsız olanlar; bağımlı servis (agent-host) bağımlılığı hazır olunca.
foreach ($s in $services | Where-Object { -not $_.After }) { Start-Service $s }
foreach ($s in $services | Where-Object { -not $_.After }) {
    "$($s.Name): $(if (Wait-Ready $s) { 'hazır' } else { 'YANIT VERMİYOR' })"
}
foreach ($s in $services | Where-Object { $_.After }) {
    Start-Service $s
    "$($s.Name): $(if (Wait-Ready $s) { 'hazır' } else { 'YANIT VERMİYOR' })"
}


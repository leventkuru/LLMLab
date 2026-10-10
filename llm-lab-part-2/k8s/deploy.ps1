<#
  llm-lab'ı yerel kind kümesine dağıtır.
    .\k8s\deploy.ps1          imajları derle, kümeye yükle, uygula
    .\k8s\deploy.ps1 -Down    namespace'i (veritabanı diski dahil) siler

  Önkoşul: Docker Desktop açık, `kind create cluster --name llm-lab` yapılmış, .env dolu.
  Not: docker compose yığını AÇIKKEN çalıştırmayın; iki SQL Server aynı anda belleğe sığmaz.
#>
param([switch]$Down)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cluster = 'llm-lab'
$ctx = "kind-$cluster"

if ($Down) {
    kubectl --context $ctx delete namespace llm-lab --ignore-not-found
    return
}

# .env'de lisans kabulü ve parola olmalı (Secret buradan oluşturulur).
$envFile = Join-Path $root '.env'
$vars = @{}
Get-Content $envFile | Where-Object { $_ -match '^\s*[A-Za-z_]+=' } | ForEach-Object {
    $k, $v = $_ -split '=', 2
    $vars[$k.Trim()] = $v.Trim()
}
if ($vars['ACCEPT_EULA'] -ne 'Y') { throw ".env içinde ACCEPT_EULA=Y yok: SQL Server lisansını kabul etmeden devam edilmez." }
if (-not $vars['MSSQL_SA_PASSWORD']) { throw ".env içinde MSSQL_SA_PASSWORD yok." }

Write-Host '1/4 İmajlar derleniyor...'
docker build -t llm-lab/llmapi:dev (Join-Path $root 'LlmApi')
docker build -t llm-lab/web:dev (Join-Path $root 'web')

Write-Host '2/4 İmajlar kind düğümüne yükleniyor...'
kind load docker-image llm-lab/llmapi:dev llm-lab/web:dev --name $cluster

Write-Host '3/4 Namespace ve Secret...'
kubectl --context $ctx create namespace llm-lab --dry-run=client -o yaml | kubectl --context $ctx apply -f -
# --from-env-file: parola komut satırı argümanı olarak görünmez.
kubectl --context $ctx -n llm-lab create secret generic mssql --from-env-file=$envFile --dry-run=client -o yaml |
    kubectl --context $ctx apply -f -

Write-Host '4/4 Manifest uygulanıyor...'
kubectl --context $ctx apply -f (Join-Path $PSScriptRoot 'llm-lab.yaml')
# Aynı etiketle yeniden yüklenen imaj için podları tazele.
kubectl --context $ctx -n llm-lab rollout restart deployment/llmapi deployment/web | Out-Null

kubectl --context $ctx -n llm-lab rollout status statefulset/mssql --timeout=300s
kubectl --context $ctx -n llm-lab rollout status deployment/llmapi --timeout=300s
kubectl --context $ctx -n llm-lab rollout status deployment/web --timeout=120s

Write-Host ''
Write-Host 'Hazır. Arayüze erişmek için (bu pencere açık kalmalı):'
Write-Host '  kubectl --context kind-llm-lab -n llm-lab port-forward svc/web 8081:80'
Write-Host '  sonra http://localhost:8081'

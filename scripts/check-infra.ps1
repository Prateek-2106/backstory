<#
.SYNOPSIS
  Step 2 test: checks every piece of local infrastructure and prints PASS/FAIL.
.EXAMPLE
  ./scripts/check-infra.ps1
  ./scripts/check-infra.ps1 -SkipOllama     # if you have not installed Ollama yet
#>
param(
    [string]$EmbedModel = "nomic-embed-text",
    [string]$ChatModel  = "llama3.1:8b",
    [string]$OllamaUrl  = "http://localhost:11434",
    [switch]$SkipOllama
)

# "Continue": in Windows PowerShell 5.1, "Stop" turns any stderr from docker into an error.
$ErrorActionPreference = "Continue"
$results = New-Object System.Collections.Generic.List[object]

function Check([string]$Name, [scriptblock]$Test) {
    try {
        $detail = & $Test
        $results.Add([pscustomobject]@{ Check = $Name; Result = "PASS"; Detail = "$detail" })
    } catch {
        $results.Add([pscustomobject]@{ Check = $Name; Result = "FAIL"; Detail = $_.Exception.Message })
    }
}

function Health([string]$Container) {
    $status = docker inspect -f "{{.State.Health.Status}}" $Container 2>$null
    if ($LASTEXITCODE -ne 0) { throw "container $Container not found (is 'docker compose up -d' running?)" }
    if ($status -ne "healthy") { throw "$Container is '$status'" }
    "healthy"
}

# Must match src/Backstory.Core/Contracts/Topics.cs (+ DLQs)
$expectedTopics = @(
    "news.articles.v1", "sources.documents.v1", "sources.indexed.v1",
    "context.requests.v1", "context.generated.v1",
    "news.articles.v1.dlq", "sources.documents.v1.dlq", "context.requests.v1.dlq"
)

Check "Docker running" { docker info --format "{{.ServerVersion}}" 2>$null; if ($LASTEXITCODE -ne 0) { throw "Docker Desktop is not running" } }

Check "Kafka healthy"    { Health "backstory-kafka" }
Check "Postgres healthy" { Health "backstory-postgres" }
Check "Redis healthy"    { Health "backstory-redis" }
Check "Qdrant healthy"   { Health "backstory-qdrant" }

Check "Topics created (kafka-init)" {
    $exit = docker inspect -f "{{.State.ExitCode}}" backstory-kafka-init 2>$null
    if ($exit -ne "0") { throw "kafka-init exit code '$exit'; run: docker logs backstory-kafka-init" }
    $actual = docker exec backstory-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --list
    $missing = $expectedTopics | Where-Object { $actual -notcontains $_ }
    if ($missing) { throw "missing: $($missing -join ', ')" }
    "$($expectedTopics.Count) topics"
}

Check "Kafka UI (localhost:8081)" {
    $r = Invoke-WebRequest -ErrorAction Stop -UseBasicParsing -TimeoutSec 5 "http://localhost:8081"
    "HTTP $($r.StatusCode)"
}

Check "Postgres accepts queries" {
    $out = docker exec backstory-postgres psql -U backstory -d backstory -tAc "select version()"
    if ($LASTEXITCODE -ne 0) { throw "psql failed" }
    ($out -split ",")[0]
}

Check "Redis answers PING" {
    $out = docker exec backstory-redis redis-cli ping
    if ($out -ne "PONG") { throw "got '$out'" }
    $out
}

Check "Qdrant REST (localhost:6333)" {
    $info = Invoke-RestMethod -ErrorAction Stop -TimeoutSec 5 "http://localhost:6333/"
    "version $($info.version)"
}

if (-not $SkipOllama) {
    Check "Ollama reachable" {
        $tags = Invoke-RestMethod -ErrorAction Stop -TimeoutSec 5 "$OllamaUrl/api/tags"
        "$($tags.models.Count) models installed"
    }
    Check "Model: $EmbedModel" {
        $names = (Invoke-RestMethod -ErrorAction Stop -TimeoutSec 5 "$OllamaUrl/api/tags").models.name
        if (-not ($names | Where-Object { $_ -like "$EmbedModel*" })) { throw "not pulled; run: ollama pull $EmbedModel" }
        "present"
    }
    Check "Model: $ChatModel" {
        $names = (Invoke-RestMethod -ErrorAction Stop -TimeoutSec 5 "$OllamaUrl/api/tags").models.name
        if (-not ($names | Where-Object { $_ -like "$ChatModel*" })) { throw "not pulled; run: ollama pull $ChatModel" }
        "present"
    }
    Check "Embedding works (768 dims)" {
        $body = @{ model = $EmbedModel; input = @("Background for a news headline") } | ConvertTo-Json
        $r = Invoke-RestMethod -ErrorAction Stop -Method Post -TimeoutSec 60 -ContentType "application/json" -Body $body "$OllamaUrl/api/embed"
        $dims = $r.embeddings[0].Count
        if ($dims -ne 768) { throw "expected 768 dimensions, got $dims" }
        "$dims dims"
    }
}

$results | Format-Table -AutoSize
$failed = @($results | Where-Object Result -eq "FAIL").Count
if ($failed -gt 0) {
    Write-Host "$failed check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "All $($results.Count) checks passed. Step 2 done." -ForegroundColor Green

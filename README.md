# Backstory

Cited background for every headline. A RAG pipeline pulls context only from allowlisted,
trustworthy sources and drops any claim it cannot trace to a retrieved passage.

C# (.NET 10) · Kafka · Qdrant · Postgres · Redis · Kubernetes

## Layout

```
Backstory.sln
Directory.Build.props      shared compiler settings (nullable, warnings as errors, net10.0)
Directory.Packages.props   central NuGet versions
global.json                pins the .NET SDK band
src/
  Backstory.Core/            domain, contracts, messaging logic, RAG pipeline (no third-party deps)
  Backstory.Infrastructure/  Kafka adapter (Postgres, Redis, Qdrant later)
  Backstory.Ingestion.Worker/ polls feeds, trust-checks every link, publishes to Kafka
tools/
  Backstory.KafkaDemo/       step 3 demo: publish, consume, retry, dead-letter
tests/
  Backstory.Core.Tests/            unit tests (no infrastructure needed)
  Backstory.Infrastructure.Tests/  integration tests (need Kafka running)
.github/workflows/ci.yml   restore -> build -> test on every push and PR
```

Services (Gateway, Ingestion, Indexer, Context) and deployment files are added step by step.

## Prerequisites

- .NET 10 SDK (`dotnet --version` should print 10.0.x)
- Docker Desktop (needed from step 2)

## Local infrastructure

| Service | URL / port | Purpose |
|---|---|---|
| Kafka | `localhost:9092` | Event backbone (8 topics created by `kafka-init`) |
| Kafka UI | http://localhost:8081 | Browse topics and messages |
| Postgres | `localhost:5432` (backstory / backstory-dev) | Articles and briefs |
| Redis | `localhost:6379` | Brief cache |
| Qdrant | http://localhost:6333/dashboard | Vector store |
| Ollama | http://localhost:11434 | Local LLM + embeddings (installed natively) |

```powershell
winget install Ollama.Ollama
ollama pull nomic-embed-text
ollama pull llama3.1:8b
docker compose up -d
./scripts/check-infra.ps1
```

Stop with `docker compose down`; add `-v` to wipe all data.

## Build and test

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release                                  # everything (Kafka must be running)
dotnet test -c Release --filter "Category!=Integration" # unit tests only
```

## Ingestion Worker (step 4)

```bash
dotnet run --project src/Backstory.Ingestion.Worker
```

- http://localhost:5101/ingestion/status shows each feed's last poll: published, already seen, untrusted, rejected, with reasons
- Feeds are configured in `src/Backstory.Ingestion.Worker/appsettings.json`; the allowlist is `config/trusted-sources.json`

## Kafka demo (step 3)

```bash
dotnet run --project tools/Backstory.KafkaDemo                 # 3 events, consumed back
dotnet run --project tools/Backstory.KafkaDemo -- --fail        # retries, then dead-letter topic
dotnet run --project tools/Backstory.KafkaDemo -- --poison      # unparseable message, dead-lettered at once
```

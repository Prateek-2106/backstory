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
  Backstory.Core/          domain, contracts, RAG pipeline (no third-party deps)
tests/
  Backstory.Core.Tests/    xUnit tests
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
dotnet test -c Release
```

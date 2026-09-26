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

## Build and test

```bash
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

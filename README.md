# EY Risk Screening Backend

Backend desarrollado en .NET 10 para una plataforma de búsqueda y cruce de entidades con listas internacionales de riesgo.

## Requisitos

- .NET SDK 10.0.300
- Docker Desktop
- Docker Compose

## Estructura

```text
src/
├── EyRiskScreening.Api
├── EyRiskScreening.Application
├── EyRiskScreening.Domain
└── EyRiskScreening.Infrastructure

tests/
├── EyRiskScreening.UnitTests
└── EyRiskScreening.IntegrationTests
```

## Configuración local

Copia el archivo de ejemplo:

```powershell
Copy-Item .env.example .env
```

Configura una contraseña local para SQL Server dentro de `.env`:

```env
MSSQL_SA_PASSWORD=your_password
```

Luego inicia SQL Server:

```powershell
docker compose up -d
```

Para verificar el estado del contenedor:

```powershell
docker compose ps
```

Para detenerlo:

```powershell
docker compose down
```

## Ejecutar el proyecto

```powershell
dotnet restore EyRiskScreening.slnx --locked-mode
dotnet build EyRiskScreening.slnx
dotnet run --project src/EyRiskScreening.Api
```

## Ejecutar las pruebas

```powershell
dotnet test EyRiskScreening.slnx
```

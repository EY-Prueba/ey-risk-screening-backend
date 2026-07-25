# EY Risk Screening

Aplicación para consultar entidades en listas internacionales de riesgo mediante OFAC, World Bank y Offshore Leaks.

La solución permite autenticarse, ejecutar búsquedas en una o varias fuentes y consultar el historial de resultados.

## Aplicación desplegada

- Frontend:
- API: 
- Swagger: 


## Tecnologías principales

- .NET 10
- ASP.NET Core
- SQL Server
- Entity Framework Core
- Playwright y Chromium
- Swagger/OpenAPI
- Docker Compose

## Ejecución local

### Requisitos

- .NET SDK 10.0.300
- Docker Desktop
- PowerShell 7

Para instalar PowerShell 7 como herramienta global:

```powershell
dotnet tool install --global PowerShell
```

### 1. Configurar SQL Server

Crea el archivo local de variables:

```powershell
Copy-Item .env.example .env
```

Configura `MSSQL_SA_PASSWORD` dentro de `.env` y levanta SQL Server:

```powershell
docker compose up -d
docker compose ps
```

Espera hasta que el contenedor aparezca como `healthy`.

### 2. Configurar secretos locales

Configura la conexión a SQL Server:

```powershell
dotnet user-secrets set `
  "ConnectionStrings:DefaultConnection" `
  "Server=localhost,1433;Database=EyRiskScreening;User Id=sa;Password=<PASSWORD_LOCAL>;Encrypt=True;TrustServerCertificate=True" `
  --project .\src\EyRiskScreening.Api
```

Genera una clave JWT local:

```powershell
$jwtKey = [Convert]::ToBase64String(
  [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))

dotnet user-secrets set "Jwt:SigningKeyBase64" $jwtKey `
  --project .\src\EyRiskScreening.Api

Remove-Variable jwtKey
```

### 3. Restaurar y aplicar migraciones

```powershell
dotnet tool restore
dotnet restore EyRiskScreening.slnx --locked-mode

dotnet ef database update `
  --project .\src\EyRiskScreening.Infrastructure `
  --startup-project .\src\EyRiskScreening.Api
```

### 4. Instalar Chromium

```powershell
dotnet build `
  .\src\EyRiskScreening.Infrastructure\EyRiskScreening.Infrastructure.csproj `
  --configuration Release `
  --no-restore `
  -p:CopyLocalLockFileAssemblies=true

pwsh `
  .\src\EyRiskScreening.Infrastructure\bin\Release\net10.0\playwright.ps1 `
  install chromium
```

### 5. Crear el primer administrador

El bootstrap solo crea el primer usuario de una base vacía:

```powershell
dotnet user-secrets set "BootstrapAdmin:Enabled" "true" `
  --project .\src\EyRiskScreening.Api

dotnet user-secrets set "BootstrapAdmin:UserName" "<ADMIN_LOCAL>" `
  --project .\src\EyRiskScreening.Api

dotnet user-secrets set "BootstrapAdmin:Email" "<EMAIL_LOCAL>" `
  --project .\src\EyRiskScreening.Api

dotnet user-secrets set "BootstrapAdmin:Password" "<PASSWORD_LOCAL>" `
  --project .\src\EyRiskScreening.Api
```

Después de crear el usuario, desactiva el bootstrap y elimina esos secretos.

### 6. Ejecutar la API

```powershell
dotnet run `
  --project .\src\EyRiskScreening.Api `
  --launch-profile https
```

Swagger estará disponible en:

```text
http://localhost:5105
```

Desde Swagger:

1. Ejecuta `POST /api/v1/auth/login`.
2. Copia el valor `accessToken`.
3. Pulsa **Authorize**.
4. Pega únicamente el token.
5. Ejecuta los endpoints protegidos.

## Pruebas

```powershell
dotnet restore EyRiskScreening.slnx --locked-mode
dotnet build EyRiskScreening.slnx --configuration Release --no-restore
dotnet test EyRiskScreening.slnx --configuration Release --no-build --no-restore
```
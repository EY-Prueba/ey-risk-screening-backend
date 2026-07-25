# syntax=docker/dockerfile:1.7

ARG DOTNET_SDK_VERSION=10.0.300
ARG DOTNET_RUNTIME_VERSION=10.0.8

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/EyRiskScreening.Domain/EyRiskScreening.Domain.csproj src/EyRiskScreening.Domain/packages.lock.json src/EyRiskScreening.Domain/
COPY src/EyRiskScreening.Application/EyRiskScreening.Application.csproj src/EyRiskScreening.Application/packages.lock.json src/EyRiskScreening.Application/
COPY src/EyRiskScreening.Infrastructure/EyRiskScreening.Infrastructure.csproj src/EyRiskScreening.Infrastructure/packages.lock.json src/EyRiskScreening.Infrastructure/
COPY src/EyRiskScreening.Api/EyRiskScreening.Api.csproj src/EyRiskScreening.Api/packages.lock.json src/EyRiskScreening.Api/
RUN dotnet restore src/EyRiskScreening.Api/EyRiskScreening.Api.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/EyRiskScreening.Api/EyRiskScreening.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION}-noble AS runtime
ARG PLAYWRIGHT_VERSION=1.61.0

LABEL org.opencontainers.image.title="EY Risk Screening API" \
      org.opencontainers.image.version="1.0" \
      org.opencontainers.image.description="ASP.NET Core API with Playwright Chromium ${PLAYWRIGHT_VERSION}"

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

WORKDIR /app
COPY --from=build /app/publish ./

RUN apt-get update \
    && apt-get install --yes --no-install-recommends tini=0.19.0-1 \
    && dotnet EyRiskScreening.Api.dll --playwright-install install --with-deps chromium \
    && chmod a+rx /app/.playwright/node/linux-x64/node \
    && chmod -R a+rX /ms-playwright \
    && rm -rf /var/lib/apt/lists/*

RUN set -eux; \
    test -s /usr/bin/tini; \
    test -x /usr/bin/tini; \
    /usr/bin/tini --version; \
    command -v dotnet; \
    dotnet --info >/dev/null; \
    test -s /app/EyRiskScreening.Api.dll; \
    chromium_path="$(find "${PLAYWRIGHT_BROWSERS_PATH}" -type f -path '*/chrome-linux64/chrome' -print -quit)"; \
    test -n "${chromium_path}"; \
    test -s "${chromium_path}"; \
    test -x "${chromium_path}"; \
    echo "Validated Chromium executable: ${chromium_path}"

EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["dotnet", "EyRiskScreening.Api.dll"]

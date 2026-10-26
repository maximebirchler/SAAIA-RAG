# ================================
# SAAIA.Backend - production image
# ================================

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0.420 AS build
WORKDIR /src
ARG SAAIA_CODE_REVISION
RUN test -n "$SAAIA_CODE_REVISION" && test "$SAAIA_CODE_REVISION" != "unknown"

# Copy solution + csproj first (better layer caching)
COPY RAG.sln ./
COPY global.json Directory.Build.props ./
COPY contracts/SAAIA.Contracts/SAAIA.Contracts.csproj contracts/SAAIA.Contracts/
COPY backend/SAAIA.Backend/SAAIA.Backend.csproj backend/SAAIA.Backend/

RUN dotnet restore backend/SAAIA.Backend/SAAIA.Backend.csproj

# Copy remaining sources
COPY contracts/ contracts/
COPY backend/ backend/

# Publish
RUN dotnet publish backend/SAAIA.Backend/SAAIA.Backend.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Optional OCR runtime used by ingestion for scanned/low-text PDFs when enabled.
# The backend OCR policy is document-driven and not limited to client UI languages.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ocrmypdf \
        unpaper \
        tesseract-ocr-all \
    && rm -rf /var/lib/apt/lists/*

ARG SAAIA_CODE_REVISION
ENV SAAIA_CODE_REVISION=${SAAIA_CODE_REVISION}
LABEL com.saaia.backend.source-revision="${SAAIA_CODE_REVISION}"

# Kestrel
ENV ASPNETCORE_URLS=http://0.0.0.0:5122
EXPOSE 5122

# Copy app
COPY --from=build /app/publish ./

# Runtime folders (mounted in docker-compose.prod.yml)
RUN mkdir -p /app/documents /app/data

ENTRYPOINT ["dotnet", "SAAIA.Backend.dll"]

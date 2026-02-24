# ================================
# SAAIA.Backend - production image
# ================================

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution + csproj first (better layer caching)
COPY RAG.sln ./
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

# Kestrel
ENV ASPNETCORE_URLS=http://0.0.0.0:5122
EXPOSE 5122

# Copy app
COPY --from=build /app/publish ./

# Runtime folders (mounted in docker-compose.prod.yml)
RUN mkdir -p /app/documents /app/data

ENTRYPOINT ["dotnet", "SAAIA.Backend.dll"]

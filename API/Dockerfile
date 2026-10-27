# Stage 1: Build Client
FROM node:22-alpine AS client-build
WORKDIR /src/Client
COPY Client/package*.json ./
RUN npm ci --ignore-scripts
COPY Client/ .
RUN npm run build:production

# Stage 2: Build API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY API/App.API.csproj API/
COPY Core/App.Core.csproj Core/
COPY Data/App.Data.csproj Data/
COPY Infrastructure/App.Infrastructure.csproj Infrastructure/

RUN dotnet restore API/App.API.csproj

COPY . .
WORKDIR /src/API
RUN dotnet publish -c Release -o /app/publish

# Stage 3: Final Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 libheif1 libde265-0 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=api-build /app/publish .
# Copy client build to wwwroot
COPY --from=client-build /src/Client/dist/App.Client/browser ./wwwroot
RUN mkdir -p /data/uploads

EXPOSE 8080
ENTRYPOINT ["dotnet", "App.API.dll"]

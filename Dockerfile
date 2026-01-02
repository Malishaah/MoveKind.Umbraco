# ==== build ====
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MoveKind.sln ./
COPY MoveKind.Umbraco/*.csproj MoveKind.Umbraco/
RUN dotnet restore MoveKind.sln

COPY . .
RUN dotnet publish MoveKind.Umbraco/MoveKind.Umbraco.csproj -c Release -o /app/publish

# ==== runtime ====
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Installera openssl (behövs för cert)
RUN apt-get update && apt-get install -y --no-install-recommends openssl && rm -rf /var/lib/apt/lists/*

# Kestrel: lyssna på HTTP + HTTPS
ENV ASPNETCORE_URLS="http://+:8080;https://+:8443"
ENV ASPNETCORE_Kestrel__Endpoints__Http__Url="http://+:8080"
ENV ASPNETCORE_Kestrel__Endpoints__Https__Url="https://+:8443"
ENV ASPNETCORE_Kestrel__Certificates__Default__Path="/https/aspnetapp.pfx"
ENV ASPNETCORE_Kestrel__Certificates__Default__Password="pass123!"

# Valfria envs för scriptet
ENV CERT_DIR="/https" \
    CERT_NAME="aspnetapp" \
    CERT_PASSWORD="pass123!" \
    CERT_DAYS="365" \
    CERT_CN="localhost"

COPY --from=build /app/publish ./
# Kopiera script och gör körbart
COPY MoveKind.Umbraco/generate-cert.sh /app/generate-cert.sh
RUN chmod +x /app/generate-cert.sh

EXPOSE 8080 8443
ENTRYPOINT ["/app/generate-cert.sh"]

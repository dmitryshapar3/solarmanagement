FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/DeyeSolar.Web/DeyeSolar.Web.csproj --locked-mode \
    && dotnet publish src/DeyeSolar.Web/DeyeSolar.Web.csproj -c Release -o /app/publish --no-self-contained --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data/keys/integrations /data/integration-packages && chown -R app:app /data
USER app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=false \
    Auth__DataProtectionKeysPath=/data/keys \
    Integrations__KeyRingPath=/data/keys/integrations \
    IntegrationRuntime__PackageDirectory=/data/integration-packages \
    Operations__DatabaseMode=validate
HEALTHCHECK --interval=30s --timeout=8s --start-period=20s --retries=3 CMD ["dotnet", "DeyeSolar.Web.dll", "--health-check"]
ENTRYPOINT ["dotnet", "DeyeSolar.Web.dll"]

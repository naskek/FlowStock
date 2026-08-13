ARG FLOWSTOCK_DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0
ARG FLOWSTOCK_DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:8.0

FROM ${FLOWSTOCK_DOTNET_SDK_IMAGE} AS build
ARG FLOWSTOCK_SOURCE_COMMIT=INVALID_SOURCE_COMMIT
WORKDIR /src
COPY . .
RUN test "${#FLOWSTOCK_SOURCE_COMMIT}" -eq 40 \
    && case "$FLOWSTOCK_SOURCE_COMMIT" in *[!0-9a-f]*) exit 1 ;; esac \
    && dotnet publish apps/windows/FlowStock.Server/FlowStock.Server.csproj -c Release -o /out/flowstock /p:UseAppHost=false /p:SourceRevisionId="$FLOWSTOCK_SOURCE_COMMIT"
RUN dotnet publish apps/windows/FlowStock.DiscoveryRelay/FlowStock.DiscoveryRelay.csproj -c Release -o /out/discovery-relay /p:UseAppHost=false

FROM ${FLOWSTOCK_DOTNET_ASPNET_IMAGE} AS flowstock-runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/flowstock ./
COPY apps/android/tsd /app/tsd
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","FlowStock.Server.dll"]

FROM ${FLOWSTOCK_DOTNET_ASPNET_IMAGE} AS discovery-relay-runtime
WORKDIR /app
RUN groupadd --system flowstock \
    && useradd --system --gid flowstock --home-dir /home/flowstock --create-home flowstock
COPY --from=build /out/discovery-relay ./
USER flowstock
ENTRYPOINT ["dotnet","FlowStock.DiscoveryRelay.dll"]

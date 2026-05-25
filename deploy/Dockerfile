ARG FLOWSTOCK_DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0
ARG FLOWSTOCK_DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:8.0

FROM ${FLOWSTOCK_DOTNET_SDK_IMAGE} AS build
WORKDIR /src
COPY . .
RUN dotnet publish apps/windows/FlowStock.Server/FlowStock.Server.csproj -c Release -o /out /p:UseAppHost=false

FROM ${FLOWSTOCK_DOTNET_ASPNET_IMAGE} AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libreoffice-writer \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out/ ./
COPY apps/android/tsd /app/tsd
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","FlowStock.Server.dll"]

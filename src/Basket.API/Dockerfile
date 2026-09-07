FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props nuget.config ./
COPY src/Basket.API/Basket.API.csproj src/Basket.API/
COPY src/EventBus/EventBus.csproj src/EventBus/
COPY src/EventBusServiceBus/EventBusServiceBus.csproj src/EventBusServiceBus/
COPY src/IntegrationEventLogEF/IntegrationEventLogEF.csproj src/IntegrationEventLogEF/
COPY src/eShop.ServiceDefaults/eShop.ServiceDefaults.csproj src/eShop.ServiceDefaults/
RUN dotnet restore src/Basket.API/Basket.API.csproj

COPY src/Basket.API/ src/Basket.API/
COPY src/EventBus/ src/EventBus/
COPY src/EventBusServiceBus/ src/EventBusServiceBus/
COPY src/IntegrationEventLogEF/ src/IntegrationEventLogEF/
COPY src/eShop.ServiceDefaults/ src/eShop.ServiceDefaults/
COPY src/Shared/ src/Shared/

RUN dotnet publish src/Basket.API/Basket.API.csproj \
    --configuration Release \
    --os linux --arch x64 \
    --self-contained false \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Basket.API.dll"]

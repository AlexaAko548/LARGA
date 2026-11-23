# ManagerWeb on Cloud Run. deploy-cloudrun.ps1 publishes the app first and puts this file next to
# the published output, so the image just runs it - no SDK or MAUI workload needed in the build.
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY . .
# Cloud Run sends traffic to port 8080.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "LARGA.ManagerWeb.dll"]

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MontarLooks.Web/MontarLooks.Web.csproj src/MontarLooks.Web/
RUN dotnet restore src/MontarLooks.Web/MontarLooks.Web.csproj
COPY src/ src/
RUN dotnet publish src/MontarLooks.Web/MontarLooks.Web.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    TZ=America/Sao_Paulo \
    Storage__UploadsPath=/app/data/uploads
COPY --from=build /app/publish .
# Uploads folder owned by the non-root "app" user that ships with the image.
# Source files on the host may be 600; make published files readable by the "app" user.
RUN chmod -R a+rX /app && mkdir -p /app/data/uploads && chown -R app:app /app/data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "MontarLooks.Web.dll"]

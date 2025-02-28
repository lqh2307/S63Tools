FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build

WORKDIR /S63Tools/S63Tools

ADD . .

RUN dotnet publish -c Release -o /out


FROM mcr.microsoft.com/dotnet/runtime:6.0 AS runtime

WORKDIR /app

COPY --from=build /out .

ENTRYPOINT ["dotnet", "S63Tools.dll"]

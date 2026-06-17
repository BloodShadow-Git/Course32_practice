FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["AdminFrontend/AdminFrontend.csproj", "AdminFrontend/"]
RUN dotnet restore "AdminFrontend/AdminFrontend.csproj"
COPY . .
WORKDIR "/src/AdminFrontend"
RUN dotnet publish -c Release -o /app/publish

FROM nginx:alpine
COPY AdminFrontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
EXPOSE 80

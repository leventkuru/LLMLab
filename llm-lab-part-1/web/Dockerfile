# Aşama 1: Vite ile derle
FROM node:24-alpine AS build
WORKDIR /src
# Önce yalnızca bağımlılık dosyaları: kaynak değişince npm ci katmanı önbellekten gelir.
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

# Aşama 2: yalnızca statik dosyalar + nginx
FROM nginx:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
EXPOSE 80

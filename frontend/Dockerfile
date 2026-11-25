FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# A production bundle always uses the same-origin API, regardless of local demo files.
RUN VITE_API_URL=/api VITE_DEMO_MODE=false npm run build

FROM nginx:stable-alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx-main.conf /etc/nginx/nginx.conf
COPY nginx.conf /etc/nginx/conf.d/default.conf
USER 101:101
EXPOSE 8443
ENTRYPOINT ["nginx", "-g", "daemon off;"]

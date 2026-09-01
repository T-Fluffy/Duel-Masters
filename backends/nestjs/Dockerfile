# syntax=docker/dockerfile:1

# Duel Masters NestJS (Socket.io) backend.
# Self-contained; build context MUST be the nestjs folder:
#   docker build -f backends/nestjs/Dockerfile backends/nestjs
#
# NOTE: requires the NestJS project files (package.json, tsconfig.json, src/),
# which land with the official port later.

FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY tsconfig*.json ./
COPY src/ ./src/
RUN npm run build

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production

COPY --from=build /app/package*.json ./
COPY --from=build /app/dist ./dist
RUN npm ci --omit=dev && npm cache clean --force

USER node
EXPOSE 3000
CMD ["node", "dist/main.js"]
# syntax=docker/dockerfile:1

# Duel Masters Spring Boot (STOMP) backend.
# Self-contained; build context MUST be the spring-boot folder:
#   docker build -f backends/spring-boot/Dockerfile backends/spring-boot
#
# NOTE: requires the Maven project (pom.xml, src/), which lands with the official
# port later.

FROM maven:3.9-eclipse-temurin-21 AS build
WORKDIR /source

COPY pom.xml .
RUN mvn dependency:go-offline

COPY src ./src
RUN mvn clean package -DskipTests

FROM eclipse-temurin:21-jre AS runtime
WORKDIR /app
COPY --from=build /source/target/*.jar app.jar
EXPOSE 8080
ENTRYPOINT ["java", "-jar", "app.jar"]
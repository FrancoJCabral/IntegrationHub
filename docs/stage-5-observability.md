# Etapa 5 — Observabilidad

API y Worker usan OpenTelemetry 1.18.0 (Extensions.Hosting y Exporter.Console); API agrega Instrumentation.AspNetCore. Ambos usan Exporter.Prometheus.AspNetCore 1.18.0-beta.1, todavía beta. Los recursos identifican los servicios como IntegrationHub.Api e IntegrationHub.Worker.

Las trazas de creación HTTP, publicación RabbitMQ, consumo y ejecución del conector comparten TraceId mediante traceparent/tracestate W3C. Cada intento tiene un span; retry y duplicado generan eventos. Se exportan a consola (Observability:ConsoleTraces=false permite desactivarlas). No hay backend de almacenamiento de trazas; Grafana muestra métricas.

## Métricas

Prometheus scrapea cada 5 segundos API :5180/metrics y Worker :9464/metrics mediante host.docker.internal. Worker usa un host ASP.NET Core únicamente para /metrics, conservando su BackgroundService.

| Contador Prometheus | Significado |
| --- | --- |
| integrationhub_jobs_published_total | Publicaciones exitosas; RabbitMQ espera confirmación |
| integrationhub_jobs_processed_total | Ejecución exitosa y marca completed guardada |
| integrationhub_jobs_duplicated_total | Reserva rechazada; sin volver a ejecutar connector |
| integrationhub_connector_executions_total | Intentos de connector, incluyendo retries |
| integrationhub_connector_failures_total | Intentos fallidos, excluyendo cancelación externa |
| integrationhub_connector_retries_total | Retries programados por Polly |

No se usan JobId ni payload como etiquetas de métricas. Los contadores se reinician con el proceso y aparecen después de la primera medición. El dashboard muestra cero para series aún ausentes; comprobar los targets de Prometheus para distinguir esto de un servicio caído.

## Infraestructura y ejecución

Compose conserva RabbitMQ/Redis y agrega prom/prometheus:v3.13.1 y grafana/grafana:12.4.10. Configuración en observability/: scraping, datasource Prometheus preconfigurado y un dashboard con seis totales. Grafana permite lectura anónima local, sin cuenta admin inicial. Los puertos de los contenedores se publican en loopback. Los datos usan tmpfs y se pierden al detenerlos.

Usar .env local y cargar credenciales RabbitMQ en cada terminal según [Etapa 4](stage-4-docker-redis.md). No sobrescribir un .env existente.

```powershell
docker compose up -d
# Terminal API, después de cargar credenciales:
$env:Messaging__Provider = 'RabbitMq'
dotnet run --project backend/IntegrationHub.Api --no-launch-profile --urls http://0.0.0.0:5180
# Otra terminal, después de cargar credenciales:
$env:Worker__Enabled = 'true'
$env:Idempotency__Provider = 'Redis'
dotnet run --project workers/IntegrationHub.Worker --no-launch-profile --urls http://0.0.0.0:9464
```

El binding 0.0.0.0 permite scraping desde Docker Desktop; usar sólo en la red local de desarrollo. Prometheus: http://localhost:9090/targets. Dashboard: http://localhost:3000/d/integrationhub/integrationhub. Sin Docker, Worker escucha por defecto en localhost:9464. Detener API/Worker con Ctrl+C y ejecutar docker compose down; no se borran imágenes ni recursos globales.

## Prueba realizada

El 2026-09-14 los cuatro contenedores estuvieron healthy y ambos targets de Prometheus up. Tres POST devolvieron 202. Se inspeccionó un mensaje RabbitMQ con traceparent antes de iniciar Worker; la consola mostró el mismo TraceId en API, consumo y conector. Redis guardó completed. Republicar el mismo payload y headers mediante Management generó un duplicado sin otra ejecución.

Prometheus y el dashboard renderizado en Grafana mostraron: 3 publicados, 3 procesados, 1 duplicado, 3 ejecuciones, 0 fallos y 0 retries. Los conectores simulados de esta prueba fueron exitosos; retry/fallo se verifican en el test de instrumentación con un fallo transitorio seguido de éxito. No se agregaron endpoints debug ni screenshots.

Suite: 124 tests (27 Domain, 41 API, 56 Worker). Se agregó un test de métricas/correlación/retry/duplicado y se amplió el test existente del publisher para comprobar propagación W3C. Esta etapa no cambia las garantías de idempotencia ni el estado Pending del repositorio de API descritos en la Etapa 4.

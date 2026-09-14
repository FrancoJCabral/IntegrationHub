# Etapa 4 — Docker, Redis e idempotencia

Docker Compose levanta sólo infraestructura local: RabbitMQ 4.3.5 con Management UI y Redis 8.2.9. API y Worker siguen en .NET 8 fuera de Docker. Imágenes oficiales: rabbitmq:4.3.5-management y redis:8.2.9-alpine. Healthchecks: rabbitmq-diagnostics check_running y redis-cli ping. Puertos publicados únicamente en 127.0.0.1: 5672, 15672 y 6379.

## Ejecutar localmente

Desde la raíz, con Docker Desktop en Linux containers:

```powershell
Copy-Item .env.example .env
docker compose config --quiet
docker compose up -d
docker compose ps
```

.env está ignorado. Los valores de .env.example son exclusivamente DEMO, no credenciales productivas. Management está en http://localhost:15672. Redis no usa autenticación en este entorno local; no exponerlo a redes externas.

.NET no carga .env automáticamente. En cada una de las dos terminales donde se ejecutarán API y Worker, cargar las credenciales locales sin imprimirlas:

```powershell
foreach ($line in Get-Content .env) {
    if ($line -match '^RABBITMQ_USER=(.*)$') { $env:RabbitMq__UserName = $Matches[1] }
    if ($line -match '^RABBITMQ_PASSWORD=(.*)$') { $env:RabbitMq__Password = $Matches[1] }
}
```

Terminal API:

```powershell
$env:Messaging__Provider = 'RabbitMq'
dotnet run --project backend/IntegrationHub.Api --launch-profile https
```

Terminal Worker:

```powershell
$env:Worker__Enabled = 'true'
$env:Idempotency__Provider = 'Redis'
dotnet run --project workers/IntegrationHub.Worker
```

Redis:Configuration usa localhost:6379; puede reemplazarse mediante Redis__Configuration. Los defaults versionados siguen siendo Messaging:Provider=InMemory, Worker:Enabled=false e Idempotency:Provider=InMemory. Detener API y Worker con Ctrl+C y ejecutar docker compose down. No se eliminan imágenes ni recursos globales. Los datos de ambos contenedores usan tmpfs: esta demo pierde colas y marcas al detenerlos, deliberadamente sin persistencia compleja.

## Idempotencia

IIdempotencyStore expone TryAcquireAsync, CompleteAsync y ReleaseAsync. IdempotentConnectorExecutor envuelve la pipeline Polly existente: reserva → ejecuta → completa. InMemory usa exclusión mutua y reloj inyectable; Redis usa StackExchange.Redis 3.2.0 con ConnectionMultiplexer Singleton, creado de forma diferida al primer uso y dispuesto por DI. InMemory no registra conexiones Redis.

La clave integrationhub:processed:{JobId} guarda processing:{token propietario} durante la reserva. La adquisición usa SET NX EX dentro de un script atómico. Completar y liberar comparan el token mediante Lua, evitando que un propietario anterior borre o complete la reserva de otro. Tras éxito se guarda completed con TTL Idempotency:RetentionHours=24. La reserva temporal usa Idempotency:LeaseSeconds=120; no se almacena payload.

- Primera entrega: reserva, Polly + conector, completed, ACK.
- JobId ya reservado/completado: log de duplicado, sin ejecutar conector, camino de ACK.
- Fallo o cancelación: no completar; liberar sólo si conserva propiedad. El mismo JobId puede intentarse posteriormente. Continúa el NACK sin requeue existente para fallos; esto no agrega retries de RabbitMQ.

El presupuesto cooperativo de ejecución comienza antes de adquirir la reserva y cancela al 80% del lease, dejando margen para completar. No hay renovación de locks. Los conectores deben respetar CancellationToken. Pausas del proceso, fallos de red, expiración del lease o un efecto externo completado antes de registrar completed aún pueden permitir repetición: no es exactly-once. Un duplicado concurrente se confirma mientras el primer propietario trabaja; si éste falla, ese duplicado confirmado no se recupera automáticamente. La retención vencida o Redis reiniciado permite procesar otra vez. Son límites deliberados del portfolio, sin outbox ni DB.

## Validación realizada

El 2026-09-14 se verificaron ambos contenedores healthy, Management UI y el binding integrationhub.jobs → integration.job.submitted → integrationhub.worker. Un POST real devolvió 202/Location/Pending. Se comprobó JSON persistente en la cola antes de iniciar Worker. Worker ejecutó Crm y Redis guardó completed con TTL cercano a 24 horas. Se republicó exactamente el mismo payload mediante la API de Management: se registró Duplicate detected, sin segunda ejecución. Resultado del broker: dos entregas, dos ACK, cero ready y cero unacked. Después se detuvieron los procesos y se ejecutó docker compose down.

Para repetir la duplicación sin endpoints debug, antes de iniciar Worker usar Get messages de Management con requeue para copiar el payload; después de procesarlo publicarlo de nuevo en el exchange con la misma routing key y JobId. El GET HTTP sigue Pending por el repositorio de API no compartido.

Se agregaron cinco tests: primera entrega/duplicado, fallo y nuevo intento, concurrencia, propiedad/TTL e InMemory sin Redis. dotnet restore, dotnet build y dotnet test: 123 aprobados (27 Domain, 41 API, 55 Worker), 0 warnings y 0 errores. La suite no depende de Docker ni Redis reales.

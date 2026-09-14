# Etapa 2 — RabbitMQ e Integration Worker

## Arquitectura

La API valida el request, crea y guarda un IntegrationJob Pending, construye un IntegrationJobSubmittedMessage, publica y devuelve 202 con Location. El contrato HTTP de Etapa 1 se conserva. GET /api/health sigue siendo independiente del broker.

```text
API → IntegrationJobService → repository InMemory (Pending)
                           → IIntegrationJobPublisher
                             ├─ InMemory: publicación local sin consumidor
                             └─ RabbitMQ → IntegrationHub.Worker → SimulatedConnectorExecutor
```

Application ahora referencia Contracts por el caso concreto de publicación. Api implementa ambos publishers. Worker es un ejecutable .NET 8 separado que sólo referencia Contracts: no referencia Api ni Domain. Worker.Tests referencia Worker y Contracts. No se agregó persistencia compartida.

## Contrato y topología

IntegrationJobSubmittedMessage es un record con JobId (Guid), Connector, Operation, ExternalId y SubmittedAtUtc (UTC). System.Text.Json produce JSON UTF-8; los nombres de propiedades del mensaje usan PascalCase en ambos procesos. No incluye estado, entidad, diagnósticos ni headers HTTP.

RabbitMQ.Client 7.2.2 es el cliente oficial compatible con net8.0. API y Worker tienen configuración RabbitMq propia con los mismos nombres por defecto:

| Clave | Valor |
| --- | --- |
| HostName | localhost |
| Port | 5672 |
| VirtualHost | / |
| Exchange | integrationhub.jobs |
| Queue | integrationhub.worker |
| RoutingKey | integration.job.submitted |

El exchange es direct y durable. La cola es durable, no exclusiva y sin auto-delete. El binding conecta la cola al exchange mediante RoutingKey. Tanto publisher como Worker declaran esta misma topología, permitiendo publicar antes de iniciar el Worker.

El publisher Singleton crea la conexión de forma diferida y la reutiliza. Un SemaphoreSlim serializa acceso al canal y publicación. Usa ContentType application/json, mensajes persistentes, mandatory y publisher confirms con tracking: PublishAsync espera confirmación y propaga fallos, incluidos mensajes no enrutables. Al fallar libera canal/conexión; una request posterior puede abrir otros, sin reintentar la publicación fallida. DI dispone los recursos al detener la API.

## Configuración y ejecución

Por defecto Messaging:Provider=InMemory (también en Development) y Worker:Enabled=false. InMemory almacena mensajes en una ConcurrentQueue Singleton, sólo en el proceso API; pierde contenido al reiniciar y no comunica con el Worker. No crea ni registra una fábrica de conexiones RabbitMQ.

```sh
dotnet run --project backend/IntegrationHub.Api --launch-profile https
dotnet run --project workers/IntegrationHub.Worker -- --Worker:Enabled=false
```

El Worker deshabilitado inicia el host, registra un mensaje breve y permanece disponible hasta detenerlo con Ctrl+C, sin abrir conexiones. No tiene endpoint HTTP.

Para una futura ejecución con broker disponible, configurar separadamente ambos procesos mediante variables de entorno:

- API: Messaging__Provider=RabbitMq.
- Worker: Worker__Enabled=true.
- Ambos: RabbitMq__HostName, RabbitMq__Port, RabbitMq__VirtualHost, RabbitMq__Exchange, RabbitMq__Queue y RabbitMq__RoutingKey si difieren de los defaults.
- Ambos: RabbitMq__UserName y RabbitMq__Password obligatorios, provistos externamente. No hay credenciales por defecto en el código o appsettings; también pueden usarse User Secrets en Development (inicializar el identificador del proyecto API antes de usarlos).

Un provider desconocido se rechaza. La configuración RabbitMq se valida sólo al seleccionarlo en API o habilitar el Worker. No se instaló ni arrancó RabbitMQ, Docker o Testcontainers en esta etapa. La validación end-to-end con broker queda pendiente de Docker Compose.

## Consumo y fallos

El consumidor asíncrono usa autoAck=false, prefetch=10 y concurrencia de dispatch=1. Procesa el body dentro del callback, respetando la vida útil del buffer de RabbitMQ.Client. Valida JSON, JobId, conector, campos obligatorios/límites y timestamp UTC antes de ejecutar.

SimulatedConnectorExecutor admite Crm, Erp y Payments, realiza un delay determinista de 10 ms y respeta CancellationToken. No cambia el mensaje ni hace HTTP real.

- Éxito: ACK individual sólo después de completar la ejecución.
- JSON/estructura inválidos o excepción de ejecución: NACK individual con requeue=false; el consumidor sigue activo. Al no existir DLQ, esos mensajes se descartan.
- Apagado durante trabajo: no se confirma el mensaje; cerrar el canal permite su devolución al broker. Esto es semántica de cierre de RabbitMQ, no una política de retry.
- Fallo de infraestructura/canal o cancelación del consumidor por el broker: el servicio falla y el host se detiene; no hay bucle de reconexión. AutomaticRecoveryEnabled=false.

Los logs normales contienen mensajes genéricos y, cuando corresponde, JobId/Connector. No incluyen payload completo, ExternalId ni credenciales. No hay retries, retry queues o DLQ: se incorporarán con una política explícita en otra etapa.

## Limitaciones deliberadas

1. API y Worker tienen memoria separada. El Worker no puede actualizar el IntegrationJob almacenado en la API: GET continúa devolviendo Pending incluso si el consumidor terminó. No hay callbacks a la API, archivos compartidos ni estado estático entre procesos. Se resolverá con persistencia compartida.
2. Guardar y publicar no es atómico. Si falla la publicación, el job queda Pending y el POST devuelve 500 ProblemDetails genérico; no retorna un 202 engañoso. Todavía no existe transactional outbox; queda como mejora posterior junto con persistencia durable.
3. Una confirmación perdida o cancelación puede dejar incierto si el broker recibió un mensaje. No hay idempotencia ni garantía exactly-once. Reenviar requests puede generar otros jobs.
4. InMemory no representa entrega durable ni procesamiento cross-process. La ejecución real RabbitMQ requiere broker y credenciales configurados externamente.

## Validación

Desde la raíz: dotnet restore, dotnet build y dotnet test. La suite conserva Etapa 1 y agrega validación de publicación única/valores, fallos dejando Pending, publicación RabbitMQ con conexiones simuladas, confirms, topología y liberación de recursos; además simulador, mensajes inválidos, ACK/NACK, cancelación y Worker deshabilitado. Moq se usa únicamente en proyectos de tests contra interfaces públicas del cliente; no se prueban sus internals ni se abre una conexión de red.

Referencias: [cliente oficial](https://www.nuget.org/packages/RabbitMQ.Client/7.2.2), [guía .NET](https://www.rabbitmq.com/client-libraries/dotnet-api-guide), [publisher confirms](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet).

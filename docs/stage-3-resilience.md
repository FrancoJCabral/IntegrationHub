# Etapa 3 — Resiliencia de conectores

## Alcance y diseño

El Worker usa Polly.Core 8.7.0 (API moderna ResiliencePipeline). API, Contracts y Domain mantienen sus contratos y comportamiento. La resiliencia protege el intento de integración externa; no reintenta publicaciones, deliveries ni conexiones RabbitMQ.

IExternalConnector representa un sistema concreto mediante Name y ExecuteAsync(message, CancellationToken). Se registran tres instancias de SimulatedExternalConnector: Crm, Erp y Payments. Comparten implementación porque actualmente realizan el mismo delay determinista de 10 ms y siempre tienen éxito con entradas válidas. No hay llamadas HTTP, fallos aleatorios ni latencias aleatorias en los simuladores. Los fallos controlados existen sólo en tests.

IConnectorExecutor sigue siendo la entrada utilizada por el consumidor. ResilientConnectorExecutor resuelve por nombre exacto y conserva una pipeline por conector en su instancia Singleton. Un nombre desconocido produce PermanentConnectorException antes de ejecutar otra integración.

## Orden y clasificación

```text
Circuit Breaker (resultado final de la operación lógica)
  → Retry (hasta MaxRetryAttempts reintentos)
    → Timeout (por intento)
      → IExternalConnector
```

El breaker externo recibe un único resultado después de terminar todos los intentos. Un fallo temporal seguido por éxito es una operación exitosa para el circuito; cuatro intentos fallidos de una operación cuentan como un único resultado fallido.

- TransientConnectorException: fallo temporal que puede recuperarse; se reintenta.
- TimeoutRejectedException de Polly: timeout por intento; se considera transitorio.
- PermanentConnectorException: datos/operación inválidos o conector desconocido; no se reintenta.
- ArgumentException, errores inesperados y otras excepciones no clasificadas: se propagan sin retry.
- OperationCanceledException por cancelación del llamador/shutdown: se propaga sin retry; no se transforma en fallo temporal.

El breaker cuenta como fallos de disponibilidad únicamente errores transitorios y timeouts finales. Los errores permanentes no abren el circuito. Un circuito abierto rechaza la ejecución mediante BrokenCircuitException sin invocar el conector.

## Configuración

Defaults en workers/IntegrationHub.Worker/appsettings.json:

| Opción Resilience | Default |
| --- | --- |
| TimeoutSeconds | 5 |
| MaxRetryAttempts | 3 |
| BaseDelayMilliseconds | 200 |
| CircuitBreaker:FailureRatio | 0.5 |
| CircuitBreaker:MinimumThroughput | 4 |
| CircuitBreaker:SamplingDurationSeconds | 30 |
| CircuitBreaker:BreakDurationSeconds | 15 |

MaxRetryAttempts=3 significa un intento inicial más hasta tres reintentos (cuatro ejecuciones). Con 0 se omite la estrategia Retry porque AddRetry de Polly requiere al menos un reintento. El timeout interior tiene su propio presupuesto para cada intento: no limita el tiempo total de la operación ni incluye las esperas entre retries. Es cooperativo: el conector debe respetar el token que recibe de la pipeline.

Retry usa BackoffType.Exponential y UseJitter=true. Polly aplica su jitter decorrelacionado al backoff exponencial: los delays no son necesariamente crecientes ni una secuencia fija de 200/400/800 ms. La variación está en la espera de resiliencia, no en el resultado del simulador. Los tests usan delay base cero cuando verifican número de intentos, y reloj falso para timeout y recuperación del circuito, sin sleeps largos.

Cada conector conserva su propio estado: abrir CRM no afecta ERP o Payments. Tras BreakDuration, la siguiente llamada puede probar HalfOpen; si funciona, el circuito vuelve a Closed. Las pipelines no se reconstruyen por mensaje. Su estado es local al proceso Worker; reiniciarlo reinicia los circuitos.

IValidateOptions y ValidateOnStart rechazan configuración inválida incluso con Worker deshabilitado: timeout y duraciones del circuito entre 1 y 86400 segundos, retry no negativo, base delay entre 0 y 86400000 ms, ratio finito en (0,1] y throughput mínimo 2. Estos límites mantienen las opciones dentro de valores admitidos por Polly. La configuración se toma al iniciar; no hay recarga dinámica de pipelines.

## Logs y consumidor

El executor registra inicio y final exitoso de cada operación lógica. OnRetry registra JobId, Connector, número de retry (desde 1), delay y tipo de error, sin mensaje de excepción ni stack trace. OnOpened, OnHalfOpened y OnClosed registran únicamente conector, estado y duración pertinente. El procesador registra una sola falla final después de resiliencia y devuelve el resultado al consumer. No se registra ExternalId, payload o credenciales.

La semántica RabbitMQ se conserva:

- Éxito, incluso tras retries: ACK individual.
- Agotamiento transient/timeout, fallo permanente, circuito abierto o mensaje inválido: NACK sin requeue.
- Shutdown durante una ejecución: ningún ACK incorrecto; el mensaje queda sin confirmar y cerrar el canal permite que RabbitMQ lo devuelva a la cola.

No hay DLQ ni retry queues: un NACK sin requeue descarta el mensaje en la topología actual. Un mensaje rechazado por circuito abierto también se descarta. Es una limitación deliberada hasta diseñar la siguiente política de entrega.

## Validación y límites

La suite conserva la cobertura de Etapas 1 y 2, adaptando las pruebas del simulador a IExternalConnector. Agrega éxito sin retries, recuperación tras dos fallos, agotamiento exacto, permanent/validation/unexpected sin retries, timeout por intento con FakeTimeProvider, cancelación, apertura rápida, HalfOpen/Closed, aislamiento entre conectores, validación de startup y ACK/NACK con el executor resiliente real y canal RabbitMQ simulado.

Ejecutar desde la raíz:

```sh
dotnet restore
dotnet build
dotnet test
dotnet run --project workers/IntegrationHub.Worker -- --Worker:Enabled=false
```

Worker:Enabled=false sigue siendo el default: inicia y se detiene sin conexiones RabbitMQ ni ejecución de conectores. Messaging:Provider=InMemory sigue siendo el default de API. No se levantó un broker; la validación RabbitMQ end-to-end permanece pendiente.

GET del job sigue mostrando Pending porque el repositorio de API no se comparte con Worker. Se conserva la ventana no atómica save + publish de Etapa 2. No hay DB, outbox, idempotencia, Redis, métricas ni OpenTelemetry. No existe garantía exactly-once: futuros conectores reales con efectos secundarios requerirán diseñar idempotencia antes de aplicar retries indiscriminadamente. La próxima evolución podrá ser Redis/idempotencia o DLQ según el roadmap; no se implementa aquí.

Referencias: [Polly.Core 8.7.0](https://www.nuget.org/packages/Polly.Core/8.7.0), [retry](https://www.pollydocs.org/strategies/retry.html), [timeout](https://www.pollydocs.org/strategies/timeout.html), [circuit breaker](https://www.pollydocs.org/strategies/circuit-breaker.html).

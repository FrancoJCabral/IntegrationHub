# Etapa 1 — Core de integraciones y API base

IntegrationHub es un portfolio de integración entre sistemas. Esta etapa establece el dominio y una API .NET 8; las capacidades distribuidas se incorporarán cuando exista procesamiento real.

## Arquitectura

- `backend/IntegrationHub.Domain`: entidad y reglas, sin dependencias de otros proyectos.
- `backend/IntegrationHub.Application`: servicio y abstracción de repositorio; depende de Domain.
- `backend/IntegrationHub.Contracts`: contratos HTTP independientes, con DataAnnotations.
- `backend/IntegrationHub.Api`: controladores, composición y almacenamiento en memoria; depende de Application, Contracts y Domain. La referencia a Domain es necesaria para implementar el repositorio y mapear las entidades a respuestas.
- `tests/IntegrationHub.Domain.Tests`: xUnit, referencia a Domain.
- `tests/IntegrationHub.Api.Tests`: xUnit y WebApplicationFactory, referencia a Api; cada caso tiene su propio host y repositorio.

## IntegrationJob

Cada job tiene un Guid generado, conector simulado (`Crm`, `Erp`, `Payments`), operación y referencia externa obligatorias. El dominio aplica trim y límites de 100 y 200 caracteres respectivamente. La API valida esos límites sobre el texto recibido antes del trim. No hay setters públicos para modificar el estado.

El estado inicial es `Pending`. Las únicas transiciones son `Pending → Processing → Succeeded` y `Pending → Processing → Failed`. Ambos resultados son terminales. Las fechas se generan en UTC: creación, inicio, finalización y última actualización; las tres últimas comienzan en null.

## HTTP

| Método | Ruta | Resultado |
| --- | --- | --- |
| GET | `/api/health` | 200; no consulta servicios externos |
| POST | `/api/integrations/jobs` | 202, cuerpo del job Pending y Location para consultarlo |
| GET | `/api/integrations/jobs/{id}` | 200 con el job o 404 si no existe |

Los contratos exponen connector y status como strings; se mapean explícitamente desde los enums del dominio. Connector acepta nombres válidos sin distinguir mayúsculas y rechaza números. No se devuelve la entidad directamente. La validación devuelve 400 y los errores inesperados devuelven 500 ProblemDetails sin diagnóstico interno. Swagger sólo está disponible en Development.

El servicio crea, guarda y consulta; no procesa. `InMemoryIntegrationJobRepository` usa ConcurrentDictionary y lifetime Singleton: comparte los jobs entre requests del mismo proceso y pierde todo al reiniciar. El 202 indica aceptación local, no procesamiento ni entrega durable. Todavía no hay worker porque esta etapa sólo define el ingreso y consulta de trabajos. La próxima etapa será RabbitMQ + Integration Worker.

## Ejecutar y verificar

Requiere SDK 8.0.424 (global.json) y Visual Studio 2022 compatible con .NET 8. Desde la raíz:

```sh
dotnet restore
dotnet build
dotnet test
dotnet run --project backend/IntegrationHub.Api --launch-profile https
```

La API escucha en https://localhost:7180 y Swagger en `/swagger`. El perfil no abre el navegador automáticamente. Los ejemplos están en `backend/IntegrationHub.Api/IntegrationHub.Api.http`; copiar el ID o seguir Location después del POST. Para HTTPS local, confiar en el certificado de desarrollo de .NET si es necesario.

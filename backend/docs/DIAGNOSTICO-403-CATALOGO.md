# Diagnóstico de respuestas 403 del catálogo

El cliente de detalles emite el evento estructurado `40301` (`CatalogForbiddenOutcome`) una vez al resolver cada respuesta 403 inicial. No altera la negociación CSRF, la caché, el lote, las prioridades ni el presupuesto de solicitudes.

| Resultado | Significado |
|---|---|
| Recuperado | El reintento existente recibió una respuesta HTTP satisfactoria. No garantiza por sí solo que el contenido sea válido o que el guardado posterior termine. |
| Definitivo | El 403 no proporcionó un token utilizable, o el único reintento volvió a recibir 403. |
| ReintentoFallido | El reintento terminó en otro estado HTTP o lanzó una excepción, incluido un tiempo de espera agotado. |
| Cancelado | Se canceló el reintento mediante el token de cancelación del llamador. |

Campos: `Operation`, `Outcome`, `Retried`, `FinalStatus` y `ElapsedMilliseconds`. El cronómetro es monotónico e incluye la espera en la cola y la respuesta HTTP del reintento. No incluye la solicitud 403 inicial ni la deserialización o escritura posterior. Cuando no hubo reintento, duración 0; cuando no se obtuvo respuesta HTTP, estado 0.

El registro de transporte conserva todas las respuestas HTTP para auditoría. Su contador bruto de 403 no debe tratarse como número de operaciones fallidas: puede incluir el desafío inicial y el rechazo del reintento. Los errores definitivos deben contarse por los eventos finales; las cancelaciones quedan separadas. Los registros anteriores al cambio no permiten reconstruir esta clasificación con certeza y no deben interpretarse como cero errores.

No se registran valores de tokens, cuerpos ni mensajes de excepciones. La instrumentación no realiza consultas preventivas ni añade reintentos, esperas o solicitudes de red.

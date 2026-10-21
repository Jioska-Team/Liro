# Reutilización de solicitudes y prioridad

El intervalo mínimo de solicitudes sigue siendo de 3 segundos. La puerta compartida mantiene una sola solicitud en curso y respeta los tiempos de espera indicados por Roblox. No se añaden solicitudes de calentamiento ni comprobaciones periódicas para mantener la caché.

## Recorrido continuo y presupuesto global

Al terminar un recorrido principal correctamente, el siguiente comienza sin la antigua pausa fija de un minuto. Cada página vuelve a competir por un turno; los conocidos y las complementarias conservan el reparto justo existente. El intervalo `LimitedPollInterval` continúa controlando la comprobación de novedades durante un recorrido largo, no una pausa entre recorridos completos. Los errores conservan sus plazos de reintento.

Valkey guarda un presupuesto global de **18 intentos por ventana móvil de 60 segundos**, compartido entre la API y el trabajador mediante `liro:{roblox}:request-budget:v1`. Todos los clientes de catálogo, detalles, componentes e imágenes usan este presupuesto. Un reinicio de la aplicación no borra el contador. La separación de tres segundos sigue activa y también se verifica entre clientes distintos que usan el mismo Valkey.

Cada intento ocupa un cupo pendiente desde la reserva hasta el cierre de la solicitud, incluidos errores y cancelaciones. El cierre confirmado en Valkey inicia la ventana de 60 segundos de ese cupo. Como ocurre después del envío real, el desfase entre reservar y enviar no puede liberar capacidad antes de tiempo. Los cierres repetidos son idempotentes. Al actualizar desde la versión anterior se conservan los cupos vigentes durante una ventana completa, sin reiniciar el contador.

Una consulta que espera capacidad no consume reservas adicionales. La selección local de turnos conserva la prioridad principal y la equidad. Al agotarse el presupuesto se espera hasta que caduque un cupo cerrado o se confirme uno pendiente, sin descartar el trabajo ni hacer una llamada a Roblox. El registro «Presupuesto global de Roblox» muestra esa espera. Los reintentos HTTP automáticos internos se desactivan: los procesos conservan sus reintentos con seguimiento y toda nueva llamada debe pasar otra vez por la puerta y el presupuesto.

Un cierre que no pudo confirmarse se reconcilia antes de reservar otro turno. Las reservas nuevas vencen a los cinco minutos; antes del envío se valida su vigencia y se acota HTTP a treinta segundos con un minuto de margen. Al recuperar una reserva vencida, incluso de otro host, se contabiliza como completada durante una ventana íntegra. Las reservas antiguas sin plazo solo se recuperan automáticamente si se confirma que su proceso local terminó; las de otro host requieren verificación explícita. Ver [recuperación de reservas](../MARKET-DIRECT-FALLBACK.md#recuperación-de-reservas).

La configuración admite hasta 18 solicitudes y ventanas de al menos un minuto. API y trabajador deben usar el mismo Valkey y la misma clave/configuración; instancias conectadas a otro Valkey no comparten el contador. Si Valkey falla, no se envía tráfico sin contabilizar y el error conserva el trabajo pendiente para el reintento habitual. Este límite no garantiza que Roblox nunca responda 429.

## Token de validación

Los clientes de catálogo comparten el último token recibido en la cabecera `x-csrf-token` dentro del proceso. La siguiente consulta lo envía directamente. Si Roblox devuelve un rechazo con otro token, se renueva y se repite una sola vez mediante el mismo control de tráfico. Un segundo rechazo invalida ese token. El token permanece solo en memoria y no se escribe en registros, archivos ni respuestas de la API.

## Observaciones recientes

### Vueltas de conocidos y consultas simultáneas

Cuando los resultados descargados y las observaciones reutilizadas suman más de 120 artículos, la persistencia los divide en grupos de hasta 120. Cada grupo usa un contexto independiente y conserva los datos originales en memoria. No se vuelven a solicitar los detalles a Roblox para guardar el siguiente grupo. La protección de 120 del escritor real permanece activa. Las confirmaciones de conocidos se guardan después de procesar los resultados, con sus fechas y reservas individuales.

El mensaje «Consulta compartida» registra cada consumidor adicional que se une a una operación existente, con el tipo, el proceso y el total acumulado. No registra el cursor, los identificadores ni los parámetros de la consulta. El diagnóstico periódico de conocidos muestra «Consultas compartidas acumuladas en este proceso», incluso cuando vale cero. El contador pertenece a la sesión del proceso y vuelve a cero al reiniciar; API y trabajadores tienen contadores independientes. Cuenta uniones, incluidas las que posteriormente fallan o se cancelan: no equivale al número de respuestas correctas ni a una medición exacta de HTTP evitados. Una consulta original o independiente no lo incrementa.

Los conocidos pendientes se seleccionan por su última confirmación, empezando por los nunca confirmados y luego los más antiguos. Se respetan las fechas de próxima revisión, las reservas activas y las exclusiones por errores. Una fecha de próxima revisión antigua no permite que un artículo recién confirmado adelante a otro que lleva más tiempo pendiente.

Cada artículo conserva la fecha de su propia observación. El lote ya no copia la fecha del dato reutilizado más antiguo a todos los artículos recién consultados. Las fechas y las próximas revisiones se guardan mediante una actualización agrupada, condicionada a la reserva de cada identificador; una reserva obsoleta no sobrescribe el resultado.

Al preparar un lote, los datos recientes se resuelven desde la sesión compartida y sus huecos se completan con otros pendientes hasta reunir 100 identificadores que requieran HTTP, o hasta agotar los pendientes. Se limita la preparación a diez selecciones de base de datos para evitar una ronda local ilimitada. Se comprueba otra vez la caché antes de preparar la petición. Un lote puede confirmar más de 100 artículos contando las observaciones reutilizadas, pero ninguna solicitud de detalles contiene más de 100 identificadores. Si no hay suficientes pendientes, se envía el lote parcial sin esperar a llenarlo. No se garantiza que cada envío tenga 100: la caché del cliente puede resolver más identificadores después de preparar el lote.

Los clientes de un mismo proceso comparten consultas idénticas que estén en curso: búsquedas con los mismos parámetros y cursor, consultas individuales o lotes con las mismas identidades, aunque cambie el orden del lote. No se unen lotes solo parcialmente coincidentes. Se separan las clases de prioridad para impedir que la búsqueda principal herede una espera ordinaria. Cada consumidor puede cancelar su espera; la operación subyacente se cancela cuando no queda ninguno. Al finalizar se retira la operación compartida; los errores no se conservan como respuestas reutilizables. La caché de observaciones mantiene su caducidad original.

Estas mejoras no cambian los tres segundos, las 18 solicitudes globales, las 12 búsquedas por minuto ni el reparto de turnos. No reducen la frecuencia de las complementarias ni de la principal. El tiempo efectivo de una vuelta debe medirse después del reinicio: el número de confirmaciones acumuladas puede incluir revisiones repetidas y no equivale a artículos distintos.

Las consultas individuales, las consultas de detalles por lotes y las búsquedas con metadatos completos comparten observaciones validadas durante un máximo de 20 segundos desde su fecha original de observación. Esa fecha no se renueva al leer la caché. Una consulta por lotes envía únicamente los identificadores que falten; si todos están cubiertos, no hace una solicitud HTTP.

La revisión de limitados conocidos puede aprovechar esas observaciones durante su intervalo de revisión, con un máximo de un minuto. Conserva la fecha original y programa la siguiente revisión desde esa fecha. Cada identidad conserva su ObservedAtUtc original y programa su siguiente revisión desde esa fecha; reutilizar una observación no renueva su antigüedad. La vigencia de las consultas generales sigue siendo de 20 segundos. Si todos los conocidos del lote tienen datos recientes, se guarda el resultado sin consultar Roblox; si solo algunos los tienen, se consulta únicamente el resto.

La clave incluye el tipo de artículo y su identificador: un paquete y un artículo con el mismo número son entidades diferentes. Los errores, omisiones y respuestas con identidades duplicadas no se guardan como observaciones válidas. Se conservan como máximo 5.000 observaciones y se retiran las vencidas al consultar o necesitar espacio. Las observaciones anteriores no reemplazan otras más recientes. La caché se pierde al reiniciar y es independiente en cada proceso.

El mensaje «Detalles recientes reutilizados» indica cuántos identificadores de un lote se resolvieron sin repetir su consulta y cuántos se enviaron a Roblox. La mejora depende de la coincidencia entre las búsquedas y las revisiones; no se garantiza un tiempo fijo de recorrido.

## Prioridades

### Presupuesto adicional de búsquedas y páginas pendientes

Se conserva el máximo global de 18 solicitudes por ventana móvil de 60 segundos y la separación mínima de 3 segundos. Además, `/v2/search/items/details` comparte entre API y trabajadores un presupuesto de hasta 12 búsquedas por 60 segundos, configurable mediante `Roblox:Requests:SearchBudgetCapacity` entre 1 y 12. Usa una clave derivada del presupuesto global en Valkey; no se reinicia al crear un cliente. Las reservas se mantienen durante la solicitud y durante 60 segundos después de terminar, igual que en el presupuesto global.

El valor 12 es una precaución operativa frente a los rechazos observados tras 13–14 búsquedas en un minuto, no un límite oficial de Roblox ni una garantía de ausencia de respuestas 429. El presupuesto de búsquedas incluye la principal, las complementarias y los artículos recientes. Las consultas por identificadores siguen sujetas al presupuesto global. Cuando no queda cupo de búsqueda, se conserva la página pendiente y se libera el turno sin enviar HTTP ni imponer otros tres segundos por una solicitud que no se hizo. La prioridad y el reparto entre colas no cambian.

La búsqueda principal, las complementarias y las ventanas recientes reintentan exactamente la página pendiente después del plazo indicado. Conservan el cursor, el orden de las variantes, los contadores y el cronómetro del recorrido. No vuelven a la cabecera por un 429. La comprobación periódica de novedades de la principal sigue activa; no es un reinicio del recorrido. Los cursores inválidos conservan su tratamiento anterior y la cancelación interrumpe las esperas. Un reinicio del proceso todavía puede producir un primer recorrido parcial desde el punto guardado.

Los registros de respuesta identifican el proceso, la ruta sin parámetros, el estado HTTP y los encabezados `Retry-After`, `x-ratelimit-limit`, `x-ratelimit-remaining` y `x-ratelimit-reset`, si existen. No se registran cookies, tokens ni el cursor. Los encabezados se observan para diagnóstico; no se interpreta automáticamente un formato desconocido. Un 429 real continúa respetando `Retry-After` y el enfriamiento compartido ya existente; una espera local de presupuesto no activa ese enfriamiento.

Después del reinicio se debe medir la tasa de 429, el tiempo total de recorridos completos y el avance de todas las variantes. El objetivo es recuperar capacidad gastada en rechazos y páginas repetidas; no se garantiza conservar un tiempo exacto si Roblox restringe más el servicio.

Los monitores de limitados conservan su prioridad. Dentro de los turnos prioritarios, la búsqueda principal recibe hasta dos turnos por cada turno de conocidos u otras confirmaciones prioritarias cuando ambas colas tienen trabajo. Después de tres turnos prioritarios se atiende uno ordinario: las búsquedas complementarias y los artículos recientes siguen avanzando por su cola compartida. Si una cola no tiene trabajo, otra aprovecha el turno.

La siguiente solicitud se elige al terminar los 3 segundos posteriores a la respuesta anterior. Esto permite que la búsqueda principal procese la respuesta y prepare su siguiente página antes de elegir al siguiente solicitante. Se mantiene una sola solicitud en curso y no se acorta el intervalo ni se añaden consultas.

Las miniaturas usan una cola secundaria incluso dentro de una operación prioritaria. Durante un recorrido principal reciben un turno tras 24 turnos de datos bajo carga sostenida; fuera del recorrido, tras ocho. Si no hay datos esperando, pueden avanzar inmediatamente al terminar el intervalo compartido. Esta excepción acotada evita bloquear imágenes indefinidamente si una búsqueda se prolonga.

## Verificación tras reiniciar

Cuando aparece un 429, los procesos esperan hasta el plazo calculado por la puerta compartida, sin añadir el minuto mínimo del intervalo general de reintentos. Si Roblox informa cinco segundos, se espera el tiempo restante de esos cinco segundos; si informa un plazo mayor, se respeta. Si no entrega un plazo válido, sigue activo el cálculo conservador de espera de la puerta compartida. Los lotes de conocidos rechazados quedan pendientes para ese mismo plazo, sin confirmarse ni perder identificadores. El control de tres segundos y la prioridad de la búsqueda principal continúan aplicándose al reanudar. Esto elimina esperas artificiales, pero no garantiza la ausencia de nuevos 429.

Comparar respuestas 403 y 429, mensajes de reutilización, tiempos de lotes y recorridos completos. Una respuesta 429 todavía es posible: no debe reducirse el intervalo como consecuencia automática de estas optimizaciones. Los 20 segundos de reutilización son un límite de antigüedad aceptado para ahorrar consultas, no un nuevo intervalo de revisión.

El mensaje «Revisión de limitados conocidos» distingue observaciones reutilizadas e identificadores pendientes de consulta. Identificadores reutilizados no equivale a solicitudes HTTP evitadas: un lote parcialmente cubierto todavía necesita una consulta. Se mantiene el mismo límite de tráfico; el tiempo real debe comprobarse tras reiniciar, comparando recorridos completos y excluyendo el primer recorrido si reanuda un cursor anterior.

# Paquetes y componentes del catálogo

Un paquete es el artículo padre y sus componentes son artículos de tipo `Asset`. Un componente puede pertenecer a varios paquetes. La tabla `BundleComponents` identifica cada relación mediante la clave única `(BundleId, ComponentItemId)`, con claves externas a `Items`. El índice único existente `(ItemType, RobloxAssetId)` evita duplicar artículos y distingue los identificadores de paquetes de los de componentes.

La composición procede de `bundledItems` de Roblox. Solo se interpretan entradas `Asset`; las entradas `UserOutfit` pertenecen a otra clase de recurso y no se importan como artículos. No se deducen relaciones por el nombre del artículo. Los identificadores todavía no importados quedan conservados en `Items.BundleComponentAssetIds`; `BundleComponentsObservedAtUtc` registra cuándo se observó la composición. Una respuesta sin el campo no borra relaciones conocidas; una lista explícitamente vacía sí puede retirarlas. Se ignoran observaciones anteriores a la ya guardada.

El proceso `BundleComponentsWorker` revisa hasta 100 paquetes o 10 componentes pendientes por ciclo, con una pausa de 30 segundos y el control compartido de solicitudes a Roblox. Si el catálogo omite un componente o sus datos no se pueden interpretar, consulta `economy.roblox.com/v2/assets/{id}/details`, validando su identidad y metadatos. El límite de 10 componentes acota estas solicitudes individuales. Revisa nuevamente la composición de paquetes al día siguiente y completa las relaciones cuando llegan los componentes. No reactiva la revisión histórica general de artículos normales. Los errores usan los reintentos existentes; los componentes que Roblox no permita confirmar pueden quedar pendientes. Las miniaturas pendientes de paquetes se consultan de dos en dos; una imagen todavía no disponible vuelve a intentarse después de una hora.

Un componente puede tener un creador diferente al usuario Roblox. Se conserva su creador real y solo se acepta si su identificador figura en la composición confirmada de un paquete oficial. Aparece en los detalles de ese paquete, pero no en el listado principal, que sigue filtrando por el usuario Roblox. No se permite importar artículos ajenos sin esta referencia. La recuperación de reintentos agotados debe comprobar nuevamente los datos y guardar correctamente el componente antes de eliminar su error; no se reinician indiscriminadamente todos los reintentos.

## Vista principal

El filtro se aplica en PostgreSQL antes de paginar. Se muestran paquetes y accesorios independientes del usuario oficial de Roblox. Se excluyen artículos corporales de los tipos 17, 27, 28, 29, 30, 31 y 79: cabeza, torso, brazos, piernas y cabeza dinámica. Se excluyen incluso si todavía no se conoce su paquete. Los accesorios, cabello, caras y ropa conservan su visibilidad aunque también formen parte de un paquete. Los registros sin tipo confirmado no se clasifican como piezas corporales por su nombre.

Los paquetes corporales usan la miniatura del paquete completo y se identifican como «Avatar completo». Otros paquetes conservan la etiqueta «Paquete». Una miniatura pendiente se indica expresamente. El botón «Ver componentes» muestra las piezas dentro de la ficha del paquete, sin convertirlas en tarjetas del catálogo principal.

## Contrato para la interfaz

- `GET /api/v1/catalog?page=1&pageSize=24`: devuelve `items`, `page`, `pageSize` y `hasMore`. Ordena por fecha de creación de Roblox descendente, dejando fechas desconocidas al final, y desempata por identificador interno. Tamaño permitido: 1–100. Página: 1–10000.
- `GET /api/v1/catalog/bundles/{bundleId}/components`: devuelve `bundle`, `components`, `pendingAssetIds` y `relationshipsObservedAtUtc`. Devuelve 404 si no existe el paquete. Los identificadores de la ruta son de Roblox.
- `GET /api/v1/catalog/assets/{assetId}/bundles`: devuelve todos los paquetes conocidos que contienen ese artículo. Una lista vacía significa que no hay relación conocida; no demuestra que Roblox no tenga ninguna.

Cada artículo incluye `id` (identificador interno), `robloxItemId`, `itemType`, `name`, `marketplaceUrl`, `thumbnailUrl`, `assetTypeId`, `bundleTypeId`, `isBodyPart` e `isCatalogVisible`, además de los metadatos existentes. Los detalles de mercado pueden ser nulos en el listado. La consulta directa de una pieza sigue disponible para la pantalla de detalles. Las rutas del catálogo admiten lectura desde la red local autorizada; no habilitan escritura remota.

## Activación y verificación

Reiniciar el backend para cargar la nueva API y el proceso de relaciones. El inicializador de desarrollo aplica la migración `AddBundleComponents`. La carga de paquetes existentes se realiza progresivamente. La configuración de artículos recientes y monitores de limitados no cambia.

La consulta `scripts/catalogo-relaciones-diagnostico.sql` muestra el avance de las relaciones y comprueba duplicados de artículos y relaciones.

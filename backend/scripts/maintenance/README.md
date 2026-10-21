# Respaldos locales y restauracion

Requiere PowerShell 7, `kubectl` y los clientes oficiales PostgreSQL (`pg_dump`,
`pg_restore`, `psql`) de la misma version principal que el servidor o una posterior
compatible. No instala ni arranca PostgreSQL. Usar una carpeta de respaldos privada
fuera del repositorio; los archivos contienen datos de la aplicacion.

Copiar `backup-config.example.json` fuera del repositorio, ajustar las rutas y crear
la carpeta destino. El archivo no contiene contrasenas. Por defecto cada ejecucion
obtiene la conexion del AppHost local mediante su kubeconfig. Los puertos pueden
cambiar: no se guarda un puerto ni una contrasena caducados. Si Aspire esta cerrado,
el trabajo falla y registra el fallo, sin iniciar servicios. Tambien se admite la
variable de proceso `LIRO_BACKUP_POSTGRES` para ejecuciones administradas.

```powershell
pwsh -NoProfile -File .\scripts\maintenance\Backup-Database.ps1 -ConfigPath C:\ruta\config.json
pwsh -NoProfile -File .\scripts\maintenance\Setup-DatabaseBackups.ps1 -ConfigPath C:\ruta\config.json
Get-ScheduledTaskInfo -TaskName Liro.DatabaseBackup
```

La tarea diaria es a las 03:00, hora de Windows; recupera ejecuciones omitidas y
reintenta tres veces cada 30 minutos. S4U permite ejecutarla sin sesion iniciada ni
guardar la contrasena del usuario; necesita permisos para registrar tareas. El PC
y PostgreSQL deben estar disponibles. Usa `IgnoreNew` y un bloqueo de archivo para
no duplicar trabajos ni solapar una restauracion. No reinicia el backend ni bots.
El registro de la tarea es obligatorio para considerar activa la automatizacion.

Cada copia usa `pg_dump -Fc`, una sola conexion y compresion nivel 1. Conserva las
14 copias completas mas recientes, solo depura despues de un respaldo exitoso y
no borra tablas ni historial. Los `.partial` no cuentan como respaldos. Conserva
SHA-256, tamano y fecha en el manifiesto, `last-success.json` y `last-failure.json`.
Supervisar la antiguedad de `last-success.json`: un fallo no reemplaza el ultimo
exito. Estas copias en el mismo PC no protegen contra perdida del disco; una copia
externa es necesaria antes de produccion. No incluye roles globales, secretos ni
Valkey; el alcance es la base PostgreSQL completa.

```powershell
pwsh -NoProfile -File .\scripts\maintenance\Restore-DatabaseBackup.ps1 `
  -ConfigPath C:\ruta\config.json -BackupFile C:\ruta\lirodb-FECHA.dump `
  -DatabaseName liro_restore_prueba
```

Verifica el hash antes de conectar, exige un nombre `liro_restore_...` y crea una
base nueva. Si existe, falla sin sobrescribirla. Restaura en una transaccion y
verifica los recuentos de articulos, mercado, historial y RAP. No arranca ningun
worker sobre la copia, no usa `--clean` y no cambia la configuracion de la API.
Una restauracion fallida puede dejar la base nueva vacia para diagnostico: nunca
se borra automaticamente. La copia validada queda disponible para inspeccion.

## Pruebas obligatorias

`scripts/verification/Verify.ps1` y el ejecutable `Liro.RegressionTests` requieren:

- `LIRO_TEST_POSTGRES`: conexion a una base separada cuyo nombre empiece por
  `liro_test_`. Puede estar en el servidor local, pero nunca debe ser `lirodb`.
- `LIRO_TEST_REDIS`: conexion Valkey con `defaultDatabase=15` (u otra base logica
  no cero dedicada a pruebas). Los casos usan claves unicas y limpian sus claves;
  no se vacia la instancia. Para CI se recomienda una instancia separada.

Las conexiones se proporcionan como secretos del entorno, no se versionan.
Antes de registrar pruebas se ejecutan SELECT 1 y PING. Si falta una dependencia,
el ejecutable devuelve 2 y no informa una suite verde con casos omitidos. Tambien
se aplica a los subconjuntos `--...-only`. `--check-integration` comprueba solo
dependencias. La verificacion completa prueba ademas la ausencia, indisponibilidad
y configuracion peligrosa de esas conexiones, sin mostrar credenciales.

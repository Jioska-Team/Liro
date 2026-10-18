# Recuperación histórica retirada

El proyecto temporal Liro.HistoryRecovery y sus pruebas se retiraron por decisión del usuario. Ya no se inicia con AppHost ni realiza consultas a Internet Archive u otras fuentes de recuperación.

El trabajador Liro.MarketWorker sigue consultando RAP e historial disponible en la API de Roblox mediante los proxies configurados en CatalogoGeneral. Conserva su inicio y parada con el backend.

Los datos históricos importados anteriormente, las tablas de archivo y la vista MarketHistoryCombined se conservan. Los scripts de consulta y diagnóstico histórico pueden seguir utilizándose para leerlos. La retirada del proyecto no borra ni reinicia datos ni activa tareas pendientes.

La consulta Consultar-Historial-Completo.sql combina las fuentes guardadas; su nombre no garantiza cobertura completa desde la conversión a limited. Mercado seguirá incorporando los datos que proporcione Roblox.

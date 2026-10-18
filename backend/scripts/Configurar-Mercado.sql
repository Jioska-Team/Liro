-- Ejecutar DESPUÉS de aplicar la migración. Plantilla inactiva; no contiene credenciales.
-- No sobrescribe registros existentes. Sustituir host/puerto y referencia antes de activar.
INSERT INTO "CatalogoGeneral"
  ("Id","Tipo","Clave","Valor","TipoDato","Descripcion","Activo",
   "FechaCreacion","FechaModificacion","CreadoPor","ModificadoPor")
VALUES
  ('c76d5832-9203-425a-8fd5-5b6cc09d3801','CONEXION_MERCADO','PROXY_MERCADO_01',
   '{"Address":"http://proxy.example:8080","SecretReference":"PROXY_01","RequestsPerMinute":6}',
   'Json','Proxy exclusivo de mercado; reemplazar dirección y configurar secreto antes de activar.',false,
   NOW(),NOW(),'configuracion-inicial','configuracion-inicial')
ON CONFLICT ("Tipo","Clave") DO NOTHING;

-- Ejemplo de edición SIN contraseñas:
-- UPDATE "CatalogoGeneral" SET "Valor"='{"Address":"http://HOST:PUERTO","SecretReference":"PROXY_01","RequestsPerMinute":6}',
-- "Activo"=true,"FechaModificacion"=NOW(),"ModificadoPor"='administrador'
-- WHERE "Tipo"='CONEXION_MERCADO' AND "Clave"='PROXY_MERCADO_01';

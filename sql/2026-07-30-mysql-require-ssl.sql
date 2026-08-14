/* ============================================================================
   Backfill: REQUIRE SSL en los usuarios MySQL ya aprovisionados
   ----------------------------------------------------------------------------
   ESTE SCRIPT SE EJECUTA EN EL MOTOR MySQL, no en el catálogo de SQL Server
   (es el único de la carpeta sql/ que no va contra SQL Server — ojo con eso).

   Contexto (docs/bugs.md ítem 28): desde ahora MySqlProvisioner crea los
   usuarios con REQUIRE SSL, así el motor rechaza cualquier conexión sin cifrar
   de ese usuario. Los usuarios creados ANTES de este cambio no lo tienen: sus
   credenciales pueden seguir viajando en texto plano si el cliente no pide TLS,
   que es justamente el agujero que se está cerrando ahora que el puerto es
   accesible desde afuera.

   REQUISITO PREVIO: el servidor tiene que tener TLS habilitado y funcionando.
   Verificarlo ANTES de correr el ALTER — si no, los usuarios afectados quedan
   sin poder conectarse:

       SHOW VARIABLES LIKE 'have_ssl';        -- debe decir YES
       SHOW VARIABLES LIKE 'require_secure_transport';
       STATUS;                                -- "SSL: Cipher in use is ..."

   ADVERTENCIA: es un cambio con efecto inmediato sobre usuarios reales. Un
   usuario que hoy se conecta con TLS desactivado a propósito va a empezar a
   recibir "Access denied". Eso es lo buscado, pero conviene avisarlo antes.
   ========================================================================== */

/* ---------------------------------------------------------------------------
   PASO 1 — Ver el estado actual. ssl_type vacío = no exige cifrado.
   El prefijo 'usr_colmena_' es el que arma el catálogo al reservar la BD; si
   en algún ambiente se usó otro, ajustarlo en las tres consultas de acá abajo.
   --------------------------------------------------------------------------- */
SELECT user, host, ssl_type, plugin
FROM mysql.user
WHERE user LIKE 'usr\_colmena\_%'
ORDER BY user;

/* ---------------------------------------------------------------------------
   PASO 2 — Generar los ALTER. No se ejecuta nada todavía: devuelve una columna
   de sentencias para revisar y luego copiar/pegar. Se hace así a propósito, en
   vez de un bloque dinámico que altere todo de una: son cuentas de usuarios
   reales y conviene ver la lista antes de aplicarla.

   ALTER USER ... REQUIRE SSL no toca la contraseña ni los permisos ni los datos:
   solo agrega la exigencia de canal cifrado.
   --------------------------------------------------------------------------- */
SELECT CONCAT(
           'ALTER USER ''', user, '''@''', host, ''' REQUIRE SSL;'
       ) AS sentencia
FROM mysql.user
WHERE user LIKE 'usr\_colmena\_%'
  AND ssl_type = ''          -- solo los que todavía no lo exigen
ORDER BY user;

/* ---------------------------------------------------------------------------
   PASO 3 — Verificar. Después de aplicar los ALTER del paso 2, ssl_type debe
   decir 'ANY' en todas las filas (equivale a REQUIRE SSL: exige canal cifrado
   sin pedir un certificado de cliente concreto).
   --------------------------------------------------------------------------- */
SELECT user, host, ssl_type
FROM mysql.user
WHERE user LIKE 'usr\_colmena\_%'
ORDER BY user;

/* ---------------------------------------------------------------------------
   REVERTIR (si algo sale mal y hay que devolver el acceso sin cifrar):

       ALTER USER 'usr_colmena_uXX_nombre'@'%' REQUIRE NONE;

   Y para dejar de exigirlo también en las BDs nuevas, poner
   Provisioning:MySql:RequireTls = false en appsettings.json — eso apaga a la
   vez el REQUIRE SSL del CREATE USER y el parámetro ssl-mode/sslMode de las
   cadenas de conexión que se le entregan al usuario.
   --------------------------------------------------------------------------- */

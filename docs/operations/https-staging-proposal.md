# HTTPS para staging — propuesta

**Estado:** propuesta, sin cambios de infraestructura. No desplegar hasta aprobar dominio y ventana.

## Situación actual

- El droplet expone **puerto 80** únicamente; `web` (nginx) sirve la SPA y hace proxy de `/api` a `api:8080`.
- La API y el worker no están expuestos directamente.
- La autenticación viaja en `Authorization: Bearer`; sobre HTTP puede leerse en tránsito. Por eso solo se
  admiten documentos sintéticos hasta habilitar TLS.

## Opción recomendada: Caddy como terminador TLS

Un contenedor Caddy delante de `web`, con certificados Let's Encrypt automáticos:

```
Internet :80/:443 -> caddy -> web:80 -> api:8080
```

- `web` deja de publicar `80:80`; solo Caddy publica `80:80` y `443:443`.
- Caddy hace proxy a `web:80` y mantiene el mismo origen para la SPA y `/api`.
- Certificado y renovación automáticos (HTTP-01); necesita el puerto 80 accesible.
- Variables: `STAGING_PUBLIC_URL=https://<host>`; el workflow debe usar esa URL para CORS
  (`Cors__AllowedOrigins__0`) y para la verificación pública.
- No hay cambios en el frontend: sigue usando `/api` en el mismo origen.
- Health checks públicos: `https://<host>/health/ready`.

### Requisito de nombre

| Opción | DNS | Certificado | Comentario |
|---|---|---|---|
| **Subdominio propio** (recomendado) | `staging.<dominio>` → A `138.197.15.40` | Let's Encrypt para ese nombre | Requiere que indiques el dominio; no comprar nada si ya lo tienes |
| Sin dominio propio | `138-197-15-40.sslip.io` → A `138.197.15.40` | Let's Encrypt para ese nombre | Funciona sin registrar dominio; nombre poco presentable |
| Cloudflare Tunnel | sin abrir puertos | Cloudflare | Alternativa si no quieres exponer 80/443; no es Let's Encrypt directo |

## Opción B: nginx del host + certbot

- Mover el puerto 80 del contenedor `web` a `127.0.0.1:8080` y poner nginx del host como terminador.
- `certbot --nginx -d <host>`; renovación por timer.
- Más piezas que mantener que Caddy y duplica la configuración de proxy.

## Pasos de implementación (cuando se apruebe)

1. Confirmar dominio/subdominio y crear el registro A.
2. Añadir `caddy` al compose, volumen para certificados y `Caddyfile` con el host.
3. Quitar `ports: 80:80` de `web`; publicar `80/443` en Caddy.
4. Añadir `STAGING_PUBLIC_URL` como variable y usarla en `PUBLIC_BASE_URL`.
5. Desplegar con el workflow manual; verificar:
   - `https://<host>/health/ready` responde;
   - redirección 80→443;
   - HSTS y headers de seguridad;
   - certificado válido y renovación programada.
6. Solo entonces permitir documentos reales; los términos de Azure/Mistral siguen pendientes.

## Riesgos

- Cambiar el puerto publicado puede dejar el sitio inaccesible si Caddy no arranca; mantener la IP/fallback
  y una ventana de reversión.
- El certificado necesita que el puerto 80 esté libre y el DNS resuelva al droplet.
- No usar `sslip.io` para producción; solo staging si no hay dominio.
- TLS no sustituye la aprobación legal ni la revisión de retención de datos.

## Decisión necesaria

Indica:

1. dominio/subdominio a usar (o autorización para `sslip.io`);
2. ventana para el cambio;
3. si Caddy (recomendado) o nginx+certbot.

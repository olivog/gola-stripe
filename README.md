# Gola Stripe — esqueleto mínimo

Proyecto **ASP.NET MVC 5** (.NET Framework **4.7.2**) para abrir en **Visual Studio 2019**, probar F5 y luego subir a GitHub.

Aún **no** incluye Stripe. Eso viene en el siguiente ciclo.

## Requisitos

- Visual Studio 2019 (Workload: *ASP.NET and web development*)
- .NET Framework 4.7.2 Developer Pack (si VS lo pide al abrir)

## Abrir y depurar

1. Copia el contenido de este zip dentro de tu clone de `gola-stripe` (o descomprime y abre la solución).
2. Abre `GolaStripe.sln` con Visual Studio 2019.
3. Clic derecho en la solución → **Restore NuGet Packages** (si hace falta).
4. Pulsa **F5** (o IIS Express).
5. Debes ver: **Gola Stripe — OK**

## Subir a GitHub

```bash
cd gola-stripe
# (archivos del proyecto ya en la carpeta)
git add .
git commit -m "Initial ASP.NET MVC5 skeleton for VS2019"
git push
```

No subas `bin/`, `obj/`, `.vs/` ni keys de Stripe (el `.gitignore` ya los excluye).

## Siguiente paso

Cuando F5 funcione: NuGet `Stripe.net` + Checkout en modo test + webhook.

# OpenTelemetry (toggle)

## Objectif
Activer traces/metrics **optionnellement** sans casser le runtime.

## Attendu
- OFF par défaut
- Activer via appsettings ou variables d’environnement
- Instrumentation : ASP.NET Core + HttpClient (+ Npgsql si possible)

## Export
- OTLP endpoint (collector) si activé

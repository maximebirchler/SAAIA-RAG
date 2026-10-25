using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace SAAIA.Client.WinUI.Services;

internal static class ConnectionErrorClassifier
{
    internal static string Classify(Exception ex, string url, string lang)
    {
        var raw = ex?.Message ?? "";
        var lower = raw.ToLowerInvariant();
        var host = string.IsNullOrWhiteSpace(url) ? "" : $" ({url})";

        if (ex is HttpRequestException { StatusCode: { } status })
        {
            if ((int)status == 401)
                return lang switch
                {
                    "en" => $"The API key was rejected. Check the server configuration{host}.",
                    "es" => $"La clave API fue rechazada. Revisa la configuración del servidor{host}.",
                    "pt" => $"A chave API foi rejeitada. Verifica a configuração do servidor{host}.",
                    "de" => $"Der API-Schlüssel wurde abgelehnt. Prüfen Sie die Serverkonfiguration{host}.",
                    "it" => $"La chiave API è stata rifiutata. Verifica la configurazione del server{host}.",
                    _ => $"La clé API a été refusée. Vérifiez la configuration du serveur{host}."
                };
            if ((int)status == 403)
                return lang switch
                {
                    "en" => $"This key does not allow access to the server. Contact the administrator{host}.",
                    "es" => $"Esta clave no permite acceder al servidor. Contacta con el administrador{host}.",
                    "pt" => $"Esta chave não permite aceder ao servidor. Contacta o administrador{host}.",
                    "de" => $"Dieser Schlüssel erlaubt keinen Serverzugriff. Kontaktieren Sie den Administrator{host}.",
                    "it" => $"Questa chiave non consente l'accesso al server. Contatta l'amministratore{host}.",
                    _ => $"Cette clé ne permet pas d'accéder au serveur. Contactez l'administrateur{host}."
                };
            return lang switch
            {
                "en" => $"The server rejected the connection (HTTP {(int)status}){host}.",
                "es" => $"El servidor rechazó la conexión (HTTP {(int)status}){host}.",
                "pt" => $"O servidor rejeitou a ligação (HTTP {(int)status}){host}.",
                "de" => $"Der Server hat die Verbindung abgelehnt (HTTP {(int)status}){host}.",
                "it" => $"Il server ha rifiutato la connessione (HTTP {(int)status}){host}.",
                _ => $"Le serveur a refusé la connexion (HTTP {(int)status}){host}."
            };
        }

        bool Has(params string[] needles)
        {
            foreach (var n in needles)
                if (lower.Contains(n, StringComparison.Ordinal)) return true;
            return false;
        }

        // Timeout (TaskCanceledException + Windows socket "did not properly respond after a period of time").
        if (ex is TaskCanceledException || Has("timed out", "did not properly respond after a period of time", "operation has timed out"))
        {
            return lang switch
            {
                "en" => $"The server did not respond within the timeout{host}.",
                "es" => $"El servidor no respondió dentro del tiempo límite{host}.",
                "pt" => $"O servidor não respondeu dentro do tempo limite{host}.",
                "de" => $"Der Server hat innerhalb des Timeouts nicht geantwortet{host}.",
                "it" => $"Il server non ha risposto entro il timeout{host}.",
                _ => $"Le serveur n'a pas répondu dans les temps{host}."
            };
        }

        // Connection refused (active rejection by the server).
        if (Has("actively refused", "connection refused", "no connection could be made"))
        {
            return lang switch
            {
                "en" => $"Connection refused by the server{host}.",
                "es" => $"Conexión rechazada por el servidor{host}.",
                "pt" => $"Ligação recusada pelo servidor{host}.",
                "de" => $"Verbindung vom Server abgelehnt{host}.",
                "it" => $"Connessione rifiutata dal server{host}.",
                _ => $"Connexion refusée par le serveur{host}."
            };
        }

        // DNS resolution failure.
        if (Has("no such host", "name or service not known", "name resolution"))
        {
            return lang switch
            {
                "en" => $"Hostname could not be resolved{host}.",
                "es" => $"No se pudo resolver el nombre del host{host}.",
                "pt" => $"Não foi possível resolver o nome do host{host}.",
                "de" => $"Hostname konnte nicht aufgelöst werden{host}.",
                "it" => $"Impossibile risolvere il nome host{host}.",
                _ => $"Nom d'hôte introuvable{host}."
            };
        }

        // Network unreachable (often: VPN down, route missing).
        if (Has("network is unreachable", "no route to host", "network unreachable"))
        {
            return lang switch
            {
                "en" => $"Network unreachable — check your VPN/Wifi{host}.",
                "es" => $"Red inaccesible — comprueba tu VPN/Wifi{host}.",
                "pt" => $"Rede inacessível — verifica a tua VPN/Wifi{host}.",
                "de" => $"Netzwerk nicht erreichbar — VPN/Wifi prüfen{host}.",
                "it" => $"Rete non raggiungibile — controlla la tua VPN/Wifi{host}.",
                _ => $"Réseau injoignable — vérifie ton VPN/Wifi{host}."
            };
        }

        // TLS/SSL issues.
        if (Has("ssl", "tls", "certificate"))
        {
            return lang switch
            {
                "en" => $"Check the server's TLS certificate{host}.",
                "es" => $"Comprueba el certificado TLS del servidor{host}.",
                "pt" => $"Verifica o certificado TLS do servidor{host}.",
                "de" => $"Prüfen Sie das TLS-Zertifikat des Servers{host}.",
                "it" => $"Verifica il certificato TLS del server{host}.",
                _ => $"Vérifiez le certificat TLS du serveur{host}."
            };
        }

        // Fallback: keep raw message (still in English) but at least append URL for context.
        return string.IsNullOrEmpty(host) ? raw : $"{raw}{host}";
    }
}

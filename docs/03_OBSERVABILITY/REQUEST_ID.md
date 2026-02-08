# RequestId (corrélation)

## Objectif
Associer :
- une requête UI (client)
- un appel API
- un log serveur
- une erreur

## Règle
- Client peut envoyer `X-Request-Id`
- Serveur renvoie toujours `X-Request-Id`
- Body d’erreur contient `requestId` (même valeur)

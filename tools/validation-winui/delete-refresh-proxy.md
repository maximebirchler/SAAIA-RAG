# Panne bornée après une suppression réellement acquittée

Cet outillage sert uniquement à l'environnement WinUI synthétique local enregistré dans `environment.json`. Il laisse le backend exécuter le vrai DELETE et provoque ensuite une seule erreur 500 sur la lecture de l'inventaire du même utilisateur. Il ne simule pas une suppression échouée et ne modifie ni le backend ni sa base pour injecter la panne.

Le script `start-delete-refresh-proxy.ps1 -OutputDirectory <fixture-owned> -Port 5167 -InstanceName <unique>` enregistre le processus avec le mécanisme d'ownership existant. Le port amont vient de l'environnement propriétaire, donc pas d'adresse externe. Il refuse un port déjà occupé et tout écrasement d'un fichier de preuve/contrôle existant. L'injection est désactivée au démarrage.

Après création d'une discussion de test jetable et avant le clic final Supprimer, remplacer le contenu du fichier de contrôle annoncé par le proxy par cet objet, avec les deux UUID réels de la campagne :

```json
{
  "schemaVersion": "saaia-winui-delete-refresh-control.v1",
  "enabled": true,
  "state": "waiting_for_delete_ack",
  "targetSessionId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
  "targetUserId": "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
  "inventoryFailuresInjected": 0
}
```

Le contrôle suit `waiting_for_delete_ack` → `delete_acknowledged` → `consumed`. Une erreur du DELETE, un autre ID de session, un autre utilisateur, une lecture de messages ou une autre route ne consomme pas le défaut. Après le GET ciblé, il se désactive et enregistre `inventoryFailuresInjected: 1`. Ne pas réarmer un ancien fichier pour fabriquer une nouvelle tentative : utiliser une instance distincte et garder les preuves de chaque exécution.

La reproduction doit établir, dans cet ordre, le vrai DELETE 2xx, l'unique GET inventaire 500 injecté, l'absence durable de la cible et le comportement observé dans WinUI. Le journal public n'exporte ni corps de messages, ni query userId, ni headers d'authentification. L'ID de cible figure dans le chemin DELETE et sert à relier la preuve à la discussion de test.

Les tests `python -m unittest discover -s tools/validation-winui -p test_delete_refresh_proxy.py -v` exercent seulement la machine d'état et ses bornes. Ils ne démarrent aucun serveur, fenêtre, modèle ou base. Ils ne constituent donc pas la preuve de reproduction du défaut client.

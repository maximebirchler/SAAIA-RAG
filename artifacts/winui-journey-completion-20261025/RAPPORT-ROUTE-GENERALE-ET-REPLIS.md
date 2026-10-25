# La route générale acceptée pouvait être détournée par un repli documentaire

Le problème observé pendant le test de streaming est dans l'orchestration du client. `toolName:none` a bien été accepté : le routeur a produit `chat.general`, aucun outil et aucune mission documentaire. Le code ignorait ensuite ce choix dans deux replis lexicaux.

## Cause reproduite

Pour « Bonjour, peux-tu afficher la réponse de contrôle du streaming ? », la regex `actionConnectorTarget` extrait « contrôle du streaming » comme titre potentiel d'item. Le détecteur d'action reconnaît « peux » et des mots significatifs. Cela ne prouve pourtant pas que l'utilisateur demande une recherche documentaire.

`ApplyDocumentaryRagDefaults` et le repli de sujet autonome respectaient déjà une route explicite générale sans outils. Mais `TryHandleExactItemFallbackSourceBackedRagPipelineAsync`, appelé ensuite par `RunAsync`, ne consultait que les mots du message. Il lançait donc `rag.answer` malgré le choix accepté du routeur.

Une deuxième chaîne a été reproduite avec « Comment vas-tu ? » : le repli `documentary_probe_fallback` utilisait le point d'interrogation comme signal et ignorait également la route générale. Ces cas peuvent donc affecter de vraies questions générales ; ce n'est pas simplement un problème de formulation de la fixture.

Les anciens tests vérifiaient surtout les helpers de défauts et d'exploration. Ils ne traversaient pas cette chaîne entière après l'acceptation du routeur.

## Correction et preuve

Les deux replis réutilisent maintenant le garde existant `ShouldRespectLlmRouterGeneralWithoutTools`. Il s'applique seulement à une route provenant du LLM, générale, sans clarification et sans outil. Les replis locaux sans route fiable et les routes documentaires explicites restent éligibles. La sonde `source_backed_fallback.skipped` indique la raison `router_general_no_tools` et le point d'entrée concerné.

Trois tests comportementaux traversent la méthode de production `RunAsync`, les deux étapes du routeur natif, puis le writer ou la frontière HTTP de recherche :

- **Avant correction : deux échecs, un succès.** Les deux questions générales déclenchent un troisième appel documentaire et aucun stream ; le contrôle documentaire atteint déjà sa recherche.
- **Après correction : 3/3 réussis.** Chaque question générale conserve exactement deux appels de routage, émet les deux deltas du writer général, ne fait aucune requête HTTP backend et ne crée aucun handoff avancé. La vraie route documentaire atteint toujours `/rag/search`, où le test s'annule volontairement : ce contrôle valide l'admission documentaire, pas la qualité d'une réponse RAG simulée.
- **Régressions voisines : 163/163 réussies**, les trois nouveaux cas inclus. Les empreintes des assemblages sont exactement celles de la compilation GREEN ; cette exécution sans recompilation n'est pas présentée comme un nouveau build.

Les campagnes conservent leurs empreintes avant/après identiques et les hashes TRX ont été vérifiés. Les tests utilisent un profil explicitement isolé, des LLM/HTTP simulés en mémoire et aucun appel payant. Le binaire de production WinUI ouvert pour les tests natifs n'a pas été reconstruit par ce lot.

## Périmètre honnête

Un premier essai comprenait aussi « Pourquoi un document mentionne-t-il sa version ? ». Son raccourci déterministe a exécuté une recherche **avant tout appel du routeur**. Ce cas ne démontrait donc pas le contournement d'une route acceptée. Son résultat initial est conservé dans `explicit-general-route-red`, mais il est retiré de la série RED confirmée et aucun changement du repli de versions n'est justifié par cet essai.

Le contrôle d'architecture passe : 1591 fichiers sources, 21 fichiers de taille déjà soumis à revue et aucune violation. Le correctif n'agrandit pas MainWindow.

La suite complète Client/Core et le retest natif de la phrase sur le futur build10 appartiennent aux étapes suivantes du parent. Ils ne sont pas déduits de ces tests. Le produit reste `TESTE_NON_APPROUVE` et aucun test de planning avec Terra n'a eu lieu.

## Preuves

- `explicit-general-route-causal-proof.json` : résultats, empreintes et limites.
- `explicit-general-route-red-confirmed/manifest.json` : deux échecs causaux et contrôle positif.
- `explicit-general-route-green/manifest.json` : trois tests après correction.
- `explicit-general-route-neighbor-regressions/manifest.json` : 163 tests, assemblages identiques.
- `explicit-general-route-architecture.json` : budgets architecturaux.

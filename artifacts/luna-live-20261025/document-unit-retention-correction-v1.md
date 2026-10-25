# Conservation des métadonnées et faits lors du découpage en unités

État : sept RED causaux capturés par l’opérateur ; correctif DocumentUnit préparé et gelé. Aucun build, service, test ou fournisseur exécuté par l’auteur de ce correctif. La validation GREEN et le nouveau préflight restent à effectuer.

## Perte identifiée après le texte de page

Le diagnostic natif mis à jour conserve les deux faits de contrôle dans le texte physique de la page 5. La quantité figure maintenant dans un chunk ; le nombre de convives reste absent des unités et ne peut donc pas être récupéré par le projecteur de chunks.

Deux décisions de suppression supplémentaires existaient dans `DocumentUnitExtractor` :

- Une qualification ou une donnée de cinq mots au plus pouvait être effacée dès qu’elle était répétée sur deux pages à une position textuelle stable. Le type ou le rôle du texte n’était pas établi.
- Un bloc court pouvait être déclaré « metadata » inutile dès deux correspondances d’un vocabulaire de durée/qualification ou de valeurs temporelles. Deux mesures comme `2 h` et `45 minutes`, ou une étiquette `Duration` suivie de `4 h`, suffisaient. Ce filtre était appliqué à l’admission, à la matérialisation et au fallback.

Le fallback de page ne réparait pas ces pertes lorsqu’une autre unité utile existait déjà sur la même page.

## RED et correction générale

Sept cas indépendants ont été ajoutés puis ont tous échoué sur le produit initial : quatre cas répétés de qualification, compte, fraction Unicode et avertissement opérationnel ; trois cas de mesures/plages temporelles courtes. Les assertions vérifient aussi la page physique, le lien de section et la présence d’un contenu distinct. Aucun fait de recette n’entre dans les règles produit.

Les deux décisions de suppression ont été retirées de tous leurs points d’application. La répétition, la position stable, une qualification ou plusieurs mesures ne prouvent pas l’inutilité d’une information. Les filtres de bruit OCR distincts et les liens avec les sections restent en place.

Les anciennes attentes exigeant l’effacement de labels répétés et de plages horaires ont été remplacées par des assertions positives de conservation. Elles n’établissaient pas que ces textes étaient inutiles. Les helpers exclusivement dédiés aux suppressions ont été retirés ; la recherche sur les sources backend a confirmé l’absence de leurs références. Le regex de fin de phrase devenu sans appel a également été supprimé.

## Limites et suite

Ce correctif ne constitue pas une validation générale de tous les filtres OCR. `OcrNoiseFilter` possède notamment ses propres règles de métadonnées courtes ; si un test reste en échec, leur effet doit être établi séparément, sans ajouter une exception métier. Les faits doivent être vérifiés de bout en bout : texte physique, unité, chunk publié, lecture canonique et enfin réponse sourcée.

Les diagnostics de layout ajoutés au helper restent privés et ne changent pas le texte choisi pour le modèle. Aucun nouveau succès du cas payé n’est revendiqué ; le statut reste `TESTE_NON_APPROUVE`.

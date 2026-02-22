# 08 — Notes dev : line endings (LF/CRLF)

Sur Windows, il est fréquent de voir des warnings du type :

> `LF will be replaced by CRLF the next time Git touches it`

Ce n’est pas bloquant, mais ça pollue les `git diff` et les commits.

## Solution adoptée (repo)
Le repo inclut un fichier **`.gitattributes`** pour normaliser les fins de lignes :

- fichiers texte usuels (`.md`, `.ps1`, `.yml`, `.json`, `.cs`, `.sql`, …) → **LF**
- scripts Windows (`.cmd`, `.bat`) et `.sln` → **CRLF**
- binaires (`.png`, `.pdf`, `.zip`, …) → **binary**

## Après avoir pull / appliqué le patch
Exécute une normalisation **une seule fois** sur ta branche :

```powershell
git add --renormalize .
git status
git diff --cached --stat
```

Puis commit :

```powershell
git commit -m "chore: normalize line endings via .gitattributes"
```

> Note : si le diff est “énorme”, c’est normal la première fois (conversion EOL).  
> Ça évite ensuite des changements parasites à chaque modification de fichier.

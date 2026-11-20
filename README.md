# Discoscope

**Explorez votre disque. Retrouvez votre espace.**

Discoscope est un analyseur local de l'espace disque pour Windows 10/11. Il est développé en **C# / .NET 10 / WPF** avec une architecture MVVM. L'interface est entièrement en français ; le code et les API restent en anglais.

## Version actuelle : prototype 0.1.0

Le prototype comprend :
- Analyse réelle et asynchrone des fichiers et dossiers d'un lecteur sélectionné.
- Progression, annulation et classement des dossiers volumineux.
- Comptage des fichiers et avertissements d'accès.
- Analyse **en lecture seule** : aucune suppression, aucune donnée envoyée sur Internet.

### Démarrage

Prérequis : Windows 10/11 et SDK .NET 10.

```powershell
dotnet restore Discoscope.slnx
dotnet run --project src/Discoscope.Desktop
```

Tests :
```powershell
dotnet test tests/Discoscope.Core.Tests
```

### Limites techniques du prototype

Les tailles affichées sont les **tailles logiques des fichiers accessibles**, et non l'espace réellement alloué sur le volume. Les liens de réanalyse ne sont pas suivis ; les liens physiques NTFS, les fichiers compressés ou clairsemés et les zones protégées peuvent produire des écarts avec l'espace occupé annoncé par Windows. Certains répertoires nécessitent des privilèges élevés. Les avertissements d'accès sont comptabilisés sans masquer ces limites.

Les versions suivantes ajouteront des sources spécifiques Windows (VSS, DISM, réserves système, Windows Update) et un moteur d'actions de nettoyage avec prévisualisation, confirmation explicite et exécution via les API Windows adaptées. **Aucun fichier système n'est supprimé directement.**

## Développement

Modifications exclusivement sur `main`. Pas de CI/CD ni de GitHub Actions. Compilation et tests locaux sur Windows.

Le diagnostic des dossiers temporaires utilisateur et Windows est en lecture seule. La taille détectée ne correspond pas à une quantité supprimable garantie.

### Diagnostic Windows

Le bouton **Analyser Windows** lance uniquement `DISM /Online /Cleanup-Image /AnalyzeComponentStore /English`. Le rapport brut est affiché, sans interprétation trompeuse des tailles. Aucun nettoyage ni opération de réparation n’est exécuté. DISM peut nécessiter une élévation des privilèges ; une annulation interrompt le processus lancé.

## Architecture

- `Discoscope.Core` : modèles et scanner de dossiers, indépendants de WPF.
- `Discoscope.Desktop` : application WPF, XAML, MVVM, commandes et état.
- `Discoscope.Core.Tests` : tests de parcours et d'agrégation.

## Licence

Apache License 2.0. Voir [LICENSE](LICENSE).

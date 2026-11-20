# Discoscope

![Icône de Discoscope](src/Discoscope.Desktop/Assets/appicon-128.png)

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
dotnet build Discoscope.slnx -c Release
dotnet run --project src/Discoscope.Desktop -c Release --no-build
```

Tests :
```powershell
dotnet test Discoscope.slnx -c Release
```

### Limites techniques du prototype

Les tailles affichées sont les **tailles logiques des fichiers accessibles**, et non l'espace réellement alloué sur le volume. Les liens de réanalyse ne sont pas suivis ; les liens physiques NTFS, les fichiers compressés ou clairsemés et les zones protégées peuvent produire des écarts avec l'espace occupé annoncé par Windows. Certains répertoires nécessitent des privilèges élevés. Les avertissements d'accès sont comptabilisés sans masquer ces limites.

## Développement

Modifications exclusivement sur `main`. Pas de CI/CD ni de GitHub Actions. Compilation et tests locaux sur Windows.

Le diagnostic des dossiers temporaires utilisateur et Windows est en lecture seule. La taille détectée ne correspond pas à une quantité supprimable garantie.
Lorsque le lecteur analysé couvre ces dossiers, le diagnostic réutilise le même instantané. Les emplacements absents de l’instantané sont parcourus séparément.

### Diagnostic Windows

Le bouton **Analyser les composants** lance uniquement `DISM /Online /Cleanup-Image /AnalyzeComponentStore /English`. La sortie brute et les erreurs éventuelles sont affichées. DISM peut nécessiter des droits administrateur. Le bouton **Annuler** interrompt la commande et le délai maximal est de quatre minutes.

### Clichés instantanés VSS

L’onglet **Clichés instantanés · VSS** exécute uniquement `vssadmin list shadowstorage`. La sortie brute permet d’inspecter les associations de stockage des clichés. La commande peut nécessiter des droits administrateur ; une erreur de permissions reste visible. **Aucune suppression, aucun redimensionnement et aucune estimation de gain récupérable** ne sont effectués.

### Carte des dossiers

La carte représente les sous-dossiers immédiats et les fichiers du dossier courant, y compris ceux à la racine du lecteur. Chaque surface correspond à sa part de la taille logique analysée. Les petites entrées sont regroupées dans « Autres » et les éléments de taille nulle restent visibles dans l’Explorateur. Le survol indique le chemin, la taille et le pourcentage. Cliquez sur un dossier pour y entrer ou sur « Retour » pour remonter.

### Explorateur

Sélectionnez un dossier dans l’arborescence ou ouvrez-le dans le tableau par double-clic ou avec Entrée. Retour arrière remonte d’un niveau depuis le tableau. Les en-têtes trient par nom, taille logique ou type. « Copier le chemin » copie le chemin complet et « Ouvrir l’emplacement » affiche le dossier dans l’Explorateur Windows. La carte et l’Explorateur partagent le dossier courant et naviguent dans les résultats en mémoire.

### Fichiers volumineux

Pendant le scan, Discoscope conserve en mémoire les 200 plus grands fichiers accessibles grâce à une file de priorité bornée. L’interface affiche leur chemin, extension et taille logique. Les fichiers non accessibles et les points de réanalyse ne sont pas inclus. Cette liste est un diagnostic : aucune suppression automatique.

### Diagnostic des accès

L’onglet **Avertissements** affiche jusqu’à 2 000 chemins ignorés ou inaccessibles et leur raison. Le compteur global reste distinct de cette limite d’affichage. Le scanner conserve aussi les octets et le nombre de fichiers directement contenus dans chaque dossier, séparément des totaux récursifs, pour préparer un treemap fidèle.

### Interface

La fenêtre comprend **Vue d’ensemble**, **Explorateur**, **Fichiers volumineux**, **Avertissements** et **Système**. Les résultats restent disponibles lors des changements d’onglet. Les diagnostics DISM/VSS sont déclenchés séparément.

## Architecture

- `Discoscope.Core` : modèles et scanner de dossiers, indépendants de WPF.
- `Discoscope.Desktop` : application WPF, XAML, MVVM, commandes et état.
- `Discoscope.Core.Tests` : tests de parcours et d'agrégation.

Pour mesurer une analyse, un `ActivityListener` peut écouter `Discoscope.Core.Scan` et `Discoscope.Desktop.Scan`. Les activités distinguent l’énumération, la construction de l’arborescence, l’agrégation, l’inventaire temporaire et la préparation et la publication des résultats.

## Licence

Apache License 2.0. Voir [LICENSE](LICENSE).

# Discoscope

![Icône de Discoscope](src/Discoscope.Desktop/Assets/appicon-128.png)

Discoscope analyse l’espace disque local sous Windows. Il affiche les principaux consommateurs d’espace et permet d’examiner les fichiers temporaires avant de les déplacer vers la Corbeille.

- **Vue d’ensemble** affiche la capacité du lecteur, les dossiers les plus volumineux et la répartition par type de fichier.
- **Explorer** donne accès à l’arborescence, à la carte du disque et aux plus grands fichiers. La navigation réutilise les résultats en mémoire.
- **Nettoyage** présente les fichiers temporaires éligibles par pages de 1 000 et demande une confirmation avant tout déplacement.
- **Système** présente les mesures disponibles pour les catégories Windows, les avertissements et les rapports DISM et VSS.

## Compiler et lancer

Prérequis : Windows 11 recommandé, Windows 10 lorsque la version de Windows est prise en charge par .NET 10, et SDK .NET 10.

```powershell
dotnet build Discoscope.slnx -c Release
dotnet test Discoscope.slnx -c Release
dotnet run --project src/Discoscope.Desktop -c Release --no-build
```

Pour préparer un dossier distribuable pour Windows x64 avec le runtime inclus :

```powershell
dotnet publish src/Discoscope.Desktop -c Release -r win-x64 --self-contained true -o artifacts/Discoscope-win-x64
```

Lancer `Discoscope.exe` depuis ce dossier. Le dépôt ne fournit pas d’installateur signé.

## Comprendre les mesures

La capacité et l’espace libre sont rapportés par Windows. L’arborescence et la répartition par extension utilisent les tailles logiques des fichiers accessibles. Les fichiers compressés, clairsemés ou référencés par plusieurs liens physiques peuvent expliquer les écarts. Les liens physiques restent comptés par chemin, et les points de réanalyse sont ignorés. Les catégories système peuvent se recouvrir. Les mesures manquantes ou partielles sont signalées.

Le déplacement vers la Corbeille porte uniquement sur les fichiers temporaires éligibles et confirmés. Les métadonnées et l’identité sont revérifiées avant le déplacement. Le passage par le Shell reste une opération par chemin. L’espace libre mesuré avant et après est une observation du volume, et la taille logique des fichiers déplacés ne représente pas une quantité d’espace libéré.

Les autres catégories utilisent des diagnostics en lecture seule et un accès aux paramètres de stockage Windows. DISM et VSS peuvent nécessiter des droits administrateur. Discoscope ne déclenche aucune maintenance système destructive.

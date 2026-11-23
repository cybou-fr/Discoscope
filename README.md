# Discoscope

![Icône de Discoscope](src/Discoscope.Desktop/Assets/appicon-128.png)

Discoscope analyse l’espace disque local sous Windows. Il affiche les principaux consommateurs d’espace et permet d’examiner les fichiers temporaires avant de les déplacer vers la Corbeille.

- **Vue d’ensemble** affiche la capacité du lecteur, les dossiers les plus volumineux et la répartition des fichiers par type ou par date de modification. Sélectionnez une catégorie de fichiers ou une tranche d’ancienneté puis cliquez sur **Explorer** pour afficher ses fichiers.
- **Explorer** réunit l’arborescence, le tableau et la carte du dossier courant. La recherche par nom, extension, taille logique minimale ou date de modification utilise les résultats en mémoire. L’option **Tout le lecteur** affiche jusqu’à 10 000 correspondances et indique leur nombre total. Le tri s’applique à toutes les correspondances avant la limite d’affichage. Survolez un nom pour voir son chemin. La carte conserve la vue complète du dossier courant et les 200 plus grands fichiers du lecteur sont accessibles sous la carte.
- **Nettoyage** présente les fichiers temporaires éligibles par pages de 1 000 et demande une confirmation avant tout déplacement.
- **Système** présente les mesures disponibles pour les catégories Windows, les avertissements et les rapports DISM et VSS.

## Compiler et lancer

Prérequis : Windows 11 recommandé, Windows 10 lorsque la version de Windows est prise en charge par .NET 10, et SDK .NET 10.

```powershell
dotnet build Discoscope.slnx -c Release
dotnet test Discoscope.slnx -c Release
dotnet run --project src/Discoscope.Desktop -c Release --no-build
```

Pour lancer la vérification locale complète sous Windows, exécutez `./scripts/verify-local.ps1`. Les journaux de restauration, de compilation et de test, ainsi que les résultats TRX, sont conservés dans `artifacts/verification`.

Pour préparer un dossier distribuable pour Windows x64 avec le runtime inclus :

```powershell
dotnet publish src/Discoscope.Desktop -c Release -r win-x64 --self-contained true -o artifacts/Discoscope-win-x64
```

Lancer `Discoscope.exe` depuis ce dossier. Le dépôt ne fournit pas d’installateur signé.

## Comprendre les mesures

La capacité et l’espace libre sont rapportés par Windows. L’arborescence et la répartition par extension utilisent les tailles logiques des fichiers accessibles. Les fichiers compressés, clairsemés ou référencés par plusieurs liens physiques peuvent expliquer les écarts. Les liens physiques restent comptés par chemin, et les points de réanalyse sont ignorés. Les catégories système peuvent se recouvrir. Les mesures manquantes ou partielles sont signalées.

Le périmètre de l’analyse reste affiché si vous sélectionnez un autre lecteur. Pour une lettre attribuée à un dossier avec `subst`, la capacité affichée est celle du volume hôte. Le chemin associé est disponible au survol.

Dans **Explorer**, **Mesurer l’espace alloué** interroge les métadonnées NTFS des fichiers indexés du dossier courant et de ses sous-dossiers. La mesure est annulable. Elle présente jusqu’à 200 fichiers classés par taille allouée, avec leur taille logique. Les liens physiques répétés sont comptés une fois dans ce périmètre et les chemins devenus indisponibles sont signalés. Le résultat couvre le flux principal des fichiers mesurés et exclut notamment les métadonnées du volume et les chemins absents de l’index. Un fichier partagé avec un autre dossier peut apparaître dans les mesures des deux dossiers.

Le même passage classe jusqu’à 200 sous-dossiers directs, en incluant leurs descendants. Chaque ligne déduplique les liens dans son propre périmètre et indique si la mesure est partielle. Les lignes peuvent se recouvrir lorsque des liens physiques relient des sous-dossiers différents.

Le déplacement vers la Corbeille porte uniquement sur les fichiers temporaires éligibles et confirmés. Les fichiers comportant plusieurs liens physiques sont écartés. Quand Windows appelle le contrôle qui précède le déplacement, Discoscope exige que la Corbeille soit demandée et revérifie l’identité, les métadonnées et le nombre de liens. Un déplacement est compté uniquement si le Shell signale l’élément dans la Corbeille. Les dossiers parents restent ouverts pendant l’opération. Le fichier doit rester ouvert avec le partage de suppression pour que le déplacement puisse fonctionner. Un remplacement du fichier après le dernier contrôle du Shell reste possible. L’espace libre mesuré avant et après est une observation du volume. La taille logique des fichiers déplacés ne constitue pas une mesure d’espace libéré.

Les autres catégories utilisent des diagnostics en lecture seule et un accès aux paramètres de stockage Windows. DISM et VSS peuvent nécessiter des droits administrateur. Discoscope ne déclenche aucune maintenance système destructive.

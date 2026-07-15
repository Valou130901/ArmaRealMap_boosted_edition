# Boosted Edition — Guide

Cette page documente les fonctionnalités exclusives au fork Boosted Edition et les réglages recommandés pour le meilleur résultat en un minimum de temps. *(English: [boosted-edition.md](boosted-edition.md).)*

## Mode île

Le mode île transforme n'importe quelle frontière administrative OSM (canton, district, commune...) en une île entourée d'océan.

### Utilisation

1. Dans l'éditeur de config de carte, coche **Island Mode (experimental)**.
2. Clique **Search...** et choisis la frontière OSM (ex. *« District de la Glâne, Fribourg »*).
   Le centre et la taille de la carte sont réglés automatiquement depuis la bounding box de la frontière (+20 % de marge).
3. Génère normalement.

### Ce qu'il fait

* **Aucune déformation du terrain intérieur** : toute la carte est translatée verticalement pour que le point *le plus bas* du district (0,1ᵉ percentile, pour ignorer les pixels DEM défectueux) soit juste au-dessus du niveau de la mer (+0,5 m). Le relief réel est préservé au 1:1 — rien à l'intérieur n'est plié, aplati ou inondé. Les bords hauts deviennent simplement des falaises ou de longues pentes côtières.
* **Profil de côte hors frontière** : le terrain descend depuis l'altitude du *point de frontière le plus proche* (propagée par une transformée de distance/feature, pas le terrain extérieur brut) jusqu'à un profil de fond marin :
  * Le fond suit une courbe smoothstep atteignant le **fond océanique (-50 m) à ~500 m** de la frontière.
  * La rampe de fondu s'adapte à l'altitude du bord (jusqu'à ~2 km pour les frontières hautes) : les bords bas deviennent des plages, les bords hauts des pentes progressives.
  * Le champ d'altitude du bord est **lissé** pour qu'une frontière vallonnée (collines/vallons alternant le long du bord) ne produise plus de murs verticaux ni de coutures rayonnant depuis la côte.
* **Pas de trous** : toute zone « océan » enfermée dans l'île (artefact de rasterisation ou de géométrie) est retransformée en terre — l'île ne peut jamais contenir de tranchées creusées jusqu'au fond marin.
* **Anti-inondation** : chaque cellule dans la frontière reste ≥ **0,2 m**, appliqué *après* le solveur de contraintes routes/rivières pour que les lits de rivière et routes lissées ne coulent jamais sous l'océan.
* **Rendu du fond marin** :
  * Hors frontière, le masque est forcé en **sable** (plage/fond propre, sans clutter d'algues du sol océanique) ; la bande de côte est redessinée sur le bord. Le land-use OSM ne déborde plus sur le fond marin.
  * L'image satellite est teintée en eau selon la profondeur (turquoise tropical près de la côte, lagon profond au large). Les tuiles Swisstopo « No Data » transparentes sont gérées proprement.

Si tu veux de vraies plages là où la frontière est perchée haut, sculpte-les toi-même ensuite — la côte automatique est un tampon qui ne touche jamais le terrain réel intérieur.

## Réglages recommandés

| Réglage | Recommandation | Pourquoi |
|---|---|---|
| Taille de cellule | **2 à 2,5 m** | En dessous de 2 m : 4× le travail pour quasi aucun gain visuel (le moteur lisse de toute façon). Le détail d'élévation vient des données source, pas de la densité de grille. |
| Taille de grille | Taille carte ÷ taille cellule | ex. carte 8192 m → grille 4096 × 2 m ; carte 20480 m → grille 8192 × 2,5 m |
| Élévation haute-res Swisstopo | **Activée** pour les cartes suisses | D'où vient réellement le détail du terrain. Le premier run télécharge beaucoup de données (mis en cache ensuite). |
| Texture mask multiplier | 2 | Transitions de surfaces plus fines, coût modéré. |
| Résolution satellite | 1 m/pixel | Seule valeur testée. |
| Outil PBO | **Intégré** (Options → Arma 3 → décoche *Use PboProject*) | Optimisé dans ce fork, sans dépendance Mikero. |

## Astuces performances

* **Exclusions Windows Defender** — le plus gros gain gratuit. Binarize et le packaging PBO lisent des milliers de petits fichiers ; le scan temps réel peut doubler la durée. Exclus (PowerShell admin) :
  ```powershell
  Add-MpPreference -ExclusionPath 'P:\'
  Add-MpPreference -ExclusionPath "$env:USERPROFILE\Documents\GameRealisticMap"
  Add-MpPreference -ExclusionPath 'C:\Program Files (x86)\Steam\steamapps\common\Arma 3 Tools'
  ```
  Les exclusions s'appliquent immédiatement, sans redémarrage.
* **Garde P:\ et les caches sur un SSD/NVMe.**
* **Les re-runs sont bien plus rapides** : tuiles satellite, élévation Swisstopo et modèles préparés sont tous en cache.
* **Itère sans binarize** : utilise *Générer un fichier carte pour Arma 3* (WRP seul) pour tester les changements de terrain ; ne build le mod complet que pour les versions finales. Binarize est la seule étape mono-thread que personne ne peut paralléliser (outil BI fermé).

## Ce qui a été corrigé/optimisé vs l'original

| Domaine | Changement |
|---|---|
| Passe d'élévation île | Frontière rasterisée une fois au lieu de tests point-dans-polygone par cellule : heures → secondes sur grilles 8192 |
| Côte île | Profil continu côte→fond marin, pas de tranchée le long de la frontière, fond à -50 m |
| Satellite (mode île) | Teinte eau parallélisée (était mono-thread, semblait figée après les téléchargements), fix alpha pour tuiles No Data |
| Placement d'objets | Bride CPU 75 % retirée |
| Contraintes routes | Pas d'échantillonnage plancher à 0,5 m (le nombre de nœuds explosait sous 2 m de cellule) |
| Compilateur PBO intégré | Préparation modèles parallèle, copies réutilisées entre runs, lecture d'en-tête seule |
| PboFileSystem | Correction d'une race condition dans l'index PBO paresseux |
| Import de cartes | Import jeu/mods (OPRW), couches trouvées via le wrp, paa→png, dé-binarisation rvmat, préfixe custom, filtrage PBO protégés |
| Outils objets | Réduction par type/motif avec presets, bouton supprimer tous les objets |
| SatMap depuis IdMap | Peint avec les vraies textures du jeu + flou gaussien (rendu du satmap GRM natif) |
| Export | Heightmap PNG 16 bits pour WorldPainter/Minecraft |
